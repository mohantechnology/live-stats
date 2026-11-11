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
    private OverlayFrame _frame = OverlayFrame.Empty;
    private MetricsSnapshot _lastSnapshot = MetricsSnapshot.Empty;
    private readonly Dictionary<MetricKind, IdleWatch> _idleWatches = new();
    private bool _barVisible = true;
    private bool _suppressed;
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
    private TitleTip? _hoverTip;
    private string? _hoverTitle;

    public bool IsEmbedded => _embedder.IsEmbedded;

    public bool IsOverlayShown => _barVisible && !_suppressed;

    public event Action? SettingsChanged;

    public IntPtr HandlePublic => IsHandleCreated && NativeMethods.IsWindow(Handle) ? Handle : IntPtr.Zero;

    public StatsWindow()
    {
        AutoScaleMode = AutoScaleMode.None;
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
        MouseLeave += (_, _) => ClearHoverTip();

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
            _settings.Metrics.DiskWrite != settings.Metrics.DiskWrite ||
            _settings.Metrics.Npu != settings.Metrics.Npu ||
            _settings.Metrics.NpuMemory != settings.Metrics.NpuMemory ||
            !_settings.HideWhenIdle.SameAs(settings.HideWhenIdle) ||
            !(_settings.TotalUsage ?? new TotalUsageToggles()).SameAs(settings.TotalUsage ?? new TotalUsageToggles()) ||
            _settings.TotalSince != settings.TotalSince ||
            _settings.ShowUsageDetails != settings.ShowUsageDetails ||
            _settings.ShowLabelsCollapsed != settings.ShowLabelsCollapsed ||
            _settings.ShowLabelsExpanded != settings.ShowLabelsExpanded ||
            _settings.UsageDetailCount != settings.UsageDetailCount;
        bool hideIdleChanged = !_settings.HideWhenIdle.SameAs(settings.HideWhenIdle);
        bool rateUnitChanged = _settings.RateUnit != settings.RateUnit;
        bool densityChanged = _settings.Density != settings.Density;
        bool fontChanged =
            Math.Abs(_settings.DefaultValueFontPt - settings.DefaultValueFontPt) > 0.01f ||
            Math.Abs(_settings.DefaultLabelFontPt - settings.DefaultLabelFontPt) > 0.01f ||
            Math.Abs(_settings.CompactValueFontPt - settings.CompactValueFontPt) > 0.01f ||
            Math.Abs(_settings.CompactLabelFontPt - settings.CompactLabelFontPt) > 0.01f ||
            Math.Abs(_settings.DefaultDetailFontPt - settings.DefaultDetailFontPt) > 0.01f ||
            Math.Abs(_settings.CompactDetailFontPt - settings.CompactDetailFontPt) > 0.01f;
        _settings = settings.Clone();
        if (hideIdleChanged)
            _idleWatches.Clear();
        _renderer.ApplyTypography(_settings);
        BackColor = _renderer.FillColor;
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
                if (_lastInfo.TrayHwnd != IntPtr.Zero && _barVisible && !_suppressed)
                    ApplyTaskbar(_lastInfo, force: true);
            }
        }
        else if (_settings.PlacementMode == PlacementMode.Floating)
        {
            ApplyFloatingOpacity();
            PaintFrame();
        }

        RebuildBlocks(advanceIdleStreak: false);
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
        _lastSnapshot = snapshot;
        RebuildBlocks(advanceIdleStreak: true);
    }

    private void RebuildBlocks(bool advanceIdleStreak)
    {
        _frame = GroupLayout.FromSnapshot(_lastSnapshot, _settings, _idleWatches, advanceIdleStreak);
        RemeasureAndPaint();
    }

    private void RemeasureAndPaint()
    {
        int thickness;
        if (_settings.PlacementMode == PlacementMode.Taskbar && _lastInfo.TrayHwnd != IntPtr.Zero)
        {
            thickness = _settings.Orientation == OverlayOrientation.Vertical
                ? Math.Max(24, Math.Min(_lastInfo.TrayRect.Width, _lastInfo.TrayRect.Height))
                : Math.Max(32, _lastInfo.TrayRect.Height);
        }
        else
        {
            thickness = _settings.Orientation == OverlayOrientation.Vertical ? 24 : 40;
        }

        SyncDetailAnchor();
        var measured = _renderer.Measure(_frame, thickness);
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
            else if (_barVisible && !_suppressed)
            {
                SetBounds(Left, Top, measured.Width, measured.Height);
            }
        }

        if (_suppressed || !_barVisible)
            return;

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
        if (_suppressed || !_barVisible)
            return;

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

        // Preferred thickness follows the tray when horizontal; vertical stacks size from content.
        int barHeight = _settings.Orientation == OverlayOrientation.Vertical
            ? Math.Max(24, Math.Min(info.TrayRect.Width, info.TrayRect.Height))
            : Math.Max(32, info.TrayRect.Height);

        SyncDetailAnchor();
        var measured = _renderer.Measure(_frame, barHeight);
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

    /// <summary>
    /// User show/hide. While suppressed, taskbar auto-hide and layout cannot bring the overlay back.
    /// </summary>
    public void SetUserSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        if (!suppressed)
            return;

        _embedder.ReleaseSlot();
        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        if (_suppressed)
            visible = false;

        _barVisible = visible;
        _embedder.SetVisible(visible);

        if (!visible)
        {
            ClearHoverTip();
            HideNative();
            return;
        }

        ShowNative();
    }

    private void HideNative()
    {
        IntPtr hwnd = HandlePublic;
        if (hwnd == IntPtr.Zero)
            return;

        // Layered sprites can survive SW_HIDE. Clear the pixels first so a failed
        // hide still leaves nothing on screen, then hide the window.
        try
        {
            _renderer.PaintLayered(
                hwnd,
                _frame,
                Math.Max(1, _size.Width),
                Math.Max(1, _size.Height),
                Left,
                Top,
                asChild: false,
                alpha: 0);
        }
        catch
        {
            // Still hide below.
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);

        if (Visible)
            Visible = false;
    }

    private void ShowNative()
    {
        EnsureHostAlive();
        if (HandlePublic == IntPtr.Zero)
            return;

        if (!Visible)
            Visible = true;

        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

        if (_settings.PlacementMode == PlacementMode.Floating)
        {
            ApplyFloatingPosition(applyEdge: false);
            return;
        }

        _layoutDirty = true;
        if (_lastInfo.TrayHwnd != IntPtr.Zero)
            ApplyTaskbar(_lastInfo, force: true);
        else
            PaintFrame();
    }

    public void Reembed(TaskbarInfo info)
    {
        if (_suppressed || _settings.PlacementMode != PlacementMode.Taskbar)
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
        target.ShowUsageDetails = _settings.ShowUsageDetails;
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
        SyncDetailAnchor();
        var measured = _renderer.Measure(_frame, thickness);
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
        if (_suppressed || !_barVisible)
        {
            HideNative();
            return;
        }

        // Stay topmost so the overlay remains above the settings window.
        TopMost = true;
        if (!Visible)
            Visible = true;
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
        var window = new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
        int keep = Math.Min(MinVisiblePx, Math.Min(window.Width, window.Height));
        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle hit = Rectangle.Intersect(s.Bounds, window);
            // A parked off-edge strip still counts if enough of it can be grabbed.
            if (hit.Width >= keep && hit.Height >= keep)
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
        if (_suppressed || !_barVisible || HandlePublic == IntPtr.Zero) return;

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
            _hoverTip?.KeepOnTop();
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

        bool ok = _renderer.PaintLayered(Handle, _frame, _size.Width, _size.Height, x, y, asChild: false, FloatingAlpha);
        if (ok)
        {
            if (_settings.PlacementMode == PlacementMode.Taskbar)
                _embedder.RaiseZOrder();
            else
                RaiseFloatingZOrder();
            _hoverTip?.KeepOnTop();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_gdiPaint && (_settings.PlacementMode == PlacementMode.Floating || _embedder.IsLayered))
            return;
        e.Graphics.Clear(_renderer.FillColor);
        _renderer.PaintGdi(e.Graphics, _frame, ClientSize.Width, ClientSize.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_gdiPaint || (_settings.PlacementMode == PlacementMode.Taskbar && !_embedder.IsLayered))
            e.Graphics.Clear(_renderer.FillColor);
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
        if (!_dragging)
        {
            UpdateHoverTip(e.Location);
            return;
        }

        ClearHoverTip();

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

    /// <summary>How many pixels must stay on a screen so the overlay can still be grabbed.</summary>
    private const int MinVisiblePx = 10;

    private void ClampToVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        int keep = Math.Min(MinVisiblePx, Math.Min(Math.Max(1, Width), Math.Max(1, Height)));
        // Allow hanging off any edge; only keep a grab strip inside the virtual desktop.
        int x = FitOnScreen(Left, vs.Left - Width + keep, vs.Right - keep);
        int y = FitOnScreen(Top, vs.Top - Height + keep, vs.Bottom - keep);
        if (x != Left || y != Top)
            SetBounds(x, y, Width, Height);
    }

    private static int FitOnScreen(int value, int min, int max)
    {
        if (max < min)
            return min;
        return Math.Clamp(value, min, max);
    }

    private void UpdateHoverTip(Point client)
    {
        string? title = _renderer.TitleAt(client);
        if (string.Equals(title, _hoverTitle, StringComparison.Ordinal))
            return;

        _hoverTitle = title;
        if (string.IsNullOrEmpty(title))
        {
            _hoverTip?.Hide();
            return;
        }

        _hoverTip ??= new TitleTip();
        _hoverTip.Present(title, PointToScreen(client));
    }

    private void ClearHoverTip()
    {
        if (_hoverTitle == null)
            return;

        _hoverTitle = null;
        _hoverTip?.Hide();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        // Right-click uses ContextMenuStrip, which already opens Task Manager.
        if (e.Button == MouseButtons.Left && !_suppressClick)
            ToggleUsageDetails();
    }

    private void SyncDetailAnchor()
    {
        _renderer.DetailAnchor = DetailAnchor.Center;
        if (FrameHasDetails(_frame))
            return;
        if (_settings.PlacementMode != PlacementMode.Taskbar || _lastInfo.TrayHwnd == IntPtr.Zero)
            return;

        bool horizontal = _lastInfo.TrayRect.Width >= _lastInfo.TrayRect.Height;
        if (!horizontal)
            return;

        var screen = SystemInformation.VirtualScreen;
        if (_lastInfo.TrayRect.Bottom >= screen.Bottom - 16)
            _renderer.DetailAnchor = DetailAnchor.Bottom;
        else if (_lastInfo.TrayRect.Top <= screen.Top + 16)
            _renderer.DetailAnchor = DetailAnchor.Top;
    }

    private static bool FrameHasDetails(OverlayFrame frame)
    {
        foreach (MetricBlock block in frame.Main)
        {
            if (block.Details != null)
                return true;
        }

        return false;
    }

    private void ToggleUsageDetails()
    {
        _settings.ShowUsageDetails = !_settings.ShowUsageDetails;
        _renderer.ApplyTypography(_settings);
        _layoutDirty = true;
        RebuildBlocks(advanceIdleStreak: false);
        SettingsChanged?.Invoke();
    }

    protected override void SetVisibleCore(bool value)
    {
        if ((_suppressed || !_barVisible) && value)
            value = false;
        base.SetVisibleCore(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTip?.Dispose();
            _embedder.Dispose();
            _renderer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Full label shown when the painted text is cut off. Stays above the topmost bar.</summary>
    private sealed class TitleTip : Form
    {
        private readonly Label _text;
        private readonly Font _font = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        public TitleTip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 32);
            _text = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(480, 0),
                ForeColor = Color.FromArgb(245, 245, 245),
                BackColor = Color.FromArgb(28, 28, 32),
                Font = _font,
                Location = new Point(8, 4)
            };
            Controls.Add(_text);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }

        public void Present(string title, Point screen)
        {
            _text.Text = title;
            Size pref = _text.PreferredSize;
            ClientSize = new Size(Math.Max(12, pref.Width) + 16, Math.Max(12, pref.Height) + 8);

            var loc = new Point(screen.X + 16, screen.Y + 18);
            Rectangle vs = SystemInformation.VirtualScreen;
            if (loc.X + Width > vs.Right)
                loc.X = Math.Max(vs.Left, screen.X - Width - 8);
            if (loc.Y + Height > vs.Bottom)
                loc.Y = Math.Max(vs.Top, screen.Y - Height - 8);
            Location = loc;

            if (!Visible)
                Show();
            KeepOnTop();
        }

        public void KeepOnTop()
        {
            if (!IsHandleCreated || !Visible)
                return;

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
