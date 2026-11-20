using System.Diagnostics;
using TaskbarLiveStats.Metrics;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;
using TaskbarLiveStats.Taskbar;
using TaskbarLiveStats.UI;

namespace TaskbarLiveStats;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly StatsWindow _window;
    private readonly MetricsHub _metrics;
    private readonly TaskbarWatcher _watcher;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _showHideItem;
    private readonly SettingsStore _store = new();
    private readonly HotkeyHook _hotkey = new();
    private AppSettings _settings;
    private SettingsForm? _settingsForm;
    private bool _userHidden;
    private bool _fullscreenActive;
    private readonly System.Windows.Forms.Timer _fullscreenTimer;

    public TrayAppContext(bool openSettingsOnStart = false, bool startedFromAutostart = false)
    {
        _settings = _store.Load(out bool isFirstRun);
        if (isFirstRun)
        {
            _settings.StartWithWindows = true;
            StartupHelper.SetEnabled(true);
            _store.Save(_settings);
        }
        else
        {
            StartupHelper.SetEnabled(_settings.StartWithWindows);
        }

        _window = new StatsWindow();
        _window.Show();
        _window.ApplySettings(_settings, forceLayout: true);

        _metrics = new MetricsHub(_settings.UpdateIntervalMs);
        _metrics.SetHistoryRecording(_settings.SaveNetworkHistory, _settings.SaveDiskHistory, _settings.SaveComputerUseHistory);
        _watcher = new TaskbarWatcher(200);
        _fullscreenTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _fullscreenTimer.Tick += (_, _) => RefreshFullscreenState();

        _startupItem = new ToolStripMenuItem("Autostart when Windows boots")
        {
            Checked = StartupHelper.IsEnabled(),
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = _startupItem.Checked;
            StartupHelper.SetEnabled(_startupItem.Checked);
            Persist();
        };

        _showHideItem = new ToolStripMenuItem("Hide live stats")
        {
            ShortcutKeyDisplayString = HotkeyHook.Format(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey)
        };
        _showHideItem.Click += (_, _) => ToggleOverlayVisibility();
        _hotkey.Pressed += ToggleOverlayVisibility;
        _hotkey.Apply(_settings);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        _menu.Items.Add(_showHideItem);
        _menu.Items.Add("Open Task Manager", null, (_, _) => OpenTaskManager());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Text = "Taskbar Live Stats",
            Icon = AppIcons.AppIcon,
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenSettings();
        };

        _window.ContextMenuStrip = _menu;
        _menu.Opening += (_, _) =>
        {
            if (_menu.IsHandleCreated)
            {
                NativeMethods.SetWindowPos(
                    _menu.Handle,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            _showHideItem.Text = _userHidden ? "Show live stats" : "Hide live stats";
            _showHideItem.ShortcutKeyDisplayString =
                HotkeyHook.Format(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        };

        _window.SettingsChanged += () =>
        {
            _window.WritePositionTo(_settings);
            Persist();
        };

        _metrics.Updated += snap =>
        {
            if (_window.HandlePublic == IntPtr.Zero) return;
            try
            {
                _tray.Text = TruncateTip(GroupLayout.Tooltip(snap, _settings));
                if (_window.IsOverlayShown)
                    _window.UpdateMetrics(snap);
            }
            catch { /* ignore */ }
        };

        _watcher.VisibilityChanged += _ =>
        {
            if (_userHidden)
                return;
            SyncOverlayVisibility();
        };

        _watcher.ExplorerRestarted += () =>
        {
            if (_settings.PlacementMode != PlacementMode.Taskbar)
                return;
            _window.EnsureHostAlive();
            _hotkey.Apply(_settings);
            if (_userHidden)
            {
                _window.SetVisible(false);
                return;
            }
            SyncOverlayVisibility();
            if (!_fullscreenActive || _settings.ShowOnFullscreen)
                _window.Reembed(_watcher.Current);
        };

        _watcher.Changed += info =>
        {
            if (_userHidden || _settings.PlacementMode != PlacementMode.Taskbar)
                return;
            if (_fullscreenActive && _settings.ShowOnFullscreen)
                return;
            if (info.IsVisible)
                _window.ApplyTaskbar(info);
        };

        _watcher.Start();
        _fullscreenTimer.Start();
        RefreshFullscreenState(force: true);

        // Autostart hide only applies to Windows Run launches, not manual or --settings starts.
        if (startedFromAutostart && !openSettingsOnStart && _settings.HideOnAutostart)
        {
            _userHidden = true;
            HideOverlay();
        }

        if (openSettingsOnStart)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 300 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                OpenSettings();
            };
            timer.Start();
        }
    }

    private bool SettingsOpen => _settingsForm is { IsDisposed: false };

    private void ShowOverlay()
    {
        _window.SetUserSuppressed(false);
        SyncOverlayVisibility();
    }

    private void HideOverlay()
    {
        _window.SetFullscreenFloat(false);
        _window.SetUserSuppressed(true);
        _metrics.Pause();
    }

    /// <summary>Show the bar so a settings change can be seen. Clears a previous shortcut hide.</summary>
    private void RevealOverlay()
    {
        _userHidden = false;
        ShowOverlay();
    }

    private void ToggleOverlayVisibility()
    {
        _userHidden = !_userHidden;
        if (_userHidden)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void RefreshFullscreenState(bool force = false)
    {
        IntPtr overlay = _window.HandlePublic;
        IntPtr settingsHwnd = _settingsForm is { IsDisposed: false, IsHandleCreated: true }
            ? _settingsForm.Handle
            : IntPtr.Zero;
        bool fullscreen = FullscreenDetector.IsFullscreenAppActive(overlay, settingsHwnd);
        if (!force && fullscreen == _fullscreenActive)
            return;

        _fullscreenActive = fullscreen;
        if (_userHidden)
            return;

        SyncOverlayVisibility();
    }

    /// <summary>
    /// Applies taskbar visibility, fullscreen hide, and optional fullscreen float.
    /// Does not clear a user hotkey hide.
    /// </summary>
    private void SyncOverlayVisibility()
    {
        if (_userHidden)
            return;

        _window.SetUserSuppressed(false);

        if (_fullscreenActive && !_settings.ShowOnFullscreen)
        {
            _window.SetFullscreenFloat(false);
            _window.SetVisible(false);
            _metrics.Pause();
            return;
        }

        if (_fullscreenActive && _settings.ShowOnFullscreen)
        {
            _window.SetFullscreenFloat(true);
            _window.SetVisible(true);
            _metrics.Resume();
            return;
        }

        _window.SetFullscreenFloat(false);

        if (_settings.PlacementMode == PlacementMode.Floating)
        {
            _window.SetVisible(true);
            _metrics.Resume();
            return;
        }

        bool taskbarVisible = _watcher.Current.IsVisible;
        _window.SetVisible(taskbarVisible);
        if (taskbarVisible)
        {
            if (_watcher.Current.TrayHwnd != IntPtr.Zero)
                _window.ApplyTaskbar(_watcher.Current);
            _metrics.Resume();
        }
        else
        {
            _metrics.Pause();
        }
    }

    private void OpenSettings()
    {
        if (SettingsOpen)
        {
            RevealOverlay();
            _settingsForm!.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, ApplySettings);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;

        RevealOverlay();
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings.Clone();

        StartupHelper.SetEnabled(_settings.StartWithWindows);
        if (_startupItem.Checked != _settings.StartWithWindows)
            _startupItem.Checked = _settings.StartWithWindows;

        _metrics.IntervalMs = _settings.UpdateIntervalMs;
        _metrics.SetHistoryRecording(_settings.SaveNetworkHistory, _settings.SaveDiskHistory, _settings.SaveComputerUseHistory);
        _hotkey.Apply(_settings);

        _window.ApplySettings(_settings, forceLayout: true);
        _window.WritePositionTo(_settings);
        Persist();

        // Apply and OK bring the bar back so the change is visible.
        _userHidden = false;
        RefreshFullscreenState(force: true);
    }

    private void Persist() => _store.Save(_settings);

    private static void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskmgr.exe",
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    private static string TruncateTip(string text)
    {
        string oneLine = text.Replace('\n', ' ');
        return oneLine.Length <= 63 ? oneLine : oneLine[..60] + "...";
    }

    private void Exit()
    {
        Persist();
        _hotkey.Dispose();
        _fullscreenTimer.Stop();
        _fullscreenTimer.Dispose();
        _watcher.Stop();
        _metrics.Pause();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _metrics.Dispose();
        _watcher.Dispose();
        _window.Dispose();
        _settingsForm?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkey.Dispose();
            _fullscreenTimer.Stop();
            _fullscreenTimer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _metrics.Dispose();
            _watcher.Dispose();
            _window.Dispose();
        }

        base.Dispose(disposing);
    }
}
