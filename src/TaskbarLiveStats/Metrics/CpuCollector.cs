using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

internal sealed class CpuCollector
{
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasPrev;

    public double Sample()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        ulong idleT = idle.ToUInt64();
        ulong kernelT = kernel.ToUInt64();
        ulong userT = user.ToUInt64();

        if (!_hasPrev)
        {
            _prevIdle = idleT;
            _prevKernel = kernelT;
            _prevUser = userT;
            _hasPrev = true;
            return 0;
        }

        ulong idleDelta = idleT - _prevIdle;
        ulong kernelDelta = kernelT - _prevKernel;
        ulong userDelta = userT - _prevUser;
        // Kernel includes idle time
        ulong total = kernelDelta + userDelta;
        ulong busy = total > idleDelta ? total - idleDelta : 0;

        _prevIdle = idleT;
        _prevKernel = kernelT;
        _prevUser = userT;

        if (total == 0) return 0;
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }

    public void Reset() => _hasPrev = false;
}
