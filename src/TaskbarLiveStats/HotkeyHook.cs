using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats;

/// <summary>Registers a global hotkey against an existing HWND (usually the stats overlay).</summary>
internal sealed class HotkeyHook : IDisposable
{
    public const int HotkeyId = 0x54_4C_53_01; // "TLS\x01"

    private IntPtr _hwnd;
    private bool _registered;

    public void Bind(IntPtr hwnd) => _hwnd = hwnd;

    public void Apply(AppSettings settings)
    {
        Unregister();
        if (!settings.HotkeyEnabled || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
            return;

        int modifiers = settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT;
        _registered = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers, settings.HotkeyVirtualKey);
    }

    public static string Format(int modifiers, int virtualKey)
    {
        var parts = new List<string>();
        int mods = modifiers & ~NativeMethods.MOD_NOREPEAT;
        if ((mods & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");

        string key = virtualKey switch
        {
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            _ => $"Vk{virtualKey:X2}"
        };
        parts.Add(key);
        return string.Join(" + ", parts);
    }

    public static bool IsHotkeyMessage(Message m) =>
        m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId;

    private void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero) return;
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    public void Dispose() => Unregister();
}
