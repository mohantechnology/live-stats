using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

internal sealed class DiskCollector : IDisposable
{
    private IntPtr _query;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private bool _ready;
    private bool _primed;

    public DiskCollector()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            return;

        uint r1 = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
            IntPtr.Zero,
            out _readCounter);

        uint r2 = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\PhysicalDisk(_Total)\Disk Write Bytes/sec",
            IntPtr.Zero,
            out _writeCounter);

        _ready = r1 == 0 && r2 == 0;
        if (_ready)
        {
            // Rate counters need a priming collect
            NativeMethods.PdhCollectQueryData(_query);
            _primed = true;
        }
    }

    public (double ReadBps, double WriteBps) Sample()
    {
        if (!_ready) return (0, 0);

        if (NativeMethods.PdhCollectQueryData(_query) != 0)
            return (0, 0);

        if (!_primed)
        {
            _primed = true;
            return (0, 0);
        }

        double read = GetDouble(_readCounter);
        double write = GetDouble(_writeCounter);
        return (Math.Max(0, read), Math.Max(0, write));
    }

    private static double GetDouble(IntPtr counter)
    {
        if (NativeMethods.PdhGetFormattedCounterValue(counter, NativeMethods.PDH_FMT_DOUBLE, IntPtr.Zero, out var value) != 0)
            return 0;
        return value.DoubleValue;
    }

    public void Reset()
    {
        _primed = false;
        if (_ready)
            NativeMethods.PdhCollectQueryData(_query);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
