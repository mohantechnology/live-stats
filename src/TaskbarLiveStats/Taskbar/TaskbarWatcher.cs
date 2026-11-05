using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

internal sealed class TaskbarWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext? _ui;
    private readonly NativeMethods.WinEventProc _onEvent;
    private IntPtr _locationHook;
    private IntPtr _foregroundHook;
    private uint _hookedThread;
    private IntPtr _lastTray = IntPtr.Zero;
    private bool _lastVisible = true;
    private bool _initialized;
    private bool _inTick;
    private int _queued;

    public event Action<TaskbarInfo>? Changed;
    public event Action? ExplorerRestarted;
    public event Action<bool>? VisibilityChanged;

    public TaskbarInfo Current { get; private set; }

    public TaskbarWatcher(int intervalMs = 200)
    {
        _ui = SynchronizationContext.Current;
        _onEvent = OnWinEvent;
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(100, intervalMs) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        EnsureForegroundHook();
        Tick();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        if (_inTick) return;
        _inTick = true;
        try
        {
            var info = Query();
            EnsureLocationHook(info.TrayHwnd);

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
        finally
        {
            _inTick = false;
        }
    }

    private void EnsureForegroundHook()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _onEvent,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void EnsureLocationHook(IntPtr tray)
    {
        if (tray == IntPtr.Zero) return;
        uint thread = NativeMethods.GetWindowThreadProcessId(tray, out _);
        if (thread == 0 || thread == _hookedThread) return;

        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        _hookedThread = thread;
        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _onEvent,
            0,
            thread,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND && idObject != NativeMethods.OBJID_WINDOW)
            return;

        if (Interlocked.Exchange(ref _queued, 1) == 1)
            return;

        void Run()
        {
            Interlocked.Exchange(ref _queued, 0);
            Tick();
        }

        if (_ui == null || SynchronizationContext.Current == _ui)
            Run();
        else
            _ui.Post(_ => Run(), null);
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
        if (thickness < 24)
            return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(tray, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return thickness >= 32;

        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            return thickness >= 32;

        // Shown taskbar occupies a meaningful strip along one monitor edge.
        // A full-height bar that has slid mostly off-screen still counts as shown
        // so an embedded child can ride the animation instead of popping off early.
        int visibleBand = VisibleThickness(trayRect, mi.rcMonitor, horizontal);
        return visibleBand >= 8 || thickness >= 28;
    }

    private static int VisibleThickness(NativeMethods.RECT tray, NativeMethods.RECT monitor, bool horizontal)
    {
        if (horizontal)
        {
            int top = Math.Max(tray.Top, monitor.Top);
            int bottom = Math.Min(tray.Bottom, monitor.Bottom);
            return Math.Max(0, bottom - top);
        }

        int left = Math.Max(tray.Left, monitor.Left);
        int right = Math.Min(tray.Right, monitor.Right);
        return Math.Max(0, right - left);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        if (_locationHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_locationHook);
        if (_foregroundHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_foregroundHook);
        _locationHook = IntPtr.Zero;
        _foregroundHook = IntPtr.Zero;
    }
}
