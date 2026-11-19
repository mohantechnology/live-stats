using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Disk read/write via PDH PhysicalDisk(_Total) Bytes/sec.
/// PDH computes the rate from consecutive collects using the real sample gap,
/// so changing the UI refresh interval does not require manual delta math.
/// </summary>
internal sealed class DiskCollector : IDisposable
{
    private IntPtr _query;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private bool _ready;
    private bool _primed;
    private double _lastRead;
    private double _lastWrite;

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
            // Rate counters need a priming collect; first Sample() still skips rates.
            NativeMethods.PdhCollectQueryData(_query);
        }
    }

    public DiskReading Sample()
    {
        if (!_ready)
            return default;

        if (NativeMethods.PdhCollectQueryData(_query) != 0)
            return new DiskReading(_lastRead, _lastWrite, 0, 0, false);

        bool readOk = TryGetRawTotal(_readCounter, out ulong readBytes);
        bool writeOk = TryGetRawTotal(_writeCounter, out ulong writeBytes);
        bool totalsValid = readOk && writeOk;

        if (!_primed)
        {
            _primed = true;
            return new DiskReading(0, 0, readBytes, writeBytes, totalsValid);
        }

        double read = ReadRate(_readCounter, _lastRead);
        double write = ReadRate(_writeCounter, _lastWrite);
        _lastRead = read;
        _lastWrite = write;
        return new DiskReading(read, write, readBytes, writeBytes, totalsValid);
    }

    private static bool TryGetRawTotal(IntPtr counter, out ulong total)
    {
        total = 0;
        if (NativeMethods.PdhGetRawCounterValue(counter, IntPtr.Zero, out var raw) != 0)
            return false;
        // High bit set means a PDH error status, not a usable count.
        if ((raw.CStatus & 0x80000000) != 0 || raw.FirstValue < 0)
            return false;
        total = (ulong)raw.FirstValue;
        return true;
    }

    private static double ReadRate(IntPtr counter, double fallback)
    {
        double value = PdhReader.ReadDouble(counter);
        if (double.IsNaN(value))
            return fallback;
        return Math.Max(0, value);
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
