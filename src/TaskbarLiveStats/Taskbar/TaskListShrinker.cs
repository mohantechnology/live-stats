using System.Text;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

/// <summary>
/// Narrows the taskbar app-list band so a slot exists for our stats bar.
/// </summary>
internal sealed class TaskListShrinker : IDisposable
{
    private IntPtr _taskList = IntPtr.Zero;
    private NativeMethods.RECT _original;
    private bool _hasOriginal;
    private int _lastReserved = -1;
    private int _lastSlotLeft = int.MinValue;

    public void EnsureFound(IntPtr trayHwnd)
    {
        if (_taskList != IntPtr.Zero && NativeMethods.IsWindow(_taskList))
            return;

        _taskList = FindChildByClass(trayHwnd, "MSTaskListWClass");
        if (_taskList == IntPtr.Zero)
            _taskList = FindChildByClass(trayHwnd, "MSTaskSwWClass");
        if (_taskList == IntPtr.Zero)
            _taskList = FindChildByClass(trayHwnd, "Taskband"); // some builds

        // Fallback: widest visible child that is not the notify area
        if (_taskList == IntPtr.Zero)
            _taskList = FindWidestContentChild(trayHwnd);

        if (_taskList != IntPtr.Zero && NativeMethods.GetWindowRect(_taskList, out var r))
        {
            _original = r;
            _hasOriginal = true;
        }
    }

    public void ReserveLeftOfNotify(IntPtr trayHwnd, NativeMethods.RECT trayRect, NativeMethods.RECT notifyRect, int reservePx, int gap)
    {
        EnsureFound(trayHwnd);
        if (_taskList == IntPtr.Zero)
            return;

        bool horizontal = trayRect.Width >= trayRect.Height;
        if (!horizontal)
            return;

        reservePx = Math.Max(40, reservePx);
        int slotLeft = notifyRect.Left - reservePx - gap;

        if (reservePx == _lastReserved && slotLeft == _lastSlotLeft)
        {
            // Re-assert in case Explorer restored the band
        }

        _lastReserved = reservePx;
        _lastSlotLeft = slotLeft;

        if (!NativeMethods.GetWindowRect(_taskList, out var listScreen))
            return;

        int listLeft = listScreen.Left;
        int listTop = listScreen.Top;
        int height = listScreen.Height;
        int newRight = Math.Min(listScreen.Right, slotLeft);
        int newWidth = Math.Max(48, newRight - listLeft);

        IntPtr parent = NativeMethods.GetParent(_taskList);
        var pt = new NativeMethods.POINT { X = listLeft, Y = listTop };
        if (parent != IntPtr.Zero)
            NativeMethods.ScreenToClient(parent, ref pt);

        NativeMethods.MoveWindow(_taskList, pt.X, pt.Y, newWidth, height, true);
    }

    public void Restore()
    {
        if (!_hasOriginal || _taskList == IntPtr.Zero || !NativeMethods.IsWindow(_taskList))
        {
            _lastReserved = -1;
            return;
        }

        IntPtr parent = NativeMethods.GetParent(_taskList);
        var pt = new NativeMethods.POINT { X = _original.Left, Y = _original.Top };
        if (parent != IntPtr.Zero)
            NativeMethods.ScreenToClient(parent, ref pt);

        NativeMethods.MoveWindow(_taskList, pt.X, pt.Y, _original.Width, _original.Height, true);
        _lastReserved = -1;
        _lastSlotLeft = int.MinValue;
    }

    public void Dispose() => Restore();

    private static IntPtr FindChildByClass(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            if (string.Equals(sb.ToString(), className, StringComparison.Ordinal))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindWidestContentChild(IntPtr tray)
    {
        IntPtr notify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr best = IntPtr.Zero;
        int bestW = 0;

        NativeMethods.EnumChildWindows(tray, (hwnd, _) =>
        {
            if (hwnd == notify) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            if (r.Width > bestW && r.Width > 80)
            {
                bestW = r.Width;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }
}
