using System.Globalization;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Headline and per-core CPU use % Processor Time — the metric Task Manager
/// Performance / modern Processes tabs align with (busy time ÷ elapsed × cores).
/// </summary>
internal sealed class CpuCollector : IDisposable
{
    private IntPtr _query;
    private IntPtr _totalCounter;
    private IntPtr _coreCounter;
    private bool _totalReady;
    private bool _coresReady;
    private bool _primed;
    private double _lastPercent;
    private CoreUsage[] _lastCores = [];

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasPrev;

    public CpuCollector()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        _totalReady = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\Processor Information(_Total)\% Processor Time",
            IntPtr.Zero,
            out _totalCounter) == 0;

        _coresReady = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\Processor Information(*)\% Processor Time",
            IntPtr.Zero,
            out _coreCounter) == 0;

        if (!_totalReady && !_coresReady)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }

        NativeMethods.PdhCollectQueryData(_query);
    }

    public (double Percent, CoreUsage[] Cores) Sample()
    {
        if (_query == IntPtr.Zero)
            return (SampleTimes(), []);

        if (NativeMethods.PdhCollectQueryData(_query) != 0)
            return (_lastPercent, _lastCores);

        if (!_primed)
        {
            _primed = true;
            if (!_totalReady)
                _lastPercent = SampleTimes();
            return (_lastPercent, _lastCores);
        }

        _lastPercent = _totalReady ? ReadTotal() : SampleTimes();
        if (_coresReady)
            _lastCores = ReadCores();

        return (_lastPercent, _lastCores);
    }

    public void Reset()
    {
        _primed = false;
        _hasPrev = false;
        if (_query != IntPtr.Zero)
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

    private double ReadTotal()
    {
        double value = PdhReader.ReadDouble(_totalCounter);
        if (double.IsNaN(value))
            return _lastPercent;
        return Math.Clamp(value, 0, 100);
    }

    private CoreUsage[] ReadCores()
    {
        if (!PdhReader.TryReadCounterArray(_coreCounter, out var items))
            return _lastCores;

        var best = new Dictionary<int, double>();
        foreach (var (name, value) in items)
        {
            if (name.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryParseInstance(name.Trim(), out int group, out int number))
                continue;

            int index = CoreTopology.FlatIndex(group, number);
            double pct = Math.Clamp(value, 0, 100);
            best[index] = best.TryGetValue(index, out double prev) ? Math.Max(prev, pct) : pct;
        }

        if (best.Count == 0)
            return _lastCores;

        var cores = new CoreUsage[best.Count];
        int i = 0;
        foreach (var (index, pct) in best)
            cores[i++] = new CoreUsage(index, pct);
        return cores;
    }

    private static bool TryParseInstance(string name, out int group, out int number)
    {
        group = 0;
        number = 0;
        int comma = name.IndexOf(',');
        if (comma <= 0 || comma >= name.Length - 1)
            return false;

        ReadOnlySpan<char> groupText = name.AsSpan(0, comma);
        ReadOnlySpan<char> numberText = name.AsSpan(comma + 1);
        int hash = numberText.IndexOf('#');
        if (hash >= 0)
            numberText = numberText[..hash];

        return int.TryParse(groupText, NumberStyles.Integer, CultureInfo.InvariantCulture, out group)
            && int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            && group >= 0
            && number >= 0;
    }

    private double SampleTimes()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return _lastPercent;

        ulong idleT = idle.ToUInt64();
        ulong kernelT = kernel.ToUInt64();
        ulong userT = user.ToUInt64();

        if (!_hasPrev)
        {
            _prevIdle = idleT;
            _prevKernel = kernelT;
            _prevUser = userT;
            _hasPrev = true;
            return _lastPercent;
        }

        ulong idleDelta = idleT - _prevIdle;
        ulong kernelDelta = kernelT - _prevKernel;
        ulong userDelta = userT - _prevUser;
        ulong total = kernelDelta + userDelta;
        ulong busy = total > idleDelta ? total - idleDelta : 0;

        _prevIdle = idleT;
        _prevKernel = kernelT;
        _prevUser = userT;

        if (total == 0)
            return 0;
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }
}
