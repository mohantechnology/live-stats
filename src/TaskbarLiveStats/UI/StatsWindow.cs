using System.Diagnostics;
using TaskbarLiveStats.Metrics;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;
using TaskbarLiveStats.Taskbar;

namespace TaskbarLiveStats.UI;

/// <summary>
/// Usage overlay: taskbar-docked or free-floating, with per-metric blocks.
/// </summary>
internal sealed class StatsWindow : Form
{
    private readonly StatsRenderer _renderer = new();
    private readonly TaskbarEmbedder _embedder;
    private AppSettings _settings = AppSettings.CreateDefaults();
    private MetricBlock[] _blocks = GroupLayout.FromSnapshot(MetricsSnapshot.Empty, AppSettings.CreateDefaults());
    private bool _barVisible = true;
    private bool _gdiPaint;
    private TaskbarInfo _lastInfo;
    private Size _size = new(420, 40);
    private NativeMethods.RECT _lastTrayRect;
    private NativeMethods.RECT _lastNotifyRect;
    private bool _layoutDirty = true;
    private long _lastFullLayoutTicks;

    private bool _dragging;
    private Point _dragStartCursor;
    private Point _dragStartLocation;
    private int _dragStartOffsetX;
    private bool _suppressClick;

    public bool IsEmbedded => _embedder.IsEmbedded;

    public event Action? SettingsChanged;
    public event Action? HotkeyToggleRequested;

    public IntPtr HandlePublic => IsHandleCreated && NativeMethods.IsWindow(Handle) ? Handle : IntPtr.Zero;

    public StatsWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = _size;
        Location = new Point(-32000, -32000);
        BackColor = ThemeHelper.TaskbarFillColor;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _embedder = new TaskbarEmbedder(IntPtr.Zero)
        {
            DesiredWidth = _size.Width,
            DesiredHeight = _size.Height
        };

        Load += (_, _) => _embedder.Bind(Handle);
        HandleCreated += (_, _) => _embedder.Bind(Handle);

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseClick += OnMouseClick;

        // Keep the WinForms context menu above this WS_EX_TOPMOST overlay.
        ContextMenuStripChanged += (_, _) =>
        {
            if (ContextMenuStrip != null)
                ContextMenuStrip.Opening += EnsureContextMenuTopMost;
        };
    }

    private void EnsureContextMenuTopMost(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenuStrip strip || !strip.IsHandleCreated)
            return;

        NativeMethods.SetWindowPos(
            strip.Handle,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                          | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void ApplySettings(AppSettings settings, bool forceLayout = true)
    {
        bool orientationChanged = _settings.Orientation != settings.Orientation;
        bool placementChanged = _settings.PlacementMode != settings.PlacementMode;
        bool metricsChanged =
            _settings.Metrics.Cpu != settings.Metrics.Cpu ||
            _settings.Metrics.Memory != settings.Metrics.Memory ||
            _settings.Metrics.Gpu != settings.Metrics.Gpu ||
            _settings.Metrics.Vram != settings.Metrics.Vram ||
            _settings.Metrics.NetSend != settings.Metrics.NetSend ||
            _settings.Metrics.NetRecv != settings.Metrics.NetRecv ||
            _settings.Metrics.DiskRead != settings.Metrics.DiskRead ||
            _settings.Metrics.DiskWrite != settings.Metrics.DiskWrite;
        bool rateUnitChanged = _settings.RateUnit != settings.RateUnit;
        bool densityChanged = _settings.Density != settings.Density;
        bool fontChanged =
            Math.Abs(_settings.DefaultValueFontPt - settings.DefaultValueFontPt) > 0.01f ||
            Math.Abs(_settings.DefaultLabelFontPt - settings.DefaultLabelFontPt) > 0.01f ||
            Math.Abs(_settings.CompactValueFontPt - settings.CompactValueFontPt) > 0.01f ||
            Math.Abs(_settings.CompactLabelFontPt - settings.CompactLabelFontPt) > 0.01f;
        _settings = settings.Clone();
        _renderer.ApplyTypography(_settings);
        if (orientationChanged || metricsChanged || rateUnitChanged || densityChanged || fontChanged)
            _renderer.ResetSizeLock();

        _embedder.OffsetX = _settings.TaskbarOffsetX;
        // Overlay stays topmost so it remains above the settings window.
        TopMost = true;

        if (placementChanged || forceLayout)
        {
            if (_settings.PlacementMode == PlacementMode.Floating)
            {
                _embedder.DetachFromTaskbar();
                _layoutDirty = true;
                ApplyFloatingPosition(applyEdge: _settings.EdgePreset != EdgePreset.None);
            }
            else
            {
                _layoutDirty = true;
                if (_lastInfo.TrayHwnd != IntPtr.Zero && _barVisible)
                    ApplyTaskbar(_lastInfo, force: true);
            }
        }
        else if (_settings.PlacementMode == PlacementMode.Floating)
        {
            ApplyFloatingOpacity();
            PaintFrame();
        }

        RemeasureAndPaint();
    }

    public void EnsureHostAlive()
    {
        if (IsHandleCreated && NativeMethods.IsWindow(Handle))
            return;

        try
        {
            if (IsHandleCreated)
                DestroyHandle();
        }
        catch { /* ignore */ }

        CreateHandle();
        _embedder.Bind(Handle);
        _layoutDirty = true;
        InvalidateLayoutCaches();
    }

    public void UpdateMetrics(MetricsSnapshot snapshot)
    {
        _blocks = GroupLayout.FromSnapshot(snapshot, _settings);
        RemeasureAndPaint();
    }

    private void RemeasureAndPaint()
    {
        int thickness = _settings.PlacementMode == PlacementMode.Taskbar && _lastInfo.TrayHwnd != IntPtr.Zero
            ? Math.Max(32, _lastInfo.TrayRect.Height)
            : 40;

        var measured = _renderer.Measure(_blocks, thickness);
        if (measured.Width != _size.Width || measured.Height != _size.Height)
        {
            _size = measured;
            _embedder.DesiredWidth = measured.Width;
            _embedder.DesiredHeight = measured.Height;
            if (_settings.PlacementMode == PlacementMode.Taskbar)
            {
                _embedder.InvalidateLayoutCache();
                _layoutDirty = true;
            }
            else
            {
                SetBounds(Left, Top, measured.Width, measured.Height);
            }
        }

        if (_settings.PlacementMode == PlacementMode.Taskbar)
        {
            if (_layoutDirty && _lastInfo.TrayHwnd != IntPtr.Zero && _barVisible)
                ApplyTaskbar(_lastInfo, force: true);
            else
                PaintFrame();
        }
        else
        {
            PaintFrame();
        }
    }

    public void ApplyTaskbar(TaskbarInfo info) => ApplyTaskbar(info, force: false);

    private void ApplyTaskbar(TaskbarInfo info, bool force)
    {
        if (_settings.PlacementMode != PlacementMode.Taskbar)
            return;

        if (info.TrayHwnd == IntPtr.Zero)
            return;

        bool due = Environment.TickCount64 - _lastFullLayoutTicks > 2000;
        if (!force && !_layoutDirty && !due && _embedder.HasValidLayout && HandlePublic != IntPtr.Zero && SameSlot(info))
        {
            int dx = info.TrayRect.Left - _lastTrayRect.Left;
            int dy = info.TrayRect.Top - _lastTrayRect.Top;
            _lastInfo = info;
            _lastTrayRect = info.TrayRect;
            _lastNotifyRect = info.NotifyRect;
            _embedder.Follow(dx, dy, info.TrayHwnd);
            return;
        }

        _lastInfo = info;
        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero)
            return;

        _lastTrayRect = info.TrayRect;
        _lastNotifyRect = info.NotifyRect;

        int barHeight = Math.Max(32, info.TrayRect.Height);
        if (_settings.Orientation == OverlayOrientation.Vertical)
            barHeight = 40;

        var measured = _renderer.Measure(_blocks, barHeight);
        _size = measured;
        _embedder.DesiredWidth = measured.Width;
        _embedder.DesiredHeight = measured.Height;
        _embedder.OffsetX = _settings.TaskbarOffsetX;
        _embedder.AttachOrReposition(info);

        if (_embedder.HasValidLayout && !_embedder.IsEmbedded)
        {
            var (x, y, w, h) = _embedder.GetClientBounds();
            if (Left != x || Top != y || Width != w || Height != h)
                SetBounds(x, y, w, h);
        }

        _layoutDirty = false;
        _lastFullLayoutTicks = Environment.TickCount64;
        PaintFrame();
    }

    public void SetVisible(bool visible)
    {
        // Floating mode ignores taskbar auto-hide.
        if (_settings.PlacementMode == PlacementMode.Floating)
        {
            _barVisible = true;
            EnsureHostAlive();
            if (HandlePublic != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                PaintFrame();
            }
            return;
        }

        if (!visible && _embedder.IsEmbedded)
            return;

        _barVisible = visible;

        if (!visible)
        {
            var hwnd = HandlePublic;
            if (hwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            _embedder.SetVisible(false);
            return;
        }

        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero) return;

        _embedder.SetVisible(true);
        _layoutDirty = true;
        if (_lastInfo.TrayHwnd != IntPtr.Zero)
            ApplyTaskbar(_lastInfo, force: true);
        else
            PaintFrame();
    }

    public void Reembed(TaskbarInfo info)
    {
        if (_settings.PlacementMode != PlacementMode.Taskbar)
            return;

        _lastInfo = info;
        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero) return;
        _layoutDirty = true;
        InvalidateLayoutCaches();
        _embedder.Reembed(info);
        PaintFrame();
    }

    public void WritePositionTo(AppSettings target)
    {
        target.FloatingX = _settings.FloatingX;
        target.FloatingY = _settings.FloatingY;
        target.TaskbarOffsetX = _settings.TaskbarOffsetX;
        target.EdgePreset = _settings.EdgePreset;
    }

    public void ResetFloatingPosition()
    {
        _settings.FloatingX = int.MinValue;
        _settings.FloatingY = int.MinValue;
        _settings.EdgePreset = EdgePreset.None;
        _settings.TaskbarOffsetX = 0;
        _embedder.OffsetX = 0;
        if (_settings.PlacementMode == PlacementMode.Floating)
            ApplyFloatingPosition(applyEdge: false);
        else if (_lastInfo.TrayHwnd != IntPtr.Zero)
            ApplyTaskbar(_lastInfo, force: true);
        SettingsChanged?.Invoke();
    }

    private void ApplyFloatingPosition(bool applyEdge)
    {
        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero) return;

        int thickness = 40;
        var measured = _renderer.Measure(_blocks, thickness);
        _size = measured;

        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int x, y;

        if (applyEdge && _settings.EdgePreset != EdgePreset.None)
        {
            (x, y) = EdgeToPoint(_settings.EdgePreset, wa, measured);
            _settings.FloatingX = x;
            _settings.FloatingY = y;
        }
        else if (_settings.FloatingX != int.MinValue && _settings.FloatingY != int.MinValue)
        {
            x = _settings.FloatingX;
            y = _settings.FloatingY;
            if (!IsOnAnyScreen(x, y, measured.Width, measured.Height))
            {
                x = wa.Left + 24;
                y = wa.Top + 24;
                _settings.FloatingX = x;
                _settings.FloatingY = y;
            }
        }
        else
        {
            x = wa.Right - measured.Width - 24;
            y = wa.Bottom - measured.Height - 24;
            _settings.FloatingX = x;
            _settings.FloatingY = y;
        }

        SetBounds(x, y, measured.Width, measured.Height);
        // Stay topmost so the overlay remains above the settings window.
        TopMost = true;
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
        RaiseFloatingZOrder();
        ApplyFloatingOpacity();
        PaintFrame();
    }

    private void ApplyFloatingOpacity()
    {
        if (_settings.PlacementMode != PlacementMode.Floating || HandlePublic == IntPtr.Zero)
            return;

        // Opacity is applied via UpdateLayeredWindow SourceConstantAlpha on paint.
    }

    private byte FloatingAlpha =>
        _settings.PlacementMode == PlacementMode.Floating
            ? (byte)Math.Clamp((int)Math.Round(_settings.Opacity * 255), 128, 255)
            : (byte)255;

    private static (int X, int Y) EdgeToPoint(EdgePreset edge, Rectangle wa, Size size)
    {
        const int margin = 12;
        return edge switch
        {
            EdgePreset.Left => (wa.Left + margin, wa.Top + (wa.Height - size.Height) / 2),
            EdgePreset.Right => (wa.Right - size.Width - margin, wa.Top + (wa.Height - size.Height) / 2),
            EdgePreset.Top => (wa.Left + (wa.Width - size.Width) / 2, wa.Top + margin),
            EdgePreset.Bottom => (wa.Left + (wa.Width - size.Width) / 2, wa.Bottom - size.Height - margin),
            _ => (wa.Left + margin, wa.Top + margin)
        };
    }

    private static bool IsOnAnyScreen(int x, int y, int w, int h)
    {
        var center = new Point(x + w / 2, y + h / 2);
        foreach (Screen s in Screen.AllScreens)
        {
            if (s.Bounds.Contains(center))
                return true;
        }
        return false;
    }

    private void RaiseFloatingZOrder()
    {
        if (HandlePublic == IntPtr.Zero) return;
        IntPtr after = _settings.AlwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
        NativeMethods.SetWindowPos(
            Handle, after, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (_settings.PlacementMode == PlacementMode.Taskbar &&
            _embedder is { IsEmbedded: true, HasValidLayout: true })
        {
            var (cx, cy, cw, ch) = _embedder.GetClientBounds();
            if (x != cx || y != cy || width != cw || height != ch)
            {
                base.SetBoundsCore(cx, cy, cw, ch, BoundsSpecified.All);
                return;
            }
        }

        base.SetBoundsCore(x, y, width, height, specified);
    }

    private bool SameSlot(TaskbarInfo info)
    {
        if (info.TrayHwnd != _lastInfo.TrayHwnd)
            return false;
        if (info.TrayRect.Width != _lastTrayRect.Width || info.TrayRect.Height != _lastTrayRect.Height)
            return false;

        int relX = info.NotifyRect.Left - info.TrayRect.Left;
        int relY = info.NotifyRect.Top - info.TrayRect.Top;
        int lastX = _lastNotifyRect.Left - _lastTrayRect.Left;
        int lastY = _lastNotifyRect.Top - _lastTrayRect.Top;
        return relX == lastX && relY == lastY;
    }

    private void InvalidateLayoutCaches()
    {
        _lastTrayRect = default;
        _lastNotifyRect = default;
        _embedder.InvalidateLayoutCache();
    }

    private void PaintFrame()
    {
        if (!_barVisible || HandlePublic == IntPtr.Zero) return;

        if (_settings.PlacementMode == PlacementMode.Taskbar && !_embedder.HasValidLayout)
            return;

        if (_settings.PlacementMode == PlacementMode.Taskbar && _embedder.IsEmbedded)
        {
            if (_embedder.IsLayered)
                _embedder.UseGdiPaint();
            _gdiPaint = true;
            Invalidate();
            Update();
            _embedder.RaiseZOrder();
            return;
        }

        if (_gdiPaint || (_settings.PlacementMode == PlacementMode.Taskbar && !_embedder.IsLayered))
        {
            Invalidate();
            return;
        }

        int x = Left;
        int y = Top;
        if (_settings.PlacementMode == PlacementMode.Taskbar)
            (x, y) = _embedder.GetLayeredDestPos();

        bool ok = _renderer.PaintLayered(Handle, _blocks, _size.Width, _size.Height, x, y, asChild: false, FloatingAlpha);
        if (ok)
        {
            if (_settings.PlacementMode == PlacementMode.Taskbar)
                _embedder.RaiseZOrder();
            else
                RaiseFloatingZOrder();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_gdiPaint && (_settings.PlacementMode == PlacementMode.Floating || _embedder.IsLayered))
            return;
        e.Graphics.Clear(ThemeHelper.TaskbarFillColor);
        _renderer.PaintGdi(e.Graphics, _blocks, ClientSize.Width, ClientSize.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_gdiPaint || (_settings.PlacementMode == PlacementMode.Taskbar && !_embedder.IsLayered))
            e.Graphics.Clear(ThemeHelper.TaskbarFillColor);
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _suppressClick = false;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        _dragStartOffsetX = _settings.TaskbarOffsetX;
        Capture = true;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var cur = Cursor.Position;
        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;

        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)
            _suppressClick = true;

        if (_settings.PlacementMode == PlacementMode.Taskbar)
        {
            // Drag along the taskbar; Y stays locked to the slot.
            // Keep a generous but finite range so a bad offset can't push the bar off-screen.
            _settings.TaskbarOffsetX = Math.Clamp(_dragStartOffsetX + dx, -800, 800);
            _embedder.OffsetX = _settings.TaskbarOffsetX;
            _layoutDirty = true;
            if (_lastInfo.TrayHwnd != IntPtr.Zero)
                ApplyTaskbar(_lastInfo, force: true);
        }
        else
        {
            int nx = _dragStartLocation.X + dx;
            int ny = _dragStartLocation.Y + dy;
            SetBounds(nx, ny, Width, Height);
            PaintFrame();
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Capture = false;

        if (!_suppressClick) return;

        if (_settings.PlacementMode == PlacementMode.Floating)
        {
            ClampToVirtualScreen();
            _settings.FloatingX = Left;
            _settings.FloatingY = Top;
            _settings.EdgePreset = EdgePreset.None;
        }

        SettingsChanged?.Invoke();
        PaintFrame();
    }

    private void ClampToVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        int x = Math.Clamp(Left, vs.Left, vs.Right - Width);
        int y = Math.Clamp(Top, vs.Top, vs.Bottom - Height);
        if (x != Left || y != Top)
            SetBounds(x, y, Width, Height);
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        // Right-click uses ContextMenuStrip (kept topmost via Opening handler).
        if (e.Button == MouseButtons.Left && !_suppressClick)
            OpenTaskManager();
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

    protected override void WndProc(ref Message m)
    {
        if (HotkeyHook.IsHotkeyMessage(m))
        {
            HotkeyToggleRequested?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _embedder.Dispose();
            _renderer.Dispose();
        }

        base.Dispose(disposing);
    }
}
