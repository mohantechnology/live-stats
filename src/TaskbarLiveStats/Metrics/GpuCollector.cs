using System.Runtime.InteropServices;
using TaskbarLiveStats.Native;

namespace TaskbarLiveStats.Metrics;

internal sealed class GpuCollector : IDisposable
{
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

    public GpuCollector()
    {
        InitUtilization();
        InitVram();
        _dxgiTotal = QueryDxgiGpuMemoryBudget();
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

    public (double GpuPercent, ulong VramUsed, ulong VramTotal, double VramPercent) Sample()
    {
        double gpu = SampleGpuPercent();
        ulong used = SampleVramUsed();
        ulong total = _dxgiTotal;
        if (total == 0 && used > 0)
            total = used;

        double pct = total == 0 ? 0 : Math.Clamp(100.0 * used / total, 0, 100);
        return (gpu, used, total, pct);
    }

    private double SampleGpuPercent()
    {
        if (!_utilReady) return 0;
        if (NativeMethods.PdhCollectQueryData(_utilQuery) != 0) return 0;
        if (!_utilPrimed)
        {
            _utilPrimed = true;
            return 0;
        }

        return GetCounterArrayMax(_utilCounter);
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

        // Task Manager GPU memory ≈ dedicated + shared usage (important for Intel iGPU)
        double dedicated = GetCounterArrayMaxBytes(_vramDedicatedCounter);
        double shared = _sharedReady ? GetCounterArrayMaxBytes(_vramSharedCounter) : 0;
        return (ulong)Math.Max(0, dedicated + shared);
    }

    /// <summary>
    /// Prefer engtype_3D engines (Task Manager style). Fall back to max across all engines.
    /// </summary>
    private static double GetCounterArrayMax(IntPtr counter)
    {
        if (!TryReadCounterArray(counter, out var items))
            return 0;

        double max3d = 0;
        double maxAll = 0;
        bool any3d = false;
        foreach (var (name, value) in items)
        {
            maxAll = Math.Max(maxAll, value);
            if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
            {
                any3d = true;
                max3d = Math.Max(max3d, value);
            }
        }

        return Math.Clamp(any3d ? max3d : maxAll, 0, 100);
    }

    private static double GetCounterArrayMaxBytes(IntPtr counter)
    {
        if (!TryReadCounterArray(counter, out var items))
            return 0;

        double max = 0;
        foreach (var (_, value) in items)
            max = Math.Max(max, value);
        return max;
    }

    private static bool TryReadCounterArray(IntPtr counter, out List<(string Name, double Value)> items)
    {
        items = [];
        uint bufSize = 0;
        uint itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArray(
            counter, NativeMethods.PDH_FMT_DOUBLE, ref bufSize, out itemCount, IntPtr.Zero);

        if (status != NativeMethods.PDH_MORE_DATA && status != 0)
            return false;
        if (bufSize == 0) return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArray(
                counter, NativeMethods.PDH_FMT_DOUBLE, ref bufSize, out itemCount, buffer);
            if (status != 0 || itemCount == 0) return false;

            int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (uint i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                    IntPtr.Add(buffer, (int)(i * (uint)stride)));
                if (item.FmtValue.CStatus != 0) continue;
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

    /// <summary>
    /// dGPU: dedicated VRAM. iGPU (small dedicated aperture): dedicated + shared system budget (TM-like).
    /// </summary>
    private static ulong QueryDxgiGpuMemoryBudget()
    {
        Guid iidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        int hr = CreateDXGIFactory1(ref iidFactory1, out IntPtr factory);
        if (hr != 0 || factory == IntPtr.Zero)
            return 0;

        try
        {
            const uint DxgiAdapterFlagSoftware = 2;
            const uint VendorMicrosoft = 0x1414;
            const ulong OneGb = 1024UL * 1024UL * 1024UL;
            ulong best = 0;
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
                    if (budget > best)
                        best = budget;
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }

            return best;
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
