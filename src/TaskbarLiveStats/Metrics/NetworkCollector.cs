using System.Net.NetworkInformation;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Network up/down via PDH Network Interface counters (same source as Task Manager).
/// Falls back to IP Helper totals with filter-stack de-duplication when PDH is unavailable.
/// </summary>
internal sealed class NetworkCollector : IDisposable
{
    private IntPtr _query;
    private IntPtr _recvCounter;
    private IntPtr _sendCounter;
    private bool _pdhReady;
    private bool _pdhPrimed;

    private ulong _prevIn;
    private ulong _prevOut;
    private long _prevTicks;
    private bool _hasPrev;

    public NetworkCollector()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            return;

        uint r1 = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\Network Interface(*)\Bytes Received/sec",
            IntPtr.Zero,
            out _recvCounter);

        uint r2 = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\Network Interface(*)\Bytes Sent/sec",
            IntPtr.Zero,
            out _sendCounter);

        _pdhReady = r1 == 0 && r2 == 0;
        if (_pdhReady)
            NativeMethods.PdhCollectQueryData(_query);
        else
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    public NetworkReading Sample()
    {
        if (_pdhReady)
            return SamplePdh();
        return SampleIpHelper();
    }

    public void Reset()
    {
        _pdhPrimed = false;
        _hasPrev = false;
        if (_pdhReady)
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

    private NetworkReading SamplePdh()
    {
        if (NativeMethods.PdhCollectQueryData(_query) != 0)
            return default;

        bool recvOk = TrySumRaw(_recvCounter, out ulong recvBytes);
        bool sendOk = TrySumRaw(_sendCounter, out ulong sendBytes);
        bool totalsValid = recvOk && sendOk;

        if (!_pdhPrimed)
        {
            _pdhPrimed = true;
            return new NetworkReading(0, 0, sendBytes, recvBytes, totalsValid);
        }

        double recv = SumFormatted(_recvCounter);
        double send = SumFormatted(_sendCounter);
        return new NetworkReading(Math.Max(0, send), Math.Max(0, recv), sendBytes, recvBytes, totalsValid);
    }

    private NetworkReading SampleIpHelper()
    {
        if (!TryReadTotals(out ulong inOctets, out ulong outOctets))
            return default;

        long now = Environment.TickCount64;
        if (!_hasPrev)
        {
            _prevIn = inOctets;
            _prevOut = outOctets;
            _prevTicks = now;
            _hasPrev = true;
            return new NetworkReading(0, 0, outOctets, inOctets, true);
        }

        // Divide by the real gap so a settings refresh-rate change stays correct.
        double elapsedSec = Math.Max((now - _prevTicks) / 1000.0, 0.001);
        double recv = (inOctets >= _prevIn ? inOctets - _prevIn : 0) / elapsedSec;
        double send = (outOctets >= _prevOut ? outOctets - _prevOut : 0) / elapsedSec;

        _prevIn = inOctets;
        _prevOut = outOctets;
        _prevTicks = now;

        return new NetworkReading(send, recv, outOctets, inOctets, true);
    }

    private static double SumFormatted(IntPtr counter)
    {
        if (!PdhReader.TryReadCounterArray(counter, out var items))
            return 0;

        double sum = 0;
        foreach (var (name, value) in items)
        {
            if (!IncludeInterface(name))
                continue;
            sum += value;
        }

        return sum;
    }

    private static bool TrySumRaw(IntPtr counter, out ulong total)
    {
        total = 0;
        if (!PdhReader.TryReadRawCounterArray(counter, out var items))
            return false;

        ulong sum = 0;
        int n = 0;
        foreach (var (name, value) in items)
        {
            if (!IncludeInterface(name))
                continue;
            sum += value;
            n++;
        }

        total = sum;
        return n > 0;
    }

    private static bool IncludeInterface(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("isatap", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("6TO4", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// One row per physical MAC. GetAllNetworkInterfaces() also returns WFP/QoS
    /// filter clones that share the same since-boot counters and would multiply rates.
    /// </summary>
    private static bool TryReadTotals(out ulong inOctets, out ulong outOctets)
    {
        inOctets = 0;
        outOctets = 0;

        try
        {
            var seenMac = new HashSet<string>(StringComparer.Ordinal);
            int read = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                if (IsFilterOrVirtualClone(nic.Name, nic.Description))
                    continue;

                string mac = nic.GetPhysicalAddress()?.ToString() ?? string.Empty;
                if (mac.Length > 0 && !seenMac.Add(mac))
                    continue;

                try
                {
                    var stats = nic.GetIPStatistics();
                    inOctets += (ulong)Math.Max(0, stats.BytesReceived);
                    outOctets += (ulong)Math.Max(0, stats.BytesSent);
                    read++;
                }
                catch
                {
                    // This adapter is not reporting statistics.
                }
            }

            return read > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFilterOrVirtualClone(string name, string description)
    {
        static bool Hit(string text) =>
            text.Contains("WFP", StringComparison.OrdinalIgnoreCase)
            || text.Contains("QoS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Filter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual Switch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vSwitch", StringComparison.OrdinalIgnoreCase);

        return Hit(name) || Hit(description);
    }
}
