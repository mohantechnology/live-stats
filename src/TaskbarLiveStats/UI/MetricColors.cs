using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

internal static class MetricColors
{
    public static readonly (MetricKind Kind, string Name)[] All =
    [
        (MetricKind.Cpu, "CPU"),
        (MetricKind.Memory, "Memory"),
        (MetricKind.Gpu, "GPU"),
        (MetricKind.Vram, "VRAM"),
        (MetricKind.NetSend, "Upload"),
        (MetricKind.NetRecv, "Download"),
        (MetricKind.DiskRead, "Disk read"),
        (MetricKind.DiskWrite, "Disk write")
    ];

    public static Color Resolve(MetricPalette? palette, MetricKind kind)
    {
        palette ??= new MetricPalette();
        string hex = kind switch
        {
            MetricKind.Cpu => palette.Cpu,
            MetricKind.Memory => palette.Memory,
            MetricKind.Gpu => palette.Gpu,
            MetricKind.Vram => palette.Vram,
            MetricKind.NetSend => palette.NetSend,
            MetricKind.NetRecv => palette.NetRecv,
            MetricKind.DiskRead => palette.DiskRead,
            MetricKind.DiskWrite => palette.DiskWrite,
            _ => MetricPalette.DefaultCpu
        };
        string fallback = kind switch
        {
            MetricKind.Cpu => MetricPalette.DefaultCpu,
            MetricKind.Memory => MetricPalette.DefaultMemory,
            MetricKind.Gpu => MetricPalette.DefaultGpu,
            MetricKind.Vram => MetricPalette.DefaultVram,
            MetricKind.NetSend => MetricPalette.DefaultNetSend,
            MetricKind.NetRecv => MetricPalette.DefaultNetRecv,
            MetricKind.DiskRead => MetricPalette.DefaultDiskRead,
            MetricKind.DiskWrite => MetricPalette.DefaultDiskWrite,
            _ => MetricPalette.DefaultCpu
        };
        return MetricPalette.Parse(hex, fallback);
    }

    public static void Assign(MetricPalette palette, MetricKind kind, Color color)
    {
        string hex = MetricPalette.ToHex(color);
        switch (kind)
        {
            case MetricKind.Cpu: palette.Cpu = hex; break;
            case MetricKind.Memory: palette.Memory = hex; break;
            case MetricKind.Gpu: palette.Gpu = hex; break;
            case MetricKind.Vram: palette.Vram = hex; break;
            case MetricKind.NetSend: palette.NetSend = hex; break;
            case MetricKind.NetRecv: palette.NetRecv = hex; break;
            case MetricKind.DiskRead: palette.DiskRead = hex; break;
            case MetricKind.DiskWrite: palette.DiskWrite = hex; break;
        }
    }

    public static Color ContrastText(Color back)
    {
        double luminance = (0.2126 * back.R + 0.7152 * back.G + 0.0722 * back.B) / 255.0;
        return luminance > 0.62 ? Color.FromArgb(20, 20, 20) : Color.White;
    }
}
