using Microsoft.Win32;

namespace TaskbarLiveStats.UI;

internal static class ThemeHelper
{
    public static bool IsDarkTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("SystemUsesLightTheme");
            if (value is int i)
                return i == 0;
        }
        catch
        {
            // ignore
        }

        return true;
    }

    public static Color TextColor => IsDarkTaskbar() ? Color.FromArgb(240, 240, 240) : Color.FromArgb(20, 20, 20);
    public static Color DimColor => IsDarkTaskbar() ? Color.FromArgb(170, 170, 170) : Color.FromArgb(90, 90, 90);

    /// <summary>Matches typical Win11 taskbar fill so the slot looks native.</summary>
    public static Color TaskbarFillColor => IsDarkTaskbar()
        ? Color.FromArgb(255, 24, 24, 24)
        : Color.FromArgb(255, 245, 245, 245);
}
