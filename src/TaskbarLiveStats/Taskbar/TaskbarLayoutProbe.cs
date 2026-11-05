using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using Microsoft.Win32;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Taskbar;

internal enum TaskbarAlignment
{
    Unknown = 0,
    Left = 1,
    Center = 2
}

/// <summary>
/// Measures taskbar chrome for slot placement. Prefers real HWNDs (Start / notify /
/// task list) because Win11 XAML islands often break UI Automation.
/// </summary>
internal sealed class TaskbarLayoutProbe : IDisposable
{
    private readonly object _widgetsLock = new();
    private readonly Thread _uiaThread;
    private readonly ManualResetEvent _stop = new(false);
    private readonly ManualResetEvent _poke = new(false);
    private int _disposed;

    private NativeMethods.RECT? _widgets;
    private NativeMethods.RECT? _uiaWidgets;
    private NativeMethods.RECT? _uiaStart;
    private NativeMethods.RECT? _start;
    private NativeMethods.RECT? _appList;
    private NativeMethods.RECT? _notify;
    private TaskbarAlignment _alignment = TaskbarAlignment.Unknown;
    private IntPtr _uiaTray;

    public NativeMethods.RECT? WidgetsRect => _widgets;
    public NativeMethods.RECT? StartRect => _start;
    public NativeMethods.RECT? AppListRect => _appList;
    public NativeMethods.RECT? NotifyRect => _notify;
    public TaskbarAlignment Alignment => _alignment;

    public TaskbarLayoutProbe()
    {
        _uiaThread = new Thread(UiaLoop)
        {
            IsBackground = true,
            Name = "TaskbarUia",
        };
        _uiaThread.SetApartmentState(ApartmentState.STA);
        _uiaThread.Start();
    }

    public void Refresh(IntPtr trayHwnd, int cacheMs = 400)
    {
        _ = cacheMs;
        if (trayHwnd == IntPtr.Zero)
            return;

        _alignment = ReadAlignment();

        // HWND path is cheap and stays on the UI thread.
        _start = FindStartHwnd(trayHwnd);
        _appList = FindTaskListHwnd(trayHwnd);
        _notify = FindNotifyHwnd(trayHwnd);

        lock (_widgetsLock)
        {
            if (_uiaWidgets is not null)
                _widgets = _uiaWidgets;
            _start ??= _uiaStart;
        }

        // Widgets live in the XAML island. Scan them off the UI thread so a
        // slow UI Automation walk cannot freeze hide/show.
        Volatile.Write(ref _uiaTray, trayHwnd);
        _poke.Set();
    }

    /// <summary>
    /// Default: sit in the left free gap just after the Widgets / weather band,
    /// before the centered Start + apps. Falls back to just before the notify area.
    /// </summary>
    public bool TryComputeSlot(
        NativeMethods.RECT tray,
        NativeMethods.RECT notifyFromWatcher,
        int desiredWidth,
        int gap,
        out int screenX,
        out int screenY,
        out int width,
        out int height)
    {
        bool horizontal = tray.Width >= tray.Height;
        width = Math.Max(40, desiredWidth);
        height = Math.Max(28, horizontal ? tray.Height : Math.Max(desiredWidth, 28));
        gap = Math.Max(2, gap);
        screenX = tray.Left;
        screenY = tray.Top;

        var notify = EffectiveNotify(tray, notifyFromWatcher);

        if (!horizontal)
        {
            screenX = tray.Left + Math.Max(0, (tray.Width - width) / 2);
            screenY = notify.Top - height - gap;
            return true;
        }

        height = Math.Max(28, tray.Height);
        screenY = tray.Top;

        int startLeft = _start?.Left ?? int.MaxValue;
        int appsLeft = _appList?.Left ?? int.MaxValue;
        int centerLeft = Math.Min(startLeft, appsLeft);

        // --- Preferred: after Widgets, in the left gap before Start/apps ---
        if (centerLeft < tray.Right && centerLeft > tray.Left + 80)
        {
            int widgetsRight = ResolveWidgetsRight(tray, centerLeft);
            int afterWidgets = widgetsRight + gap;
            int maxRight = centerLeft - gap;
            int available = maxRight - afterWidgets;

            if (available >= 100)
            {
                screenX = afterWidgets;
                if (screenX + width > maxRight)
                    width = Math.Max(100, maxRight - screenX);
                return true;
            }
        }

        // --- Fallback: immediately left of the system tray / clock ---
        int anchorLeft = notify.Left;
        screenX = anchorLeft - width - gap;

        int minLeft = tray.Left + 8;
        if (_appList is { } apps && apps.Right > tray.Left && apps.Right < anchorLeft)
            minLeft = Math.Max(minLeft, apps.Right + gap);

        if (screenX < minLeft)
        {
            screenX = minLeft;
            width = Math.Max(80, anchorLeft - gap - screenX);
        }

        return true;
    }

    public bool IsAfterWidgetsPlacement(
        NativeMethods.RECT tray,
        NativeMethods.RECT notifyFromWatcher,
        int desiredWidth,
        int gap)
    {
        if (!TryComputeSlot(tray, notifyFromWatcher, desiredWidth, gap, out int x, out _, out _, out _))
            return false;

        int startLeft = _start?.Left ?? int.MaxValue;
        int appsLeft = _appList?.Left ?? int.MaxValue;
        int centerLeft = Math.Min(startLeft, appsLeft);
        return centerLeft < tray.Right && x + 10 < centerLeft;
    }

    private int ResolveWidgetsRight(NativeMethods.RECT tray, int centerLeft)
    {
        if (_widgets is { } w && w.Width > 0 && w.Right > tray.Left && w.Right < centerLeft)
            return w.Right;

        // No Widgets HWND/UIA: reserve a DPI-aware band for the weather / widgets entry.
        // On 100–150% DPI the weather chip is typically ~200–320px.
        int dpiBand = GetDpiBand(tray.Height);
        int maxBand = Math.Max(120, centerLeft - tray.Left - 120);
        return tray.Left + Math.Clamp(dpiBand, 160, maxBand);
    }

    private static int GetDpiBand(int trayHeight)
    {
        // trayHeight tracks DPI roughly (48 @ 100%, ~72 @ 150% physical → often 48 virtual)
        float scale = Math.Clamp(trayHeight / 48f, 1f, 2.5f);
        return (int)Math.Round(240 * scale);
    }

    private NativeMethods.RECT EffectiveNotify(NativeMethods.RECT tray, NativeMethods.RECT watcherNotify)
    {
        if (_notify is { } n && n.Width > 8)
            return n;
        if (watcherNotify.Width > 8)
            return watcherNotify;

        // Last resort: assume ~280px system tray cluster on the right
        int w = Math.Min(320, Math.Max(160, tray.Width / 8));
        return new NativeMethods.RECT
        {
            Left = tray.Right - w,
            Top = tray.Top,
            Right = tray.Right,
            Bottom = tray.Bottom
        };
    }

    private static NativeMethods.RECT? FindStartHwnd(IntPtr tray)
    {
        // Win11 still exposes a legacy Start child for hit-testing on many builds
        IntPtr start = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "Start", null);
        if (start == IntPtr.Zero)
            start = FindChildByTitle(tray, "Start");
        return RectOf(start);
    }

    private static NativeMethods.RECT? FindTaskListHwnd(IntPtr tray)
    {
        IntPtr list = FindChildByClass(tray, "MSTaskListWClass");
        if (list == IntPtr.Zero)
            list = FindChildByClass(tray, "MSTaskSwWClass");
        return RectOf(list);
    }

    private static NativeMethods.RECT? FindNotifyHwnd(IntPtr tray)
    {
        IntPtr notify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        var r = RectOf(notify);
        if (r is { } rr && rr.Width > 8)
            return rr;
        return null;
    }

    private static NativeMethods.RECT? RectOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return null;
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
            return null;
        if (r.Width <= 0 || r.Height <= 0)
            return null;
        return r;
    }

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

    private static IntPtr FindChildByTitle(IntPtr parent, string title)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            if (string.Equals(sb.ToString(), title, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void UiaLoop()
    {
        CUIAutomation? automation = null;
        try
        {
            automation = new CUIAutomation();
            var handles = new WaitHandle[] { _stop, _poke };
            while (WaitHandle.WaitAny(handles) != 0)
            {
                _poke.Reset();
                if (Volatile.Read(ref _disposed) != 0)
                    break;

                IntPtr tray = Volatile.Read(ref _uiaTray);
                if (tray == IntPtr.Zero || !NativeMethods.IsWindow(tray))
                    continue;

                try
                {
                    IUIAutomationElement root = automation.ElementFromHandle(tray);
                    NativeMethods.RECT? widgets = FindBounds(automation, root, "WidgetsButton")
                                                   ?? FindBounds(automation, root, "TaskbarWidgetsButton");
                    NativeMethods.RECT? start = FindBounds(automation, root, "StartButton");
                    lock (_widgetsLock)
                    {
                        if (widgets is not null)
                            _uiaWidgets = widgets;
                        if (start is not null)
                            _uiaStart = start;
                    }
                }
                catch
                {
                    // HWND geometry still places the bar if UIA is busy.
                }
            }
        }
        catch
        {
            // UIA unavailable — placement falls back to the DPI widgets band.
        }
        finally
        {
            if (automation is not null && Marshal.IsComObject(automation))
                Marshal.ReleaseComObject(automation);
        }
    }

    private static NativeMethods.RECT? FindBounds(CUIAutomation automation, IUIAutomationElement root, string automationId)
    {
        try
        {
            var condition = automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_AutomationIdPropertyId,
                automationId);
            IUIAutomationElement? el = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (el is null)
                return null;
            tagRECT r = el.CurrentBoundingRectangle;
            if (r.right <= r.left || r.bottom <= r.top)
                return null;
            return new NativeMethods.RECT { Left = r.left, Top = r.top, Right = r.right, Bottom = r.bottom };
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Set();
        _poke.Set();
        if (!_uiaThread.Join(400))
            return;
        _stop.Dispose();
        _poke.Dispose();
    }

    private static TaskbarAlignment ReadAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            object? value = key?.GetValue("TaskbarAl");
            if (value is int i)
                return i == 0 ? TaskbarAlignment.Left : TaskbarAlignment.Center;
        }
        catch { /* ignore */ }
        return TaskbarAlignment.Unknown;
    }
}
