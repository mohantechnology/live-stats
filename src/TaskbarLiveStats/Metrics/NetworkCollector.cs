using System.Net.NetworkInformation;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Network up/down rates via Windows IP statistics (NetworkInterface → IP Helper).
/// Excludes loopback and down adapters.
/// </summary>
internal sealed class NetworkCollector
{
    private ulong _prevIn;
    private ulong _prevOut;
    private long _prevTicks;
    private bool _hasPrev;

    public (double SendBps, double RecvBps) Sample()
    {
        if (!TryReadTotals(out ulong inOctets, out ulong outOctets))
            return (0, 0);

        long now = Environment.TickCount64;
        if (!_hasPrev)
        {
            _prevIn = inOctets;
            _prevOut = outOctets;
            _prevTicks = now;
            _hasPrev = true;
            return (0, 0);
        }

        double elapsedSec = Math.Max((now - _prevTicks) / 1000.0, 0.001);
        double recv = (inOctets >= _prevIn ? inOctets - _prevIn : 0) / elapsedSec;
        double send = (outOctets >= _prevOut ? outOctets - _prevOut : 0) / elapsedSec;

        _prevIn = inOctets;
        _prevOut = outOctets;
        _prevTicks = now;

        return (send, recv);
    }

    public void Reset() => _hasPrev = false;

    private static bool TryReadTotals(out ulong inOctets, out ulong outOctets)
    {
        inOctets = 0;
        outOctets = 0;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var stats = nic.GetIPStatistics();
                inOctets += (ulong)Math.Max(0, stats.BytesReceived);
                outOctets += (ulong)Math.Max(0, stats.BytesSent);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
