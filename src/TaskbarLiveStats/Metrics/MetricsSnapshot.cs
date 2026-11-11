namespace TaskbarLiveStats.Metrics;

internal readonly record struct CoreUsage(int LogicalIndex, double Percent);

internal readonly record struct EngineUsage(string Name, double Percent);

internal readonly record struct NpuReading(bool Present, double Percent, ulong UsedBytes, ulong TotalBytes);

internal sealed class MetricsSnapshot
{
    public double CpuPercent { get; init; }
    public CoreUsage[] BusiestCores { get; init; } = [];
    public ulong MemoryUsedBytes { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public double MemoryPercent { get; init; }
    public double GpuPercent { get; init; }
    public EngineUsage[] BusiestEngines { get; init; } = [];
    public ulong VramUsedBytes { get; init; }
    public ulong VramTotalBytes { get; init; }
    public double VramPercent { get; init; }
    public bool NpuPresent { get; init; }
    public double NpuPercent { get; init; }
    public ulong NpuMemoryUsedBytes { get; init; }
    public ulong NpuMemoryTotalBytes { get; init; }
    public double NpuMemoryPercent { get; init; }
    public double NetSendBytesPerSec { get; init; }
    public double NetRecvBytesPerSec { get; init; }
    public ulong NetSendTotalBytes { get; init; }
    public ulong NetRecvTotalBytes { get; init; }
    public ulong NetSendStartupBytes { get; init; }
    public ulong NetRecvStartupBytes { get; init; }
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }
    public ulong DiskReadTotalBytes { get; init; }
    public ulong DiskWriteTotalBytes { get; init; }
    public ulong DiskReadStartupBytes { get; init; }
    public ulong DiskWriteStartupBytes { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static MetricsSnapshot Empty { get; } = new();
}

internal readonly record struct NetworkReading(
    double SendBps,
    double RecvBps,
    ulong SendBytes,
    ulong RecvBytes,
    bool TotalsValid);

internal readonly record struct DiskReading(
    double ReadBps,
    double WriteBps,
    ulong ReadBytes,
    ulong WriteBytes,
    bool TotalsValid);
