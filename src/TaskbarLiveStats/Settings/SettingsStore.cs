using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLiveStats.Settings;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarLiveStats");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    public bool FileExists => File.Exists(_path);

    /// <summary>
    /// Loads settings. When the file is missing, returns defaults and sets
    /// <paramref name="isFirstRun"/> so the caller can enable autostart.
    /// </summary>
    public AppSettings Load(out bool isFirstRun)
    {
        isFirstRun = !File.Exists(_path);
        if (isFirstRun)
            return AppSettings.CreateDefaults();

        try
        {
            string json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
                return AppSettings.CreateDefaults();

            loaded.Metrics ??= new MetricToggles();
            loaded.HideWhenIdle ??= new IdleHideToggles();
            loaded.TotalUsage ??= new TotalUsageToggles();
            ApplyHistoryMigration(json, loaded);
            if (loaded.HideIdleMetrics)
            {
                loaded.HideWhenIdle = IdleHideToggles.AllOn();
                loaded.HideIdleMetrics = false;
            }
            loaded.Colors ??= new MetricPalette();
            loaded.Colors.Normalize();
            loaded.UsageColors ??= new UsageColorBands();
            loaded.UsageColors.Normalize();
            loaded.RateCeilings ??= new RateCeilings();
            loaded.RateCeilings.Normalize();
            loaded.Opacity = Math.Clamp(loaded.Opacity, 0.5, 1.0);
            loaded.UpdateIntervalMs = Math.Clamp(loaded.UpdateIntervalMs <= 0 ? 1000 : loaded.UpdateIntervalMs, 100, 10_000);
            loaded.UsageDetailCount = Math.Clamp(loaded.UsageDetailCount <= 0 ? 3 : loaded.UsageDetailCount, 1, AppSettings.MaxUsageDetailCount);
            loaded.DefaultValueFontPt = Math.Clamp(loaded.DefaultValueFontPt <= 0 ? 12f : loaded.DefaultValueFontPt, 7f, 28f);
            loaded.DefaultLabelFontPt = Math.Clamp(loaded.DefaultLabelFontPt <= 0 ? 8f : loaded.DefaultLabelFontPt, 6f, 18f);
            loaded.CompactValueFontPt = Math.Clamp(loaded.CompactValueFontPt <= 0 ? 10f : loaded.CompactValueFontPt, 7f, 28f);
            loaded.CompactLabelFontPt = Math.Clamp(loaded.CompactLabelFontPt <= 0 ? 7.5f : loaded.CompactLabelFontPt, 6f, 18f);
            loaded.DefaultDetailFontPt = Math.Clamp(loaded.DefaultDetailFontPt <= 0 ? 7.5f : loaded.DefaultDetailFontPt, 6f, 18f);
            loaded.CompactDetailFontPt = Math.Clamp(loaded.CompactDetailFontPt <= 0 ? 7f : loaded.CompactDetailFontPt, 6f, 18f);
            if (loaded.HotkeyVirtualKey == 0)
                loaded.HotkeyVirtualKey = HotkeyDefaults.VirtualKey;
            if (loaded.HotkeyModifiers == 0)
                loaded.HotkeyModifiers = HotkeyDefaults.Modifiers;
            return loaded;
        }
        catch
        {
            return AppSettings.CreateDefaults();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Opacity = Math.Clamp(settings.Opacity, 0.5, 1.0);
        settings.UpdateIntervalMs = Math.Clamp(settings.UpdateIntervalMs, 100, 10_000);
        settings.UsageDetailCount = Math.Clamp(settings.UsageDetailCount <= 0 ? 3 : settings.UsageDetailCount, 1, AppSettings.MaxUsageDetailCount);
        settings.DefaultValueFontPt = Math.Clamp(settings.DefaultValueFontPt, 7f, 28f);
        settings.DefaultLabelFontPt = Math.Clamp(settings.DefaultLabelFontPt, 6f, 18f);
        settings.CompactValueFontPt = Math.Clamp(settings.CompactValueFontPt, 7f, 28f);
        settings.CompactLabelFontPt = Math.Clamp(settings.CompactLabelFontPt, 6f, 18f);
        settings.DefaultDetailFontPt = Math.Clamp(settings.DefaultDetailFontPt, 6f, 18f);
        settings.CompactDetailFontPt = Math.Clamp(settings.CompactDetailFontPt, 6f, 18f);
        settings.Colors ??= new MetricPalette();
        settings.Colors.Normalize();
        settings.UsageColors ??= new UsageColorBands();
        settings.UsageColors.Normalize();
        settings.RateCeilings ??= new RateCeilings();
        settings.RateCeilings.Normalize();
        settings.SaveUsageHistory = settings.SaveNetworkHistory || settings.SaveDiskHistory;
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, json);
    }

    /// <summary>
    /// Old settings files have only saveUsageHistory. Copy that onto both blocks
    /// when the new keys were never written.
    /// </summary>
    private static void ApplyHistoryMigration(string json, AppSettings loaded)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            bool hasNetwork = doc.RootElement.TryGetProperty("saveNetworkHistory", out _);
            bool hasDisk = doc.RootElement.TryGetProperty("saveDiskHistory", out _);
            if (!hasNetwork && !hasDisk)
            {
                loaded.SaveNetworkHistory = loaded.SaveUsageHistory;
                loaded.SaveDiskHistory = loaded.SaveUsageHistory;
            }
        }
        catch
        {
            // Keep the defaults already deserialized.
        }
    }
}
