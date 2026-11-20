using Microsoft.Win32;

namespace TaskbarLiveStats;

internal static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarLiveStats";
    public const string AutostartArgument = "--autostart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (enabled)
        {
            string path = Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{path}\" {AutostartArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
