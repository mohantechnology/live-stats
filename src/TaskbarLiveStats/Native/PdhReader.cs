using System.Runtime.InteropServices;

namespace TaskbarLiveStats.Native;

internal static class PdhReader
{
    public static double ReadDouble(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
            return double.NaN;
        if (NativeMethods.PdhGetFormattedCounterValue(counter, NativeMethods.PDH_FMT_DOUBLE, IntPtr.Zero, out var value) != 0)
            return double.NaN;
        if (value.CStatus != 0)
            return double.NaN;
        return value.DoubleValue;
    }

    public static bool TryReadCounterArray(IntPtr counter, out List<(string Name, double Value)> items)
    {
        items = [];
        if (counter == IntPtr.Zero)
            return false;

        uint bufSize = 0;
        uint itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArray(
            counter, NativeMethods.PDH_FMT_DOUBLE, ref bufSize, out itemCount, IntPtr.Zero);

        if (status != NativeMethods.PDH_MORE_DATA && status != 0)
            return false;
        if (bufSize == 0)
            return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArray(
                counter, NativeMethods.PDH_FMT_DOUBLE, ref bufSize, out itemCount, buffer);
            if (status != 0 || itemCount == 0)
                return false;

            int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (uint i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                    IntPtr.Add(buffer, (int)(i * (uint)stride)));
                if (item.FmtValue.CStatus != 0)
                    continue;
                string name = item.szName != IntPtr.Zero
                    ? (Marshal.PtrToStringUni(item.szName) ?? string.Empty)
                    : string.Empty;
                items.Add((name, Math.Max(0, item.FmtValue.DoubleValue)));
            }

            return items.Count > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
