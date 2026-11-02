using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

/// <summary>
/// Parks a top-level layered stats window in a taskbar slot (after Widgets by default).
/// Win11's DesktopWindowContentBridge covers Shell_TrayWnd, so SetParent children are
/// usually invisible — a TOPMOST overlay that tracks the tray is the reliable approach.
/// Visibility follows taskbar auto-hide via TaskbarWatcher.
/// </summary>
internal sealed class TaskbarEmbedder : IDisposable
{
    private IntPtr _hwnd;
    private readonly TaskListShrinker _shrinker = new();
    private readonly TaskbarLayoutProbe _probe = new();
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastW;
    private int _lastH;
    private bool _wantVisible = true;

    public int DesiredWidth { get; set; } = 420;
    public int DesiredHeight { get; set; } = 36;
    public int Gap { get; set; } = 4;

    /// <summary>Overlay mode is not a tray child; kept for StatsWindow paint branching.</summary>
    public bool IsEmbedded => false;

    public TaskbarEmbedder(IntPtr hwnd) => _hwnd = hwnd;

    public void Bind(IntPtr hwnd)
    {
        _hwnd = hwnd;
        EnsureOverlayStyles();
        InvalidateLayoutCache();
    }

    public void AttachOrReposition(TaskbarInfo info)
    {
        if (info.TrayHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
            return;

        EnsureOverlayStyles();

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

        PositionOverlay(screenX, screenY, width, height);
    }

    public void Reembed(TaskbarInfo info)
    {
        _shrinker.Restore();
        InvalidateLayoutCache();
        if (_wantVisible)
            AttachOrReposition(info);
    }

    private void EnsureOverlayStyles()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Stay top-level — SetParent into Win11 tray paints under the XAML bridge.
        NativeMethods.SetParent(_hwnd, IntPtr.Zero);

        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style = (style | NativeMethods.WS_POPUP) & ~(NativeMethods.WS_CHILD | NativeMethods.WS_CLIPCHILDREN);
        if (_wantVisible)
            style |= NativeMethods.WS_VISIBLE;
        else
            style &= ~NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);

        int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
        ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private void PositionOverlay(int x, int y, int width, int height)
    {
        bool desiredUnchanged = x == _lastX && y == _lastY && width == _lastW && height == _lastH;
        if (desiredUnchanged && NativeMethods.GetWindowRect(_hwnd, out var cur)
            && Math.Abs(cur.Left - x) <= 1 && Math.Abs(cur.Top - y) <= 1 && Math.Abs(cur.Width - width) <= 1)
            return;

        _lastX = x;
        _lastY = y;
        _lastW = width;
        _lastH = height;

        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOCOPYBITS;
        if (_wantVisible)
            flags |= NativeMethods.SWP_SHOWWINDOW;
        else
            flags |= NativeMethods.SWP_HIDEWINDOW;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            x, y, width, height,
            flags);
    }

    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
            return;

        if (!visible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            _shrinker.Restore();
            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
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

    public void RaiseZOrder()
    {
        if (_hwnd == IntPtr.Zero || !_wantVisible) return;
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        _shrinker.Dispose();
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            NativeMethods.SetParent(_hwnd, IntPtr.Zero);
    }
}
