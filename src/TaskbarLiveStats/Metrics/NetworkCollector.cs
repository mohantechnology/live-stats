using System.Net.NetworkInformation;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Network up/down rates via Windows IP statistics (NetworkInterface → IP Helper).
/// Excludes loopback. Down adapters stay in the total so a disconnect does not drop it.
/// </summary>
internal sealed class NetworkCollector
{
    private ulong _prevIn;
    private ulong _prevOut;
    private long _prevTicks;
    private bool _hasPrev;

    public NetworkReading Sample()
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

        double elapsedSec = Math.Max((now - _prevTicks) / 1000.0, 0.001);
        double recv = (inOctets >= _prevIn ? inOctets - _prevIn : 0) / elapsedSec;
        double send = (outOctets >= _prevOut ? outOctets - _prevOut : 0) / elapsedSec;

        _prevIn = inOctets;
        _prevOut = outOctets;
        _prevTicks = now;

        return new NetworkReading(send, recv, outOctets, inOctets, true);
    }

    public void Reset() => _hasPrev = false;

    private static bool TryReadTotals(out ulong inOctets, out ulong outOctets)
    {
        inOctets = 0;
        outOctets = 0;

        try
        {
            int read = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                try
                {
                    // Down adapters still hold their since-boot totals. Skipping them
                    // would make the sum fall the moment Wi-Fi blips.
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
}
