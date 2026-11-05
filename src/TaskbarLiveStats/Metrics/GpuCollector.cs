using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

internal sealed class GpuCollector : IDisposable
{
    // luid_0x00000000_0x00012F3E_phys_0
    private static readonly Regex AdapterMemoryRegex = new(
        @"luid_0x(?<luidHigh>[0-9A-Fa-f]+)_0x(?<luidLow>[0-9A-Fa-f]+)_phys_(?<phys>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private IntPtr _utilQuery;
    private IntPtr _utilCounter;
    private IntPtr _vramQuery;
    private IntPtr _vramDedicatedCounter;
    private IntPtr _vramSharedCounter;
    private bool _utilReady;
    private bool _vramReady;
    private bool _sharedReady;
    private bool _utilPrimed;
    private bool _vramPrimed;
    private ulong _dxgiTotal;
    private string? _primaryLuidPrefix; // "high_low" matching PDH instance names
    private double _lastGpuPercent;
    private EngineUsage[] _lastEngines = [];

    public GpuCollector()
    {
        InitUtilization();
        InitVram();
        (_dxgiTotal, _primaryLuidPrefix) = QueryDxgiPrimaryAdapter();
    }

    private void InitUtilization()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _utilQuery) != 0)
            return;

        uint r = NativeMethods.PdhAddEnglishCounter(
            _utilQuery,
            @"\GPU Engine(*)\Utilization Percentage",
            IntPtr.Zero,
            out _utilCounter);

        _utilReady = r == 0;
        if (_utilReady)
            NativeMethods.PdhCollectQueryData(_utilQuery);
    }

    private void InitVram()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _vramQuery) != 0)
            return;

        uint r1 = NativeMethods.PdhAddEnglishCounter(
            _vramQuery,
            @"\GPU Adapter Memory(*)\Dedicated Usage",
            IntPtr.Zero,
            out _vramDedicatedCounter);

        uint r2 = NativeMethods.PdhAddEnglishCounter(
            _vramQuery,
            @"\GPU Adapter Memory(*)\Shared Usage",
            IntPtr.Zero,
            out _vramSharedCounter);

        _vramReady = r1 == 0;
        _sharedReady = r2 == 0;
        if (_vramReady)
            NativeMethods.PdhCollectQueryData(_vramQuery);
    }

    public (double GpuPercent, ulong VramUsed, ulong VramTotal, double VramPercent, EngineUsage[] Engines) Sample()
    {
        var (gpu, engines) = SampleGpuPercent();
        ulong used = SampleVramUsed();
        ulong total = _dxgiTotal;
        if (total == 0 && used > 0)
            total = used;

        double pct = total == 0 ? 0 : Math.Clamp(100.0 * used / total, 0, 100);
        return (gpu, used, total, pct, engines);
    }

    private (double Percent, EngineUsage[] Engines) SampleGpuPercent()
    {
        if (!_utilReady) return (_lastGpuPercent, _lastEngines);
        if (NativeMethods.PdhCollectQueryData(_utilQuery) != 0)
            return (_lastGpuPercent, _lastEngines);
        if (!_utilPrimed)
        {
            _utilPrimed = true;
            return (_lastGpuPercent, _lastEngines);
        }

        if (!PdhReader.TryReadCounterArray(_utilCounter, out var items))
            return (_lastGpuPercent, _lastEngines);

        var (raw, engines) = EngineUtilization.Aggregate(items, _primaryLuidPrefix);
        _lastGpuPercent = raw;
        _lastEngines = engines;
        return (raw, engines);
    }

    private ulong SampleVramUsed()
    {
        if (!_vramReady) return 0;
        if (NativeMethods.PdhCollectQueryData(_vramQuery) != 0) return 0;
        if (!_vramPrimed)
        {
            _vramPrimed = true;
            return 0;
        }

        // Pair dedicated + shared per adapter. Prefer the primary (largest) GPU.
        var dedicatedByAdapter = ReadAdapterMemoryByLuid(_vramDedicatedCounter);
        var sharedByAdapter = _sharedReady
            ? ReadAdapterMemoryByLuid(_vramSharedCounter)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (_primaryLuidPrefix != null)
        {
            foreach (var key in dedicatedByAdapter.Keys.Union(sharedByAdapter.Keys))
            {
                if (!key.StartsWith(_primaryLuidPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                dedicatedByAdapter.TryGetValue(key, out double d);
                sharedByAdapter.TryGetValue(key, out double s);
                return (ulong)Math.Max(0, d + s);
            }
        }

        double best = 0;
        foreach (var key in dedicatedByAdapter.Keys.Union(sharedByAdapter.Keys))
        {
            dedicatedByAdapter.TryGetValue(key, out double d);
            sharedByAdapter.TryGetValue(key, out double s);
            best = Math.Max(best, d + s);
        }

        return (ulong)Math.Max(0, best);
    }

    private static Dictionary<string, double> ReadAdapterMemoryByLuid(IntPtr counter)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!PdhReader.TryReadCounterArray(counter, out var items))
            return map;

        foreach (var (name, value) in items)
        {
            var match = AdapterMemoryRegex.Match(name);
            if (!match.Success) continue;

            string key = string.Create(CultureInfo.InvariantCulture,
                $"{match.Groups["luidHigh"].Value}_{match.Groups["luidLow"].Value}_{match.Groups["phys"].Value}");
            // One instance per adapter; keep max defensively if duplicates appear.
            map[key] = Math.Max(map.GetValueOrDefault(key), value);
        }

        return map;
    }

    /// <summary>
    /// Picks the primary hardware GPU (largest budget). dGPU: dedicated VRAM.
    /// iGPU (small dedicated aperture): dedicated + shared system budget (TM-like).
    /// Also returns PDH LUID prefix "high_low" for matching engine counters.
    /// </summary>
    private static (ulong Budget, string? LuidPrefix) QueryDxgiPrimaryAdapter()
    {
        Guid iidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        int hr = CreateDXGIFactory1(ref iidFactory1, out IntPtr factory);
        if (hr != 0 || factory == IntPtr.Zero)
            return (0, null);

        try
        {
            const uint DxgiAdapterFlagSoftware = 2;
            const uint VendorMicrosoft = 0x1414;
            const ulong OneGb = 1024UL * 1024UL * 1024UL;
            ulong best = 0;
            string? bestLuid = null;
            for (uint i = 0; i < 8; i++)
            {
                IntPtr adapter = EnumAdapters1(factory, i);
                if (adapter == IntPtr.Zero) break;
                try
                {
                    GetAdapterDesc1(adapter, out var desc);
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0)
                        continue;
                    if (desc.VendorId == VendorMicrosoft)
                        continue;
                    string name = desc.Description ?? string.Empty;
                    if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Microsoft Remote", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ulong dedicated = unchecked((ulong)desc.DedicatedVideoMemory);
                    ulong shared = unchecked((ulong)desc.SharedSystemMemory);
                    // Discrete cards: dedicated only. iGPU aperture (~128MB): include shared budget.
                    ulong budget = dedicated >= OneGb ? dedicated : dedicated + shared;
                    if (budget <= best) continue;

                    best = budget;
                    // DXGI LUID is LowPart (u32) + HighPart (i32). PDH uses high then low hex.
                    uint low = unchecked((uint)(desc.AdapterLuid & 0xFFFFFFFF));
                    uint high = unchecked((uint)((ulong)desc.AdapterLuid >> 32));
                    bestLuid = string.Create(CultureInfo.InvariantCulture, $"{high:x8}_{low:x8}");
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }

            return (best, bestLuid);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    private static IntPtr EnumAdapters1(IntPtr factory, uint index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr method = Marshal.ReadIntPtr(vtable, 12 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(method);
        int hr = fn(factory, index, out IntPtr adapter);
        return hr == 0 ? adapter : IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr thisPtr, uint adapter, out IntPtr ppAdapter);

    private static void GetAdapterDesc1(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc)
    {
        IntPtr vtable = Marshal.ReadIntPtr(adapter);
        IntPtr method = Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(method);
        fn(adapter, out desc);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr thisPtr, out DXGI_ADAPTER_DESC1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    public void Reset()
    {
        _utilPrimed = false;
        _vramPrimed = false;
        // Keep the last percent and engine list so the UI does not flash 0 across a reset gap.
        if (_utilReady) NativeMethods.PdhCollectQueryData(_utilQuery);
        if (_vramReady) NativeMethods.PdhCollectQueryData(_vramQuery);
    }

    public void Dispose()
    {
        if (_utilQuery != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_utilQuery);
            _utilQuery = IntPtr.Zero;
        }

        if (_vramQuery != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_vramQuery);
            _vramQuery = IntPtr.Zero;
        }
    }
}
