using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

internal sealed class MemoryCollector
{
    public (ulong Used, ulong Total, double Percent) Sample()
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            return (0, 0, 0);

        ulong total = status.ullTotalPhys;
        ulong avail = status.ullAvailPhys;
        ulong used = total > avail ? total - avail : 0;
        double percent = total == 0 ? 0 : 100.0 * used / total;
        return (used, total, percent);
    }
}
