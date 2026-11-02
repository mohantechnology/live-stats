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
internal sealed class TaskbarLayoutProbe
{
    private static readonly object Sync = new();
    private static CUIAutomation? _automation;

    private NativeMethods.RECT? _widgets;
    private NativeMethods.RECT? _start;
    private NativeMethods.RECT? _appList;
    private NativeMethods.RECT? _notify;
    private TaskbarAlignment _alignment = TaskbarAlignment.Unknown;
    private long _cachedAtTicks;
    private IntPtr _cachedTray;

    public NativeMethods.RECT? WidgetsRect => _widgets;
    public NativeMethods.RECT? StartRect => _start;
    public NativeMethods.RECT? AppListRect => _appList;
    public NativeMethods.RECT? NotifyRect => _notify;
    public TaskbarAlignment Alignment => _alignment;

    public void Refresh(IntPtr trayHwnd, int cacheMs = 400)
    {
        if (trayHwnd == IntPtr.Zero)
            return;

        long now = Environment.TickCount64;
        if (trayHwnd == _cachedTray && now - _cachedAtTicks < cacheMs)
            return;

        _cachedTray = trayHwnd;
        _cachedAtTicks = now;
        _alignment = ReadAlignment();

        // HWND path first — reliable on current Win11 taskbar builds
        _start = FindStartHwnd(trayHwnd);
        _appList = FindTaskListHwnd(trayHwnd);
        _notify = FindNotifyHwnd(trayHwnd);

        // Optional UIA enrichment (may time out — never block layout on it)
        try
        {
            var automation = GetAutomation();
            _widgets = FindBoundsQuick(automation, "WidgetsButton")
                       ?? FindBoundsQuick(automation, "TaskbarWidgetsButton");
            _start ??= FindBoundsQuick(automation, "StartButton");
        }
        catch
        {
            // ignore — HWND fallbacks are enough
        }
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

    private static NativeMethods.RECT? FindBoundsQuick(CUIAutomation automation, string automationId)
    {
        try
        {
            IUIAutomationElement desktop = automation.GetRootElement();
            var condition = automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_AutomationIdPropertyId,
                automationId);
            // Children-only first wave is cheaper; Widgets is usually not a direct desktop child
            IUIAutomationElement? el = desktop.FindFirst(TreeScope.TreeScope_Descendants, condition);
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

    private static CUIAutomation GetAutomation()
    {
        lock (Sync)
            return _automation ??= new CUIAutomation();
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
