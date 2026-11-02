using System.Runtime.InteropServices;

namespace TaskbarLiveStats;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, @"Local\TaskbarLiveStats.SingleInstance", out bool created);
        if (!created)
            return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Prefer Segoe UI / system fonts for GDI+
        SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2

        Application.Run(new TrayAppContext());

        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int value);
}
