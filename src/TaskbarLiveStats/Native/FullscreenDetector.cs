using System.Text;

namespace TaskbarLiveStats.Native;

/// <summary>
/// Detects a foreground app that covers its monitor (borderless / exclusive fullscreen),
/// plus Shell notification states used for games and presentation mode.
/// </summary>
internal static class FullscreenDetector
{
    private const int QunsBusy = 2;
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;

    /// <summary>
    /// True when a fullscreen app should hide overlays that opt out of fullscreen display.
    /// <paramref name="excludeWindows"/> are ignored (our overlay / settings).
    /// </summary>
    public static bool IsFullscreenAppActive(params IntPtr[] excludeWindows)
    {
        if (IsShellFullscreenState())
            return true;

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || IsExcluded(fg, excludeWindows))
            return false;

        if (IsShellOrDesktop(fg) || IsOwnProcess(fg))
            return false;

        return CoversMonitor(fg);
    }

    private static bool IsOwnProcess(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != 0 && pid == (uint)Environment.ProcessId;
    }

    private static bool IsShellFullscreenState()
    {
        if (NativeMethods.SHQueryUserNotificationState(out int state) != 0)
            return false;

        // Busy covers F11 / many games; D3D is exclusive fullscreen; presentation blocks overlays.
        return state is QunsBusy or QunsRunningD3dFullScreen or QunsPresentationMode;
    }

    private static bool CoversMonitor(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT win))
            return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var mi = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            return false;

        // Exact monitor fill = classic / borderless fullscreen. Maximized apps use the work area.
        return win.Left == mi.rcMonitor.Left
               && win.Top == mi.rcMonitor.Top
               && win.Right == mi.rcMonitor.Right
               && win.Bottom == mi.rcMonitor.Bottom;
    }

    private static bool IsShellOrDesktop(IntPtr hwnd)
    {
        IntPtr desktop = NativeMethods.GetDesktopWindow();
        IntPtr shell = NativeMethods.GetShellWindow();
        if (hwnd == desktop || hwnd == shell)
            return true;

        var cls = new StringBuilder(64);
        if (NativeMethods.GetClassName(hwnd, cls, cls.Capacity) <= 0)
            return false;

        string name = cls.ToString();
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool IsExcluded(IntPtr hwnd, IntPtr[] excludeWindows)
    {
        foreach (IntPtr skip in excludeWindows)
        {
            if (skip != IntPtr.Zero && skip == hwnd)
                return true;
        }

        return false;
    }
}
