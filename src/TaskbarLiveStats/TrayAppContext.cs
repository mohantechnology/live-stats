using System.Diagnostics;
using TaskbarLiveStats.Metrics;
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

    public TrayAppContext()
    {
        _window = new StatsWindow();
        _window.Show();
        _metrics = new MetricsHub(1000);
        _watcher = new TaskbarWatcher(500);

        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupHelper.IsEnabled(),
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += (_, _) => StartupHelper.SetEnabled(_startupItem.Checked);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open Task Manager", null, (_, _) => OpenTaskManager());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Text = "Taskbar Live Stats",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (_, _) => OpenTaskManager();

        _window.ContextMenuRequested += () => _menu.Show(Cursor.Position);

        _metrics.Updated += snap =>
        {
            if (_window.HandlePublic == IntPtr.Zero) return;
            try
            {
                _tray.Text = TruncateTip(GroupLayout.Tooltip(snap));
                _window.UpdateMetrics(snap);
            }
            catch { /* ignore */ }
        };

        _watcher.VisibilityChanged += visible =>
        {
            // Metrics follow auto-hide; the hosted child rides with Shell_TrayWnd visually.
            _window.SetVisible(visible);
            if (visible) _metrics.Resume();
            else _metrics.Pause();
        };

        _watcher.ExplorerRestarted += () =>
        {
            _window.EnsureHostAlive();
            _window.Reembed(_watcher.Current);
        };

        _watcher.Changed += info =>
        {
            // Only layout against a fully shown taskbar — thin auto-hide strips break slot math.
            if (info.IsVisible)
                _window.ApplyTaskbar(info);
        };

        _watcher.Start();

        if (_watcher.Current.IsVisible)
        {
            _window.SetVisible(true);
            _window.ApplyTaskbar(_watcher.Current);
            _metrics.Resume();
        }
        else
        {
            _window.SetVisible(false);
            _metrics.Pause();
        }
    }

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
        _watcher.Stop();
        _metrics.Pause();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _metrics.Dispose();
        _watcher.Dispose();
        _window.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _metrics.Dispose();
            _watcher.Dispose();
            _window.Dispose();
        }

        base.Dispose(disposing);
    }
}
