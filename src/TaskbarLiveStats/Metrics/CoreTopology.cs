using System.Globalization;
using System.Runtime.InteropServices;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Maps a logical processor to P / E / LP when Windows reports more than one
/// efficiency class. A single class stays "C", because there is nothing to distinguish.
/// </summary>
internal static class CoreTopology
{
    private const int RelationProcessorCore = 0;

    private static readonly int[] GroupBases = BuildGroupBases();
    private static readonly Dictionary<int, byte> Classes = LoadClasses();
    private static readonly byte MinClass;
    private static readonly byte MaxClass;
    private static readonly int ClassCount;
    private static readonly bool Hybrid;

    public static string ReserveLabel { get; }

    static CoreTopology()
    {
        MinClass = 0;
        MaxClass = 0;
        ClassCount = 0;
        Hybrid = false;
        if (Classes.Count > 0)
        {
            MinClass = byte.MaxValue;
            var seen = new HashSet<byte>();
            foreach (byte cls in Classes.Values)
            {
                seen.Add(cls);
                if (cls < MinClass) MinClass = cls;
                if (cls > MaxClass) MaxClass = cls;
            }

            ClassCount = seen.Count;
            Hybrid = ClassCount > 1;
        }

        int widest = 9;
        foreach (int index in Classes.Keys)
            widest = Math.Max(widest, index);
        string digits = widest >= 100 ? "999" : widest >= 10 ? "99" : "9";
        ReserveLabel = (Hybrid ? "P" : "C") + digits;
    }

    public static int FlatIndex(int group, int number)
    {
        if ((uint)group < (uint)GroupBases.Length)
            return GroupBases[group] + number;
        return group * 64 + number;
    }

    public static string Label(int logicalIndex)
    {
        char prefix = 'C';
        if (Hybrid && Classes.TryGetValue(logicalIndex, out byte cls))
            prefix = Prefix(cls);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{logicalIndex}");
    }

    /// <summary>Higher efficiency class is the more performant core.</summary>
    private static char Prefix(byte cls)
    {
        if (cls == MaxClass)
            return 'P';
        if (ClassCount >= 3 && cls == MinClass)
            return 'L';
        return 'E';
    }

    private static Dictionary<int, byte> LoadClasses()
    {
        var map = new Dictionary<int, byte>();
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
            return map;

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return map;

            int offset = 0;
            int total = (int)length;
            while (offset + 8 <= total)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > total)
                    break;

                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX + PROCESSOR_RELATIONSHIP.
                // EfficiencyClass is the byte after Flags. Group masks follow GroupCount.
                if (relationship == RelationProcessorCore && size >= 32)
                {
                    byte efficiency = Marshal.ReadByte(buffer, offset + 9);
                    ushort groupCount = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                    int maskAt = offset + 32;
                    for (int i = 0; i < groupCount; i++)
                    {
                        if (maskAt + 16 > offset + size)
                            break;

                        ulong mask = (ulong)Marshal.ReadInt64(buffer, maskAt);
                        ushort group = (ushort)Marshal.ReadInt16(buffer, maskAt + 8);
                        for (int bit = 0; bit < 64; bit++)
                        {
                            if ((mask & (1UL << bit)) == 0)
                                continue;
                            map[FlatIndex(group, bit)] = efficiency;
                        }

                        maskAt += 16;
                    }
                }

                offset += size;
            }
        }
        catch
        {
            map.Clear();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }

    private static int[] BuildGroupBases()
    {
        ushort groups = GetActiveProcessorGroupCount();
        if (groups == 0)
            groups = 1;

        var bases = new int[groups];
        int acc = 0;
        for (ushort g = 0; g < groups; g++)
        {
            bases[g] = acc;
            uint n = GetActiveProcessorCount(g);
            acc += n == 0 ? 64 : (int)n;
        }

        return bases;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int RelationshipType,
        IntPtr Buffer,
        ref uint ReturnedLength);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort GroupNumber);
}
