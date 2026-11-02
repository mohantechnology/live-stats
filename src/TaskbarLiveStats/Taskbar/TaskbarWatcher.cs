using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

internal sealed class TaskbarWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private IntPtr _lastTray = IntPtr.Zero;
    private bool _lastVisible = true;
    private bool _initialized;

    public event Action<TaskbarInfo>? Changed;
    public event Action? ExplorerRestarted;
    public event Action<bool>? VisibilityChanged;

    public TaskbarInfo Current { get; private set; }

    public TaskbarWatcher(int intervalMs = 400)
    {
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(200, intervalMs) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Tick();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        var info = Query();
        bool explorerRestarted = _lastTray != IntPtr.Zero && info.TrayHwnd != IntPtr.Zero && info.TrayHwnd != _lastTray;
        bool visibilityChanged = !_initialized || info.IsVisible != _lastVisible;

        Current = info;
        _lastTray = info.TrayHwnd;
        _lastVisible = info.IsVisible;
        _initialized = true;

        if (explorerRestarted)
            ExplorerRestarted?.Invoke();

        if (visibilityChanged)
            VisibilityChanged?.Invoke(info.IsVisible);

        Changed?.Invoke(info);
    }

    public static TaskbarInfo Query()
    {
        IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        IntPtr notify = IntPtr.Zero;
        var trayRect = default(NativeMethods.RECT);
        var notifyRect = default(NativeMethods.RECT);
        bool visible = false;
        bool autoHide = false;

        if (tray != IntPtr.Zero && NativeMethods.IsWindow(tray))
        {
            NativeMethods.GetWindowRect(tray, out trayRect);
            notify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                NativeMethods.GetWindowRect(notify, out notifyRect);
                // Win11 sometimes reports a 0×0 TrayNotifyWnd during transitions — treat as missing.
                if (notifyRect.Width <= 8 || notifyRect.Height <= 0)
                {
                    notifyRect = default;
                    notify = IntPtr.Zero;
                }
            }

            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>()
            };
            IntPtr state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd);
            autoHide = ((int)state & NativeMethods.ABS_AUTOHIDE) != 0;

            visible = IsTaskbarVisuallyShown(tray, trayRect, autoHide);
        }

        return new TaskbarInfo(tray, notify, trayRect, notifyRect, visible, autoHide);
    }

    /// <summary>
    /// Auto-hide taskbars remain IsWindowVisible=true while parked as a thin edge strip.
    /// Treat "shown" as a substantial overlap with the monitor work/monitor bounds.
    /// </summary>
    private static bool IsTaskbarVisuallyShown(IntPtr tray, NativeMethods.RECT trayRect, bool autoHide)
    {
        if (trayRect.Width <= 0 || trayRect.Height <= 0)
            return false;

        if (!NativeMethods.IsWindowVisible(tray))
            return false;

        bool horizontal = trayRect.Width >= trayRect.Height;
        int thickness = horizontal ? trayRect.Height : trayRect.Width;

        if (!autoHide)
            return thickness >= 20;

        // Auto-hide: hidden strip is typically 1–4px; shown bar is ~40–72px depending on DPI.
        // Use a hysteresis-friendly threshold and also require the bar to sit on the monitor edge
        // with real thickness — avoids stuck-hidden when the strip animates through mid sizes.
        if (thickness < 24)
            return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(tray, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return thickness >= 32;

        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            return thickness >= 32;

        // Shown taskbar occupies a meaningful strip along one monitor edge
        return thickness >= 28;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
