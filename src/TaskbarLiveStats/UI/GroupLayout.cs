using System.Globalization;
using TaskbarLiveStats.Metrics;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

internal enum MetricKind
{
    Cpu,
    Memory,
    Gpu,
    Vram,
    NetSend,
    NetRecv,
    DiskRead,
    DiskWrite
}

/// <summary>One usage block: icon + large value + small label.</summary>
internal readonly record struct MetricBlock(MetricKind Kind, string IconGlyph, string Value, string Label);

internal static class GroupLayout
{
    // Segoe Fluent Icons glyphs (fallback still works in MDL2 for older ones)
    private const string IconCpu = "\uE9D9";       // SpeedHigh / activity
    private const string IconMemory = "\uE950";    // MemoryDiag
    private const string IconGpu = "\uE7F4";       // Processing / chip
    private const string IconVram = "\uE950";
    private const string IconNetwork = "\uEC05";  // NetworkTower
    private const string IconDisk = "\uEDA2";      // HardDrive

    public static MetricBlock[] FromSnapshot(MetricsSnapshot s, AppSettings settings)
    {
        var m = settings.Metrics;
        var list = new List<MetricBlock>(8);
        bool useBits = settings.RateUnit == RateUnit.Bits;

        if (m.Cpu)
            list.Add(new MetricBlock(MetricKind.Cpu, IconCpu, $"{UpperPercent(s.CpuPercent)}%", "CPU"));

        if (m.Memory)
        {
            string used = FormatGiBNumber(s.MemoryUsedBytes);
            string total = FormatGiBNumber(s.MemoryTotalBytes);
            list.Add(new MetricBlock(
                MetricKind.Memory,
                IconMemory,
                $"{used}/{total} GB",
                $"Memory ({UpperPercent(s.MemoryPercent)}%)"));
        }

        if (m.Gpu)
            list.Add(new MetricBlock(MetricKind.Gpu, IconGpu, $"{UpperPercent(s.GpuPercent)}%", "GPU"));

        if (m.Vram)
        {
            string used = FormatGiBNumber(s.VramUsedBytes);
            string total = s.VramTotalBytes > 0 ? FormatGiBNumber(s.VramTotalBytes) : "—";
            list.Add(new MetricBlock(
                MetricKind.Vram,
                IconVram,
                s.VramTotalBytes > 0 ? $"{used}/{total} GB" : $"{used} GB/—",
                $"VRAM ({UpperPercent(s.VramPercent)}%)"));
        }

        if (m.NetSend)
        {
            var (val, unit) = FormatRateParts(s.NetSendBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.NetSend, IconNetwork, $"{val} {unit}", "Send"));
        }

        if (m.NetRecv)
        {
            var (val, unit) = FormatRateParts(s.NetRecvBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.NetRecv, IconNetwork, $"{val} {unit}", "Receive"));
        }

        if (m.DiskRead)
        {
            var (val, unit) = FormatRateParts(s.DiskReadBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.DiskRead, IconDisk, $"{val} {unit}", "Disk R"));
        }

        if (m.DiskWrite)
        {
            var (val, unit) = FormatRateParts(s.DiskWriteBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.DiskWrite, IconDisk, $"{val} {unit}", "Disk W"));
        }

        if (list.Count == 0)
            list.Add(new MetricBlock(MetricKind.Cpu, IconCpu, "—", "CPU"));

        return list.ToArray();
    }

    public static string Tooltip(MetricsSnapshot s, AppSettings? settings = null)
    {
        bool useBits = settings?.RateUnit == RateUnit.Bits;
        return string.Create(CultureInfo.InvariantCulture,
            $"CPU {UpperPercent(s.CpuPercent)}%\n" +
            $"Memory {FormatGiB(s.MemoryUsedBytes)} / {FormatGiB(s.MemoryTotalBytes)} ({UpperPercent(s.MemoryPercent)}%)\n" +
            $"GPU {UpperPercent(s.GpuPercent)}%\n" +
            $"GPU memory {FormatGiB(s.VramUsedBytes)} / {FormatGiB(s.VramTotalBytes)}\n" +
            $"Send {FormatRate(s.NetSendBytesPerSec, useBits)}  Recv {FormatRate(s.NetRecvBytesPerSec, useBits)}\n" +
            $"Disk R {FormatRate(s.DiskReadBytesPerSec, useBits)}  W {FormatRate(s.DiskWriteBytesPerSec, useBits)}");
    }

    /// <summary>Stable placeholder used to size blocks so the overlay does not jitter.</summary>
    public static string ReserveValue(MetricKind kind) => kind switch
    {
        MetricKind.Memory or MetricKind.Vram => "99.9/99.9 GB",
        MetricKind.NetSend or MetricKind.NetRecv or MetricKind.DiskRead or MetricKind.DiskWrite => "999.9 MB/s",
        _ => "100%"
    };

    public static string ReserveLabel(MetricKind kind) => kind switch
    {
        MetricKind.Memory => "Memory (100%)",
        MetricKind.Vram => "VRAM (100%)",
        MetricKind.NetRecv => "Receive",
        MetricKind.DiskRead => "Disk R",
        MetricKind.DiskWrite => "Disk W",
        _ => "CPU"
    };

    /// <summary>Round percentages up so small load never displays as a false 0%.</summary>
    private static int UpperPercent(double value)
    {
        if (value <= 0) return 0;
        if (value >= 100) return 100;
        return (int)Math.Ceiling(value);
    }

    private static string FormatGiB(ulong bytes) => FormatGiBNumber(bytes) + " GB";

    private static string FormatGiBNumber(ulong bytes)
    {
        double gib = bytes / (1024.0 * 1024.0 * 1024.0);
        if (gib >= 100) return string.Create(CultureInfo.InvariantCulture, $"{gib:0}");
        if (gib >= 10) return string.Create(CultureInfo.InvariantCulture, $"{gib:0.0}");
        return string.Create(CultureInfo.InvariantCulture, $"{gib:0.00}");
    }

    private static (string Value, string Unit) FormatRateParts(double bytesPerSec, bool useBits)
    {
        if (useBits)
        {
            double bits = Math.Max(0, bytesPerSec) * 8.0;
            string[] units = ["bps", "Kbps", "Mbps", "Gbps"];
            int u = 0;
            while (bits >= 1000 && u < units.Length - 1)
            {
                bits /= 1000;
                u++;
            }

            return (FormatRateNumber(bits, u), units[u]);
        }
        else
        {
            double b = Math.Max(0, bytesPerSec);
            string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
            int u = 0;
            while (b >= 1024 && u < units.Length - 1)
            {
                b /= 1024;
                u++;
            }

            return (FormatRateNumber(b, u), units[u]);
        }
    }

    private static string FormatRateNumber(double n, int unitIndex)
    {
        if (unitIndex == 0) return $"{n:0}";
        if (n >= 100) return $"{n:0}";
        if (n >= 10) return string.Create(CultureInfo.InvariantCulture, $"{n:0.0}");
        return string.Create(CultureInfo.InvariantCulture, $"{n:0.0}");
    }

    private static string FormatRate(double bytesPerSec, bool useBits)
    {
        var (v, u) = FormatRateParts(bytesPerSec, useBits);
        return v + " " + u;
    }
}
