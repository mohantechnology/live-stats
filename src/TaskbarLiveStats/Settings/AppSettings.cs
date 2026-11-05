namespace TaskbarLiveStats.Settings;

internal enum OverlayOrientation
{
    Horizontal,
    Vertical
}

internal enum PlacementMode
{
    Taskbar,
    Floating
}

internal enum EdgePreset
{
    None,
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>Display unit for network and disk transfer rates.</summary>
internal enum RateUnit
{
    Bytes,
    Bits
}

internal enum OverlayDensity
{
    Default,
    Compact
}

internal sealed class MetricToggles
{
    public bool Cpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Vram { get; set; } = true;
    public bool NetSend { get; set; } = true;
    public bool NetRecv { get; set; } = true;
    public bool DiskRead { get; set; } = true;
    public bool DiskWrite { get; set; } = true;

    public MetricToggles Clone() => new()
    {
        Cpu = Cpu,
        Memory = Memory,
        Gpu = Gpu,
        Vram = Vram,
        NetSend = NetSend,
        NetRecv = NetRecv,
        DiskRead = DiskRead,
        DiskWrite = DiskWrite
    };

    public bool AnyEnabled() =>
        Cpu || Memory || Gpu || Vram || NetSend || NetRecv || DiskRead || DiskWrite;
}

internal sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = true;
    public MetricToggles Metrics { get; set; } = new();
    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Horizontal;
    public PlacementMode PlacementMode { get; set; } = PlacementMode.Taskbar;
    public EdgePreset EdgePreset { get; set; } = EdgePreset.None;
    public int FloatingX { get; set; } = int.MinValue;
    public int FloatingY { get; set; } = int.MinValue;
    public int TaskbarOffsetX { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool FollowSystemTheme { get; set; } = true;
    public RateUnit RateUnit { get; set; } = RateUnit.Bytes;

    public OverlayDensity Density { get; set; } = OverlayDensity.Default;

    /// <summary>Value font size (pt) when Density is Default.</summary>
    public float DefaultValueFontPt { get; set; } = 12f;
    /// <summary>Label font size (pt) when Density is Default.</summary>
    public float DefaultLabelFontPt { get; set; } = 8f;
    /// <summary>Value font size (pt) when Density is Compact.</summary>
    public float CompactValueFontPt { get; set; } = 10f;
    /// <summary>Label font size (pt) when Density is Compact.</summary>
    public float CompactLabelFontPt { get; set; } = 7.5f;

    /// <summary>Metrics sample interval in milliseconds (100–10000).</summary>
    public int UpdateIntervalMs { get; set; } = 1000;

    public bool HotkeyEnabled { get; set; } = true;
    /// <summary>Win32 MOD_* flags. Default Win+Ctrl.</summary>
    public int HotkeyModifiers { get; set; } = HotkeyDefaults.Modifiers;
    /// <summary>Virtual-key code. Default U.</summary>
    public int HotkeyVirtualKey { get; set; } = HotkeyDefaults.VirtualKey;

    public static AppSettings CreateDefaults() => new();

    public float ActiveValueFontPt =>
        Density == OverlayDensity.Compact ? CompactValueFontPt : DefaultValueFontPt;

    public float ActiveLabelFontPt =>
        Density == OverlayDensity.Compact ? CompactLabelFontPt : DefaultLabelFontPt;

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        Metrics = Metrics.Clone(),
        Orientation = Orientation,
        PlacementMode = PlacementMode,
        EdgePreset = EdgePreset,
        FloatingX = FloatingX,
        FloatingY = FloatingY,
        TaskbarOffsetX = TaskbarOffsetX,
        Opacity = Opacity,
        AlwaysOnTop = AlwaysOnTop,
        FollowSystemTheme = FollowSystemTheme,
        RateUnit = RateUnit,
        Density = Density,
        DefaultValueFontPt = DefaultValueFontPt,
        DefaultLabelFontPt = DefaultLabelFontPt,
        CompactValueFontPt = CompactValueFontPt,
        CompactLabelFontPt = CompactLabelFontPt,
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 100, 10_000),
        HotkeyEnabled = HotkeyEnabled,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey
    };
}

internal static class HotkeyDefaults
{
    // MOD_CONTROL = 0x0002, MOD_WIN = 0x0008
    public const int Modifiers = 0x0002 | 0x0008;
    public const int VirtualKey = 0x55; // 'U'
}
