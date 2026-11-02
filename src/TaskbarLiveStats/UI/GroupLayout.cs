using System.Globalization;
using TaskbarLiveStats.Metrics;

namespace TaskbarLiveStats.UI;

internal readonly record struct MetricLine(string Icon, string Value, string Unit);

internal readonly record struct MetricGroup(MetricLine Top, MetricLine Bottom);

internal static class GroupLayout
{
    public static MetricGroup[] FromSnapshot(MetricsSnapshot s)
    {
        var cpu = new MetricLine("CPU", $"{s.CpuPercent:0}", "%");
        var ram = new MetricLine("RAM", $"{s.MemoryPercent:0}", "%");

        var gpu = new MetricLine("GPU", $"{s.GpuPercent:0}", "%");
        var vram = s.VramTotalBytes > 0
            ? new MetricLine("VRAM", FormatCompactBytes(s.VramUsedBytes), "/" + FormatCompactBytes(s.VramTotalBytes))
            : new MetricLine("VRAM", FormatCompactBytes(s.VramUsedBytes), "");

        // Network: bits/s like Task Manager Performance → Ethernet/Wi‑Fi
        var (upVal, upUnit) = FormatBitRateParts(s.NetSendBytesPerSec);
        var (dnVal, dnUnit) = FormatBitRateParts(s.NetRecvBytesPerSec);
        var up = new MetricLine("↑", upVal, upUnit);
        var dn = new MetricLine("↓", dnVal, dnUnit);

        // Disk: bytes/s like Task Manager Performance → Disk
        var (rVal, rUnit) = FormatByteRateParts(s.DiskReadBytesPerSec);
        var (wVal, wUnit) = FormatByteRateParts(s.DiskWriteBytesPerSec);
        var read = new MetricLine("R", rVal, rUnit);
        var write = new MetricLine("W", wVal, wUnit);

        // RAM used/total (Task Manager Memory "In use")
        ram = new MetricLine(
            "RAM",
            FormatCompactBytes(s.MemoryUsedBytes),
            "/" + FormatCompactBytes(s.MemoryTotalBytes));

        return
        [
            new MetricGroup(cpu, ram),
            new MetricGroup(gpu, vram),
            new MetricGroup(up, dn),
            new MetricGroup(read, write)
        ];
    }

    public static string Tooltip(MetricsSnapshot s)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"CPU {s.CpuPercent:0.0}%\n" +
            $"Memory {FormatBytes(s.MemoryUsedBytes)} / {FormatBytes(s.MemoryTotalBytes)} ({s.MemoryPercent:0}%)\n" +
            $"GPU {s.GpuPercent:0.0}%\n" +
            $"GPU memory {FormatBytes(s.VramUsedBytes)} / {FormatBytes(s.VramTotalBytes)}\n" +
            $"Send {FormatBitRate(s.NetSendBytesPerSec)}  Recv {FormatBitRate(s.NetRecvBytesPerSec)}\n" +
            $"Disk R {FormatByteRate(s.DiskReadBytesPerSec)}  W {FormatByteRate(s.DiskWriteBytesPerSec)}");
    }

    private static string FormatSlashTotal(ulong total) =>
        total > 0 ? "/" + FormatCompactBytes(total) : "";

    private static string FormatCompactBytes(ulong bytes)
    {
        double b = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (b >= 1024 && u < units.Length - 1)
        {
            b /= 1024;
            u++;
        }

        if (u == 0) return $"{b:0}{units[u]}";
        if (b >= 100) return $"{b:0}{units[u]}";
        if (b >= 10) return string.Create(CultureInfo.InvariantCulture, $"{b:0.0}{units[u]}");
        return string.Create(CultureInfo.InvariantCulture, $"{b:0.00}{units[u]}");
    }

    private static string FormatBytes(ulong bytes) => FormatCompactBytes(bytes);

    private static (string Value, string Unit) FormatBitRateParts(double bytesPerSec)
    {
        // Convert bytes/s → bits/s (Task Manager)
        double bits = Math.Max(0, bytesPerSec) * 8.0;
        string[] units = ["bps", "Kbps", "Mbps", "Gbps"];
        int u = 0;
        while (bits >= 1000 && u < units.Length - 1)
        {
            bits /= 1000;
            u++;
        }

        string val = u == 0
            ? $"{bits:0}"
            : bits >= 100 ? $"{bits:0}"
            : bits >= 10 ? string.Create(CultureInfo.InvariantCulture, $"{bits:0.0}")
            : string.Create(CultureInfo.InvariantCulture, $"{bits:0.0}");
        return (val, units[u]);
    }

    private static (string Value, string Unit) FormatByteRateParts(double bytesPerSec)
    {
        double b = Math.Max(0, bytesPerSec);
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        int u = 0;
        while (b >= 1024 && u < units.Length - 1)
        {
            b /= 1024;
            u++;
        }

        string val = u == 0
            ? $"{b:0}"
            : b >= 100 ? $"{b:0}"
            : b >= 10 ? string.Create(CultureInfo.InvariantCulture, $"{b:0.0}")
            : string.Create(CultureInfo.InvariantCulture, $"{b:0.0}");
        return (val, units[u]);
    }

    private static string FormatBitRate(double bytesPerSec)
    {
        var (v, u) = FormatBitRateParts(bytesPerSec);
        return v + " " + u;
    }

    private static string FormatByteRate(double bytesPerSec)
    {
        var (v, u) = FormatByteRateParts(bytesPerSec);
        return v + " " + u;
    }
}
