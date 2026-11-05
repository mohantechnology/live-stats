using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskbarLiveStats.Metrics;

/// <summary>
/// Task Manager engine percent: sum each process on a node, clamp that node,
/// then take the busiest node. A named graph (3D, Copy, …) is the busiest
/// node of that type.
/// </summary>
internal static class EngineUtilization
{
    private static readonly Regex EngineInstanceRegex = new(
        @"luid_0x(?<luidHigh>[0-9A-Fa-f]+)_0x(?<luidLow>[0-9A-Fa-f]+)_phys_(?<phys>\d+)_eng_(?<eng>\d+)(?:_engtype_(?<type>.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static (double Percent, EngineUsage[] Engines) Aggregate(
        List<(string Name, double Value)> items,
        string? primaryLuidPrefix)
    {
        var nodes = new Dictionary<string, (string Luid, string Type, double Value)>(StringComparer.OrdinalIgnoreCase);
        double fallbackMax = 0;
        foreach (var (name, value) in items)
        {
            fallbackMax = Math.Max(fallbackMax, value);
            var match = EngineInstanceRegex.Match(name.Trim());
            if (!match.Success)
                continue;

            string luid = string.Create(CultureInfo.InvariantCulture,
                $"{match.Groups["luidHigh"].Value}_{match.Groups["luidLow"].Value}");
            string type = match.Groups["type"].Success ? match.Groups["type"].Value.Trim() : "";
            string key = string.Create(CultureInfo.InvariantCulture,
                $"{luid}_{match.Groups["phys"].Value}_{match.Groups["eng"].Value}_{type}");

            double sum = value;
            if (nodes.TryGetValue(key, out var existing))
                sum += existing.Value;
            nodes[key] = (luid, type, sum);
        }

        if (nodes.Count == 0)
            return (Math.Clamp(fallbackMax, 0, 100), []);

        bool usePrimary = false;
        if (!string.IsNullOrEmpty(primaryLuidPrefix))
        {
            foreach (var node in nodes.Values)
            {
                if (node.Luid.StartsWith(primaryLuidPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    usePrimary = true;
                    break;
                }
            }
        }

        double best = 0;
        var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Values)
        {
            if (usePrimary && !node.Luid.StartsWith(primaryLuidPrefix!, StringComparison.OrdinalIgnoreCase))
                continue;

            double pct = Math.Clamp(node.Value, 0, 100);
            best = Math.Max(best, pct);
            if (node.Type.Length == 0)
                continue;
            string label = ShortName(node.Type);
            byType[label] = Math.Max(byType.GetValueOrDefault(label), pct);
        }

        var engines = new EngineUsage[byType.Count];
        int i = 0;
        foreach (var (name, pct) in byType)
            engines[i++] = new EngineUsage(name, pct);

        return (Math.Clamp(best, 0, 100), engines);
    }

    private static string ShortName(string type)
    {
        if (type.Equals("3D", StringComparison.OrdinalIgnoreCase)) return "3D";
        if (type.Equals("Copy", StringComparison.OrdinalIgnoreCase)) return "Copy";
        if (type.Equals("VideoDecode", StringComparison.OrdinalIgnoreCase)) return "Decode";
        if (type.Equals("VideoEncode", StringComparison.OrdinalIgnoreCase)) return "Encode";
        if (type.Equals("VideoProcessing", StringComparison.OrdinalIgnoreCase)) return "Video";
        if (type.Equals("Compute", StringComparison.OrdinalIgnoreCase)) return "Compute";
        string trimmed = type.Trim();
        return trimmed.Length == 0 ? "Other" : trimmed;
    }
}
