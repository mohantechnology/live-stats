namespace TaskbarLiveStats.Metrics;

internal sealed class MetricsSnapshot
{
    public double CpuPercent { get; init; }
    public ulong MemoryUsedBytes { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public double MemoryPercent { get; init; }
    public double GpuPercent { get; init; }
    public ulong VramUsedBytes { get; init; }
    public ulong VramTotalBytes { get; init; }
    public double VramPercent { get; init; }
    public double NetSendBytesPerSec { get; init; }
    public double NetRecvBytesPerSec { get; init; }
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static MetricsSnapshot Empty { get; } = new();
}
