namespace TaskbarLiveStats.Metrics;

internal sealed class MetricsHub : IDisposable
{
    private readonly CpuCollector _cpu = new();
    private readonly MemoryCollector _memory = new();
    private readonly NetworkCollector _network = new();
    private readonly DiskCollector _disk = new();
    private readonly GpuCollector _gpu = new();
    private readonly System.Threading.Timer _timer;
    private readonly SynchronizationContext? _ui;
    private readonly object _gate = new();
    private int _sampling;
    private bool _paused = true;
    private bool _hasSample;
    private bool _resetRequested;
    private long _pausedAt;
    private int _intervalMs;
    private MetricsSnapshot _latest = MetricsSnapshot.Empty;

    public event Action<MetricsSnapshot>? Updated;

    public MetricsSnapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public MetricsHub(int intervalMs = 1000)
    {
        _ui = SynchronizationContext.Current;
        _intervalMs = Math.Clamp(intervalMs, 100, 10_000);
        _timer = new System.Threading.Timer(_ => Collect(), null, Timeout.Infinite, _intervalMs);
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            int next = Math.Clamp(value, 100, 10_000);
            if (next == _intervalMs) return;
            _intervalMs = next;
            if (!_paused)
                _timer.Change(_intervalMs, _intervalMs);
        }
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;

        // A long gap makes the next rate sample meaningless. Prime off the UI
        // thread and keep showing the last frame (or zeros) until it arrives.
        if (!_hasSample || Environment.TickCount64 - _pausedAt > 10_000)
            _resetRequested = true;

        Deliver(_hasSample ? Latest : MetricsSnapshot.Empty, synchronous: true);
        _timer.Change(150, _intervalMs);
    }

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        _pausedAt = Environment.TickCount64;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsPaused => _paused;

    private void Collect()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
            return;

        try
        {
            if (_paused) return;

            if (_resetRequested)
            {
                _resetRequested = false;
                _cpu.Reset();
                _network.Reset();
                _disk.Reset();
                _gpu.Reset();
                // Arm rate counters. Don't publish this priming read — it is
                // zeros and would blank numbers the UI is already showing.
                _ = _cpu.Sample();
                _ = _memory.Sample();
                _ = _network.Sample();
                _ = _disk.Sample();
                _ = _gpu.Sample();
                if (!_paused)
                    _timer.Change(300, _intervalMs);
                return;
            }

            double cpu = _cpu.Sample();
            var (memUsed, memTotal, memPct) = _memory.Sample();
            var (send, recv) = _network.Sample();
            var (diskR, diskW) = _disk.Sample();
            var (gpu, vramUsed, vramTotal, vramPct) = _gpu.Sample();

            if (_paused) return;

            // Store ceiling percentages so every consumer sees the upper integer value
            // (avoids rounding 0.4% down to a false "0%").
            var snap = new MetricsSnapshot
            {
                CpuPercent = CeilPercent(cpu),
                MemoryUsedBytes = memUsed,
                MemoryTotalBytes = memTotal,
                MemoryPercent = CeilPercent(memPct),
                GpuPercent = CeilPercent(gpu),
                VramUsedBytes = vramUsed,
                VramTotalBytes = vramTotal,
                VramPercent = CeilPercent(vramPct),
                NetSendBytesPerSec = send,
                NetRecvBytesPerSec = recv,
                DiskReadBytesPerSec = diskR,
                DiskWriteBytesPerSec = diskW,
                Timestamp = DateTime.UtcNow
            };

            lock (_gate) _latest = snap;
            _hasSample = true;
            Deliver(snap, synchronous: false);
        }
        catch
        {
            // Keep last good snapshot; avoid crashing the sample timer.
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private void Deliver(MetricsSnapshot snap, bool synchronous)
    {
        void Go()
        {
            try { Updated?.Invoke(snap); }
            catch { /* UI may be tearing down */ }
        }

        if (synchronous || _ui == null || SynchronizationContext.Current == _ui)
            Go();
        else
            _ui.Post(_ => Go(), null);
    }

    public void Dispose()
    {
        _paused = true;
        _timer.Dispose();
        _disk.Dispose();
        _gpu.Dispose();
    }

    private static double CeilPercent(double value)
    {
        if (value <= 0) return 0;
        if (value >= 100) return 100;
        return Math.Ceiling(value);
    }
}
