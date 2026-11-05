using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

/// <summary>
/// Places the stats bar in the taskbar slot and keeps it owned by Shell_TrayWnd
/// so it stays directly above the bar and slides with auto-hide.
/// Win11 draws icons in a XAML island. Parenting a window into the tray is not
/// visible (GDI is covered; a layered child sprite draws at the wrong place),
/// and there is no public API to push those icons the way the Widgets button does.
/// </summary>
internal sealed class TaskbarEmbedder : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _tray;
    private readonly TaskListShrinker _shrinker = new();
    private readonly TaskbarLayoutProbe _probe = new();
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastW;
    private int _lastH;
    private bool _wantVisible = true;
    private bool _embedded;
    private bool _layered = true;

    public int DesiredWidth { get; set; } = 420;
    public int DesiredHeight { get; set; } = 36;
    public int Gap { get; set; } = 4;

    /// <summary>User drag offset applied after automatic slot placement (taskbar mode).</summary>
    public int OffsetX { get; set; }

    public bool IsEmbedded => _embedded && _tray != IntPtr.Zero && NativeMethods.GetParent(_hwnd) == _tray;

    public bool IsLayered => _layered;

    public TaskbarEmbedder(IntPtr hwnd) => _hwnd = hwnd;

    public void Bind(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _embedded = false;
        _layered = true;
        InvalidateLayoutCache();
    }

    public void AttachOrReposition(TaskbarInfo info)
    {
        if (info.TrayHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
            return;

        _tray = info.TrayHwnd;
        // Child embedding is not visible on this taskbar: GDI is covered by the
        // XAML island, and a layered child sprite draws at the wrong place.
        _embedded = false;

        var notify = info.NotifyRect.Width > 8 ? info.NotifyRect : info.TrayRect;
        _probe.Refresh(info.TrayHwnd);

        if (!_probe.TryComputeSlot(info.TrayRect, info.NotifyRect, DesiredWidth, Gap,
                out int screenX, out int screenY, out int width, out int height))
            return;

        bool afterWidgets = _probe.IsAfterWidgetsPlacement(info.TrayRect, info.NotifyRect, DesiredWidth, Gap);
        if (!afterWidgets)
        {
            _shrinker.ReserveLeftOfNotify(
                info.TrayHwnd,
                info.TrayRect,
                notify.Width > 8 ? notify : (_probe.NotifyRect ?? notify),
                width,
                Gap);
        }
        else
        {
            _shrinker.Restore();
        }

        screenX += OffsetX;

        bool horizontalBar = info.TrayRect.Width >= info.TrayRect.Height;
        if (horizontalBar && DesiredHeight > height)
        {
            var screen = SystemInformation.VirtualScreen;
            if (info.TrayRect.Bottom >= screen.Bottom - 16)
                screenY -= DesiredHeight - height;
            height = DesiredHeight;
        }

        if (_embedded)
            PositionChild(screenX, screenY, width, height);
        else
            PositionOverlay(screenX, screenY, width, height);
    }

    /// <summary>Release task-list shrink when leaving taskbar dock mode.</summary>
    public void DetachFromTaskbar()
    {
        _shrinker.Restore();
        InvalidateLayoutCache();
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            // Clear tray ownership so floating mode is independent.
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
        }

        _tray = IntPtr.Zero;
        _embedded = false;
    }

    public void Reembed(TaskbarInfo info)
    {
        _shrinker.Restore();
        _embedded = false;
        InvalidateLayoutCache();
        if (_wantVisible)
            AttachOrReposition(info);
    }

    /// <summary>Translate a top-level overlay with the tray. Embedded children move with the parent.</summary>
    public void Follow(int dx, int dy, IntPtr tray)
    {
        if (tray != IntPtr.Zero)
            _tray = tray;

        if (IsEmbedded || _embedded)
        {
            RaiseZOrder();
            return;
        }

        EnsureOverlayOwner(_tray);
        if (!_wantVisible || !HasValidLayout)
        {
            KeepAboveOverlay();
            return;
        }

        if (dx != 0 || dy != 0)
        {
            _lastX += dx;
            _lastY += dy;
            NativeMethods.SetWindowPos(
                _hwnd,
                InsertAfterOverlay(),
                _lastX, _lastY, _lastW, _lastH,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOCOPYBITS | NativeMethods.SWP_SHOWWINDOW);
        }
        else
        {
            KeepAboveOverlay();
        }
    }

    /// <summary>Give the reserved taskbar slot back to the icon band.</summary>
    public void ReleaseSlot() => _shrinker.Restore();

    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
            return;

        // A taskbar child is clipped and moved by Shell_TrayWnd. Hiding it
        // ourselves lags behind the auto-hide animation.
        if (!visible && IsEmbedded)
            return;

        NativeMethods.ShowWindow(_hwnd, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);
        if (visible)
            InvalidateLayoutCache();
    }

    public (int X, int Y) GetLayeredDestPos()
        => (_lastX == int.MinValue ? 0 : _lastX, _lastY == int.MinValue ? 0 : _lastY);

    public (int X, int Y, int W, int H) GetClientBounds()
        => (_lastX == int.MinValue ? 0 : _lastX, _lastY == int.MinValue ? 0 : _lastY, _lastW, _lastH);

    public bool HasValidLayout => _lastX != int.MinValue && _lastW > 0 && _lastH > 0;

    public void InvalidateLayoutCache()
    {
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = 0;
        _lastH = 0;
    }

    public void UseGdiPaint()
    {
        if (!_layered || _hwnd == IntPtr.Zero)
            return;

        int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex &= ~NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        _layered = false;
        if (HasValidLayout)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOP,
                _lastX, _lastY, _lastW, _lastH,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    public void RaiseZOrder()
    {
        if (_hwnd == IntPtr.Zero || !_wantVisible || !NativeMethods.IsWindow(_hwnd))
            return;

        if (IsEmbedded)
        {
            // Already the top sibling — don't restack (that flickers).
            if (NativeMethods.GetWindow(_hwnd, NativeMethods.GW_HWNDPREV) == IntPtr.Zero)
                return;

            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOP,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            return;
        }

        KeepAboveOverlay();
    }

    public void Dispose()
    {
        _probe.Dispose();
        _shrinker.Dispose();
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            NativeMethods.SetParent(_hwnd, IntPtr.Zero);
    }

    private bool TryEmbed(IntPtr tray)
    {
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        int desired = (style | NativeMethods.WS_CHILD | NativeMethods.WS_CLIPSIBLINGS) &
                      ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME);
        if (_wantVisible)
            desired |= NativeMethods.WS_VISIBLE;
        else
            desired &= ~NativeMethods.WS_VISIBLE;

        if (style != desired)
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, desired);

        int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        int desiredEx = ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        if (_layered)
            desiredEx |= NativeMethods.WS_EX_LAYERED;
        else
            desiredEx &= ~NativeMethods.WS_EX_LAYERED;
        desiredEx &= ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOPMOST);
        if (ex != desiredEx)
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, desiredEx);

        if (NativeMethods.GetParent(_hwnd) != tray)
        {
            NativeMethods.SetParent(_hwnd, tray);
            if (NativeMethods.GetParent(_hwnd) != tray)
                return false;
        }

        return true;
    }

    private void PositionChild(int screenX, int screenY, int width, int height)
    {
        var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
        NativeMethods.ScreenToClient(_tray, ref pt);

        if (NativeMethods.GetClientRect(_tray, out var client) && client.Height > 0)
        {
            if (pt.Y < 0) pt.Y = 0;
            int room = client.Height - pt.Y;
            if (room > 0 && height > room)
                height = room;
        }

        Place(pt.X, pt.Y, width, height, NativeMethods.HWND_TOP);
    }

    private void PositionOverlay(int screenX, int screenY, int width, int height)
    {
        EnsureOverlayStyles();
        EnsureOverlayOwner(_tray);
        Place(screenX, screenY, width, height, InsertAfterOverlay());
    }

    private void Place(int x, int y, int width, int height, IntPtr insertAfter)
    {
        bool unchanged = x == _lastX && y == _lastY && width == _lastW && height == _lastH;
        _lastX = x;
        _lastY = y;
        _lastW = width;
        _lastH = height;

        if (unchanged)
        {
            RaiseZOrder();
            return;
        }

        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOCOPYBITS | NativeMethods.SWP_FRAMECHANGED;
        flags |= _wantVisible ? NativeMethods.SWP_SHOWWINDOW : NativeMethods.SWP_HIDEWINDOW;

        NativeMethods.SetWindowPos(_hwnd, insertAfter, x, y, width, height, flags);
    }

    private void EnsureOverlayStyles()
    {
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style = (style | NativeMethods.WS_POPUP) & ~(NativeMethods.WS_CHILD | NativeMethods.WS_CLIPCHILDREN);
        if (_wantVisible)
            style |= NativeMethods.WS_VISIBLE;
        else
            style &= ~NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);

        int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
        if (_layered)
            ex |= NativeMethods.WS_EX_LAYERED;
        ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private void EnsureOverlayOwner(IntPtr tray)
    {
        if (tray == IntPtr.Zero || _hwnd == IntPtr.Zero)
            return;
        if (NativeMethods.GetWindow(_hwnd, NativeMethods.GW_OWNER) == tray)
            return;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, tray);
    }

    private void KeepAboveOverlay()
    {
        if (_hwnd == IntPtr.Zero || !_wantVisible)
            return;

        EnsureOverlayOwner(_tray);
        // Already sitting directly on top of the taskbar — don't restack (that flickers).
        if (_tray != IntPtr.Zero && NativeMethods.GetWindow(_hwnd, NativeMethods.GW_HWNDNEXT) == _tray)
            return;

        NativeMethods.SetWindowPos(
            _hwnd,
            InsertAfterOverlay(),
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private IntPtr InsertAfterOverlay() => _tray != IntPtr.Zero ? _tray : NativeMethods.HWND_TOPMOST;
}
