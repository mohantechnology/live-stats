namespace TaskbarLiveStats.Metrics;

internal sealed class MetricsHub : IDisposable
{
    private readonly CpuCollector _cpu = new();
    private readonly MemoryCollector _memory = new();
    private readonly NetworkCollector _network = new();
    private readonly DiskCollector _disk = new();
    private readonly GpuCollector _gpu = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly object _gate = new();
    private bool _paused = true;
    private MetricsSnapshot _latest = MetricsSnapshot.Empty;

    public event Action<MetricsSnapshot>? Updated;

    public MetricsSnapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public MetricsHub(int intervalMs = 1000)
    {
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(500, intervalMs) };
        _timer.Tick += (_, _) => Collect();
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        _cpu.Reset();
        _network.Reset();
        _disk.Reset();
        _gpu.Reset();
        Collect();
        _timer.Start();
    }

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        _timer.Stop();
    }

    public bool IsPaused => _paused;

    private void Collect()
    {
        try
        {
            double cpu = _cpu.Sample();
            var (memUsed, memTotal, memPct) = _memory.Sample();
            var (send, recv) = _network.Sample();
            var (diskR, diskW) = _disk.Sample();
            var (gpu, vramUsed, vramTotal, vramPct) = _gpu.Sample();

            var snap = new MetricsSnapshot
            {
                CpuPercent = cpu,
                MemoryUsedBytes = memUsed,
                MemoryTotalBytes = memTotal,
                MemoryPercent = memPct,
                GpuPercent = gpu,
                VramUsedBytes = vramUsed,
                VramTotalBytes = vramTotal,
                VramPercent = vramPct,
                NetSendBytesPerSec = send,
                NetRecvBytesPerSec = recv,
                DiskReadBytesPerSec = diskR,
                DiskWriteBytesPerSec = diskW,
                Timestamp = DateTime.UtcNow
            };

            lock (_gate) _latest = snap;
            Updated?.Invoke(snap);
        }
        catch
        {
            // Keep last good snapshot; avoid crashing the UI timer.
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _disk.Dispose();
        _gpu.Dispose();
    }
}
