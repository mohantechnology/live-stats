using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

internal readonly record struct TaskbarInfo(
    IntPtr TrayHwnd,
    IntPtr NotifyHwnd,
    NativeMethods.RECT TrayRect,
    NativeMethods.RECT NotifyRect,
    bool IsVisible,
    bool AutoHide);
