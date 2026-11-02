namespace TaskbarLiveStats.UI;

// Kept as a thin alias so older call sites stay compile-clean if reintroduced.
internal static class UnitFormatter
{
    public static string FormatSnapshot(Metrics.MetricsSnapshot s) => GroupLayout.Tooltip(s);
}
