using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats;

/// <summary>
/// Global show/hide hotkey on a message-only window, so it keeps working
/// while the stats overlay itself is hidden.
/// </summary>
internal sealed class HotkeyHook : NativeWindow, IDisposable
{
    public const int HotkeyId = 0x54_4C_53_01; // "TLS\x01"
    private static readonly IntPtr HwndMessage = new(-3);

    private bool _registered;

    public event Action? Pressed;

    public void Apply(AppSettings settings)
    {
        EnsureWindow();
        Unregister();
        if (!settings.HotkeyEnabled || Handle == IntPtr.Zero)
            return;

        int modifiers = settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT;
        _registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, modifiers, settings.HotkeyVirtualKey);
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

    protected override void WndProc(ref Message m)
    {
        if (IsHotkeyMessage(m))
        {
            Pressed?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }

    private void EnsureWindow()
    {
        if (Handle != IntPtr.Zero)
            return;

        CreateHandle(new CreateParams
        {
            Parent = HwndMessage,
            Caption = "TaskbarLiveStats.Hotkey"
        });
    }

    private void Unregister()
    {
        if (!_registered || Handle == IntPtr.Zero)
            return;
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
