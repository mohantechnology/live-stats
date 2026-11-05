using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// NPU utilization and memory from the Windows 11 Task Manager counters.
/// When those counters are absent the collector reports not present, and the
/// tiles stay off the bar.
/// </summary>
internal sealed class NpuCollector : IDisposable
{
    private static readonly Regex AdapterMemoryRegex = new(
        @"luid_0x(?<luidHigh>[0-9A-Fa-f]+)_0x(?<luidLow>[0-9A-Fa-f]+)_phys_(?<phys>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private IntPtr _query;
    private IntPtr _utilCounter;
    private IntPtr _dedicatedCounter;
    private IntPtr _sharedCounter;
    private bool _utilReady;
    private bool _dedicatedReady;
    private bool _sharedReady;
    private bool _primed;
    private readonly ulong _total;
    private readonly string? _luidPrefix;
    private NpuReading _last;

    public NpuCollector()
    {
        _total = 0;
        _luidPrefix = null;

        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        _utilReady = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\NPU Engine(*)\Utilization Percentage",
            IntPtr.Zero,
            out _utilCounter) == 0;

        _dedicatedReady = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\NPU Adapter Memory(*)\Dedicated Usage",
            IntPtr.Zero,
            out _dedicatedCounter) == 0;

        if (!_dedicatedReady)
        {
            _dedicatedReady = NativeMethods.PdhAddEnglishCounter(
                _query,
                @"\NPU Adapter Memory(*)\Local Usage",
                IntPtr.Zero,
                out _dedicatedCounter) == 0;
        }

        _sharedReady = NativeMethods.PdhAddEnglishCounter(
            _query,
            @"\NPU Adapter Memory(*)\Shared Usage",
            IntPtr.Zero,
            out _sharedCounter) == 0;

        if (!_utilReady && !_dedicatedReady && !_sharedReady)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }

        (_total, _luidPrefix) = QueryNpuBudget();
        _last = new NpuReading(true, 0, 0, _total);
        NativeMethods.PdhCollectQueryData(_query);
    }

    public bool IsPresent => _query != IntPtr.Zero;

    public NpuReading Sample()
    {
        if (!IsPresent)
            return default;

        if (NativeMethods.PdhCollectQueryData(_query) != 0)
            return _last;

        if (!_primed)
        {
            _primed = true;
            return _last;
        }

        double percent = _last.Percent;
        if (_utilReady && PdhReader.TryReadCounterArray(_utilCounter, out var items))
            percent = EngineUtilization.Aggregate(items, _luidPrefix).Percent;

        ulong used = ReadUsed();
        _last = new NpuReading(true, percent, used, _total);
        return _last;
    }

    public void Reset()
    {
        _primed = false;
        if (IsPresent)
            NativeMethods.PdhCollectQueryData(_query);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private ulong ReadUsed()
    {
        var dedicated = _dedicatedReady
            ? ReadAdapterMemory(_dedicatedCounter)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var shared = _sharedReady
            ? ReadAdapterMemory(_sharedCounter)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (dedicated.Count == 0 && shared.Count == 0)
            return _last.UsedBytes;

        if (_luidPrefix != null)
        {
            foreach (var key in dedicated.Keys.Union(shared.Keys))
            {
                if (!key.StartsWith(_luidPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                dedicated.TryGetValue(key, out double d);
                shared.TryGetValue(key, out double s);
                return (ulong)Math.Max(0, d + s);
            }
        }

        double best = 0;
        foreach (var key in dedicated.Keys.Union(shared.Keys))
        {
            dedicated.TryGetValue(key, out double d);
            shared.TryGetValue(key, out double s);
            best = Math.Max(best, d + s);
        }

        return (ulong)Math.Max(0, best);
    }

    private static Dictionary<string, double> ReadAdapterMemory(IntPtr counter)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!PdhReader.TryReadCounterArray(counter, out var items))
            return map;

        foreach (var (name, value) in items)
        {
            var match = AdapterMemoryRegex.Match(name);
            if (!match.Success)
                continue;

            string key = string.Create(CultureInfo.InvariantCulture,
                $"{match.Groups["luidHigh"].Value}_{match.Groups["luidLow"].Value}_{match.Groups["phys"].Value}");
            map[key] = Math.Max(map.GetValueOrDefault(key), value);
        }

        return map;
    }

    /// <summary>
    /// DXCore NPU adapter budget. Discrete: dedicated only. Small aperture: dedicated + shared.
    /// </summary>
    private static (ulong Budget, string? LuidPrefix) QueryNpuBudget()
    {
        try
        {
            return QueryNpuBudgetCore();
        }
        catch
        {
            return (0, null);
        }
    }

    private static (ulong Budget, string? LuidPrefix) QueryNpuBudgetCore()
    {
        Guid iidFactory = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
        int hr = DXCoreCreateAdapterFactory(ref iidFactory, out IntPtr factory);
        if (hr != 0 || factory == IntPtr.Zero)
            return (0, null);

        IntPtr list = IntPtr.Zero;
        try
        {
            Guid npuAttribute = new("d46140c4-add7-451b-9e56-06fe8c3b58ed");
            list = CreateAdapterList(factory, npuAttribute);
            if (list == IntPtr.Zero)
                return (0, null);

            uint count = GetAdapterCount(list);
            const ulong OneGb = 1024UL * 1024UL * 1024UL;
            ulong best = 0;
            string? bestLuid = null;
            bool any = false;
            uint limit = Math.Min(count, 8);
            for (uint i = 0; i < limit; i++)
            {
                IntPtr adapter = GetAdapter(list, i);
                if (adapter == IntPtr.Zero)
                    continue;
                try
                {
                    ulong dedicated = ReadUInt64(adapter, 7);
                    ulong shared = ReadUInt64(adapter, 9);
                    ulong budget = dedicated >= OneGb ? dedicated : dedicated + shared;
                    string? luid = ReadLuidPrefix(adapter);
                    if (!any || budget > best)
                    {
                        any = true;
                        best = budget;
                        bestLuid = luid;
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }

            return any ? (best, bestLuid) : (0, null);
        }
        finally
        {
            if (list != IntPtr.Zero)
                Marshal.Release(list);
            Marshal.Release(factory);
        }
    }

    [DllImport("dxcore.dll", ExactSpelling = true)]
    private static extern int DXCoreCreateAdapterFactory(ref Guid riid, out IntPtr ppFactory);

    private static IntPtr CreateAdapterList(IntPtr factory, Guid attribute)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr method = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<CreateAdapterListDelegate>(method);
        IntPtr attrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(attribute, attrPtr, false);
            Guid iidList = new("526c7776-40e9-459b-b711-f32ad76dfc28");
            int hr = fn(factory, 1, attrPtr, ref iidList, out IntPtr list);
            return hr == 0 ? list : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(attrPtr);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateAdapterListDelegate(
        IntPtr thisPtr, uint numAttributes, IntPtr filterAttributes, ref Guid riid, out IntPtr ppv);

    private static uint GetAdapterCount(IntPtr list)
    {
        IntPtr vtable = Marshal.ReadIntPtr(list);
        IntPtr method = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetAdapterCountDelegate>(method);
        return fn(list);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAdapterCountDelegate(IntPtr thisPtr);

    private static IntPtr GetAdapter(IntPtr list, uint index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(list);
        IntPtr method = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetAdapterDelegate>(method);
        Guid iidAdapter = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        int hr = fn(list, index, ref iidAdapter, out IntPtr adapter);
        return hr == 0 ? adapter : IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDelegate(IntPtr thisPtr, uint index, ref Guid riid, out IntPtr ppv);

    private static ulong ReadUInt64(IntPtr adapter, uint property)
    {
        IntPtr vtable = Marshal.ReadIntPtr(adapter);
        IntPtr method = Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetPropertyDelegate>(method);
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            int hr = fn(adapter, property, (nuint)8, buffer);
            return hr == 0 ? (ulong)Marshal.ReadInt64(buffer) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadLuidPrefix(IntPtr adapter)
    {
        IntPtr vtable = Marshal.ReadIntPtr(adapter);
        IntPtr method = Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetPropertyDelegate>(method);
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            int hr = fn(adapter, 0, (nuint)8, buffer);
            if (hr != 0)
                return null;
            var luid = Marshal.PtrToStructure<Luid>(buffer);
            uint high = unchecked((uint)luid.HighPart);
            return string.Create(CultureInfo.InvariantCulture, $"{high:x8}_{luid.LowPart:x8}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertyDelegate(IntPtr thisPtr, uint property, nuint bufferSize, IntPtr propertyData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }
}
