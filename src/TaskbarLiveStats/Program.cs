using System.Runtime.InteropServices;

namespace TaskbarLiveStats;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AntdUI.Config.IsDark = true;

        // Prefer Segoe UI / system fonts for GDI+
        SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2

        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = UI.SettingsSelfTest.Run();
            return;
        }

        _mutex = new Mutex(true, @"Local\TaskbarLiveStats.SingleInstance", out bool created);
        if (!created)
            return;

        if (args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new TrayAppContext(openSettingsOnStart: true));
        }
        else
        {
            Application.Run(new TrayAppContext());
        }

        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int value);
}
