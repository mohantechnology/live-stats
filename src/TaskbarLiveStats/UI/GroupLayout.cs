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
    DiskWrite,
    Npu,
    NpuMemory
}

/// <summary>One usage block: icon + large value + small label, with an optional grid beside the label.</summary>
internal readonly record struct MetricBlock(
    MetricKind Kind,
    string IconGlyph,
    string Value,
    string Label,
    DetailGroup? Details = null);

/// <summary>Where a taller block sits in the taskbar slot. Extra rows grow away from the screen edge.</summary>
internal enum DetailAnchor
{
    Center,
    Top,
    Bottom
}

/// <summary>One fixed cell: name on the left, percent on the right.</summary>
internal readonly record struct DetailCell(string Name, string Percent);

/// <summary>Busiest-item grid drawn beside its parent label. Slots keep the size stable.</summary>
internal sealed class DetailGroup
{
    public string ReserveName { get; init; } = "C99";
    public string ReservePercent { get; init; } = "100%";
    public DetailCell[] Cells { get; init; } = [];
    public int Slots { get; init; }
}

internal sealed class OverlayFrame
{
    public MetricBlock[] Main { get; init; } = [];
    public static OverlayFrame Empty { get; } = new();
}

internal struct IdleWatch
{
    public bool Seen;
    public long IdleSince;
}

internal static class GroupLayout
{
    // Segoe Fluent Icons glyphs (fallback still works in MDL2 for older ones)
    private const string IconCpu = "\uE9D9";       // SpeedHigh / activity
    private const string IconMemory = "\uE950";    // MemoryDiag
    private const string IconGpu = "\uE7F4";       // Processing / chip
    private const string IconVram = "\uE950";
    private const string IconNpu = "\uE8C9";       // Robot
    private const string IconNetwork = "\uEC05";  // NetworkTower
    private const string IconDisk = "\uEDA2";      // HardDrive

    public const long IdleHideDelayMs = 10_000;

    public static OverlayFrame FromSnapshot(
        MetricsSnapshot s,
        AppSettings settings,
        Dictionary<MetricKind, IdleWatch>? idleWatches = null,
        bool advanceIdleWatch = true)
    {
        var m = settings.Metrics;
        var hide = settings.HideWhenIdle ?? new IdleHideToggles();
        var list = new List<MetricBlock>(10);
        bool useBits = settings.RateUnit == RateUnit.Bits;

        bool showCpu = Keep(MetricKind.Cpu, m.Cpu, s.CpuPercent <= 0, hide.Cpu, idleWatches, advanceIdleWatch);
        if (showCpu)
        {
            list.Add(new MetricBlock(
                MetricKind.Cpu,
                IconCpu,
                $"{UpperPercent(s.CpuPercent)}%",
                "CPU",
                settings.ShowUsageDetails ? CoreGroup(s, settings) : null));
        }

        if (Keep(MetricKind.Memory, m.Memory, s.MemoryUsedBytes == 0, hide.Memory, idleWatches, advanceIdleWatch))
        {
            string used = FormatGiBNumber(s.MemoryUsedBytes);
            string total = FormatGiBNumber(s.MemoryTotalBytes);
            list.Add(new MetricBlock(
                MetricKind.Memory,
                IconMemory,
                $"{used}/{total} GB",
                $"Memory ({UpperPercent(s.MemoryPercent)}%)"));
        }

        bool showGpu = Keep(MetricKind.Gpu, m.Gpu, s.GpuPercent <= 0, hide.Gpu, idleWatches, advanceIdleWatch);
        if (showGpu)
        {
            list.Add(new MetricBlock(
                MetricKind.Gpu,
                IconGpu,
                $"{UpperPercent(s.GpuPercent)}%",
                "GPU",
                settings.ShowUsageDetails ? EngineGroup(s, settings) : null));
        }

        if (Keep(MetricKind.Vram, m.Vram, s.VramUsedBytes == 0, hide.Vram, idleWatches, advanceIdleWatch))
            list.Add(MemoryBlock(MetricKind.Vram, IconVram, s.VramUsedBytes, s.VramTotalBytes, s.VramPercent, "VRAM"));

        if (s.NpuPresent && Keep(MetricKind.Npu, m.Npu, s.NpuPercent <= 0, hide.Npu, idleWatches, advanceIdleWatch))
            list.Add(new MetricBlock(MetricKind.Npu, IconNpu, $"{UpperPercent(s.NpuPercent)}%", "NPU"));

        if (s.NpuPresent && Keep(MetricKind.NpuMemory, m.NpuMemory, s.NpuMemoryUsedBytes == 0, hide.NpuMemory, idleWatches, advanceIdleWatch))
            list.Add(MemoryBlock(MetricKind.NpuMemory, IconVram, s.NpuMemoryUsedBytes, s.NpuMemoryTotalBytes, s.NpuMemoryPercent, "NPU mem"));

        if (Keep(MetricKind.NetSend, m.NetSend, s.NetSendBytesPerSec < 1, hide.NetSend, idleWatches, advanceIdleWatch))
        {
            var (val, unit) = FormatRateParts(s.NetSendBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.NetSend, IconNetwork, $"{val} {unit}", "Upload"));
        }

        if (Keep(MetricKind.NetRecv, m.NetRecv, s.NetRecvBytesPerSec < 1, hide.NetRecv, idleWatches, advanceIdleWatch))
        {
            var (val, unit) = FormatRateParts(s.NetRecvBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.NetRecv, IconNetwork, $"{val} {unit}", "Download"));
        }

        if (Keep(MetricKind.DiskRead, m.DiskRead, s.DiskReadBytesPerSec < 1, hide.DiskRead, idleWatches, advanceIdleWatch))
        {
            var (val, unit) = FormatRateParts(s.DiskReadBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.DiskRead, IconDisk, $"{val} {unit}", "Disk read"));
        }

        if (Keep(MetricKind.DiskWrite, m.DiskWrite, s.DiskWriteBytesPerSec < 1, hide.DiskWrite, idleWatches, advanceIdleWatch))
        {
            var (val, unit) = FormatRateParts(s.DiskWriteBytesPerSec, useBits);
            list.Add(new MetricBlock(MetricKind.DiskWrite, IconDisk, $"{val} {unit}", "Disk write"));
        }

        if (list.Count == 0)
            list.Add(new MetricBlock(MetricKind.Cpu, IconCpu, "—", "CPU"));

        return new OverlayFrame { Main = list.ToArray() };
    }

    private static MetricBlock MemoryBlock(
        MetricKind kind, string icon, ulong usedBytes, ulong totalBytes, double percent, string name)
    {
        string used = FormatGiBNumber(usedBytes);
        string total = totalBytes > 0 ? FormatGiBNumber(totalBytes) : "—";
        string label = totalBytes > 0 ? $"{name} ({UpperPercent(percent)}%)" : name;
        return new MetricBlock(
            kind,
            icon,
            totalBytes > 0 ? $"{used}/{total} GB" : $"{used} GB",
            label);
    }

    /// <summary>
    /// Show immediately once the value is above zero. After it drops to zero, keep it
    /// for 10 seconds so a quiet sample does not flicker the bar. A metric that has
    /// never been active stays hidden.
    /// </summary>
    private static bool Keep(
        MetricKind kind,
        bool enabled,
        bool idle,
        bool hideWhenIdle,
        Dictionary<MetricKind, IdleWatch>? watches,
        bool advance)
    {
        if (!enabled)
            return false;
        if (!hideWhenIdle || watches == null)
            return true;

        watches.TryGetValue(kind, out var watch);
        if (!idle)
        {
            watch.Seen = true;
            watch.IdleSince = 0;
            watches[kind] = watch;
            return true;
        }

        if (!watch.Seen)
        {
            watches[kind] = watch;
            return false;
        }

        long now = Environment.TickCount64;
        if (watch.IdleSince == 0)
        {
            if (!advance)
            {
                watches[kind] = watch;
                return true;
            }

            watch.IdleSince = now;
            watches[kind] = watch;
            return true;
        }

        watches[kind] = watch;
        return now - watch.IdleSince < IdleHideDelayMs;
    }

    private static DetailGroup? CoreGroup(MetricsSnapshot s, AppSettings settings)
    {
        if (s.BusiestCores.Length == 0)
            return null;

        int n = DetailCount(settings);
        int limit = Math.Min(n, s.BusiestCores.Length);
        var cells = new DetailCell[limit];
        for (int i = 0; i < limit; i++)
        {
            CoreUsage core = s.BusiestCores[i];
            cells[i] = new DetailCell(
                CoreTopology.Label(core.LogicalIndex),
                string.Create(CultureInfo.InvariantCulture, $"{Whole(core.Percent)}%"));
        }

        return new DetailGroup
        {
            ReserveName = CoreTopology.ReserveLabel,
            ReservePercent = "100%",
            Cells = cells,
            Slots = n
        };
    }

    private static DetailGroup? EngineGroup(MetricsSnapshot s, AppSettings settings)
    {
        if (s.BusiestEngines.Length == 0)
            return null;

        int n = DetailCount(settings);
        int limit = Math.Min(n, s.BusiestEngines.Length);
        var cells = new DetailCell[limit];
        for (int i = 0; i < limit; i++)
        {
            EngineUsage engine = s.BusiestEngines[i];
            cells[i] = new DetailCell(
                engine.Name,
                string.Create(CultureInfo.InvariantCulture, $"{Whole(engine.Percent)}%"));
        }

        return new DetailGroup
        {
            ReserveName = "Compute",
            ReservePercent = "100%",
            Cells = cells,
            Slots = n
        };
    }

    private static int DetailCount(AppSettings settings) => Math.Clamp(settings.UsageDetailCount, 1, 8);

    private static int Whole(double value)
    {
        if (value <= 0) return 0;
        if (value >= 100) return 100;
        return (int)value;
    }

    public static string Tooltip(MetricsSnapshot s, AppSettings? settings = null)
    {
        bool useBits = settings?.RateUnit == RateUnit.Bits;
        string npuLine = s.NpuPresent
            ? string.Create(CultureInfo.InvariantCulture,
                $"NPU {UpperPercent(s.NpuPercent)}%  NPU memory {FormatGiB(s.NpuMemoryUsedBytes)} / {FormatGiB(s.NpuMemoryTotalBytes)}\n")
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"CPU {UpperPercent(s.CpuPercent)}%\nMemory {FormatGiB(s.MemoryUsedBytes)} / {FormatGiB(s.MemoryTotalBytes)} ({UpperPercent(s.MemoryPercent)}%)\nGPU {UpperPercent(s.GpuPercent)}%\nGPU memory {FormatGiB(s.VramUsedBytes)} / {FormatGiB(s.VramTotalBytes)}\n{npuLine}Upload {FormatRate(s.NetSendBytesPerSec, useBits)}  Download {FormatRate(s.NetRecvBytesPerSec, useBits)}\nDisk read {FormatRate(s.DiskReadBytesPerSec, useBits)}  Disk write {FormatRate(s.DiskWriteBytesPerSec, useBits)}");
    }

    /// <summary>Stable placeholder used to size blocks so the overlay does not jitter.</summary>
    public static string ReserveValue(MetricKind kind) => kind switch
    {
        MetricKind.Memory or MetricKind.Vram or MetricKind.NpuMemory => "99.9/99.9 GB",
        MetricKind.NetSend or MetricKind.NetRecv or MetricKind.DiskRead or MetricKind.DiskWrite => "999.9 MB/s",
        _ => "100%"
    };

    public static string ReserveLabel(MetricKind kind) => kind switch
    {
        MetricKind.Memory => "Memory (100%)",
        MetricKind.Vram => "VRAM (100%)",
        MetricKind.Npu => "NPU",
        MetricKind.NpuMemory => "NPU mem (100%)",
        MetricKind.NetSend => "Upload",
        MetricKind.NetRecv => "Download",
        MetricKind.DiskRead => "Disk read",
        MetricKind.DiskWrite => "Disk write",
        MetricKind.Gpu => "GPU",
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
