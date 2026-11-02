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
