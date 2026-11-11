using System.Runtime.InteropServices;

namespace TaskbarLiveStats.Native;

[StructLayout(LayoutKind.Explicit)]
internal struct PdhFmtCounterValue
{
    [FieldOffset(0)] public uint CStatus;
    [FieldOffset(8)] public double DoubleValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValueItem
{
    public IntPtr szName;
    public PdhFmtCounterValue FmtValue;
}

/// <summary>
/// Native PDH_RAW_COUNTER. For a bulk-count rate such as Disk Read Bytes/sec,
/// <see cref="FirstValue"/> is the cumulative count since boot.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PdhRawCounter
{
    public uint CStatus;
    public NativeMethods.FILETIME TimeStamp;
    public long FirstValue;
    public long SecondValue;
    public uint MultiCount;
}
