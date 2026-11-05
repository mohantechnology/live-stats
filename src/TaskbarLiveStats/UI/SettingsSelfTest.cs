using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

/// <summary>Headless-ish smoke checks for settings chrome and orientation/placement independence.</summary>
internal static class SettingsSelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        var settings = AppSettings.CreateDefaults();
        settings.PlacementMode = PlacementMode.Floating;
        settings.Orientation = OverlayOrientation.Horizontal;
        settings.EdgePreset = EdgePreset.Right;
        settings.Density = OverlayDensity.Default;
        settings.UpdateIntervalMs = 1000;

        AppSettings? applied = null;
        using var form = new SettingsForm(settings, s => applied = s.Clone());

        if (form.TopMost)
            failures.Add("SettingsForm should not be TopMost so the overlay can stay above it.");
        if (!form.MinimizeBox)
            failures.Add("MinimizeBox should be enabled.");
        if (!form.MaximizeBox)
            failures.Add("MaximizeBox should be enabled.");
        if (!form.Resizable)
            failures.Add("Resizable should be enabled for window sizing.");

        var orient = FindSegmented(form, "Horizontal");
        var place = FindSegmented(form, "Taskbar");
        var density = FindSegmented(form, "Default");
        if (orient is null || place is null)
        {
            failures.Add("Could not find Orientation/Placement segmented controls.");
        }
        else
        {
            orient.SelectIndex = 1;
            place.SelectIndex = 1;
            if (orient.SelectIndex != 1)
                failures.Add("Orientation SelectIndex failed to stay Vertical.");
            if (place.SelectIndex != 1)
                failures.Add("Placement incorrectly changed when Orientation was set.");

            orient.SelectIndex = 0;
            if (place.SelectIndex != 1)
                failures.Add("Placement changed when toggling Orientation back to Horizontal.");

            orient.SelectIndex = 1;
            place.SelectIndex = 1;
            if (density != null)
                density.SelectIndex = 1; // Compact

            ClickNamedButton(form, "Apply");
            Application.DoEvents();

            if (applied is null)
            {
                if (orient.SelectIndex != 1 || place.SelectIndex != 1)
                    failures.Add("After Apply, Orientation/Placement segmenteds are no longer independent.");
            }
            else
            {
                if (applied.Orientation != OverlayOrientation.Vertical)
                    failures.Add($"Expected Orientation=Vertical, got {applied.Orientation}.");
                if (applied.PlacementMode != PlacementMode.Floating)
                    failures.Add($"Expected Placement=Floating after Vertical orient, got {applied.PlacementMode}.");
                if (density != null && applied.Density != OverlayDensity.Compact)
                    failures.Add($"Expected Density=Compact, got {applied.Density}.");
            }
        }

        // Memory/VRAM labels include percent
        var snap = new Metrics.MetricsSnapshot
        {
            MemoryUsedBytes = 8UL * 1024 * 1024 * 1024,
            MemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
            MemoryPercent = 50,
            VramUsedBytes = 2UL * 1024 * 1024 * 1024,
            VramTotalBytes = 8UL * 1024 * 1024 * 1024,
            VramPercent = 25
        };
        var blocks = GroupLayout.FromSnapshot(snap, settings);
        var mem = blocks.FirstOrDefault(b => b.Kind == MetricKind.Memory);
        var vram = blocks.FirstOrDefault(b => b.Kind == MetricKind.Vram);
        if (!mem.Label.Contains("50%", StringComparison.Ordinal))
            failures.Add($"Memory label should include percent, got '{mem.Label}'.");
        if (!vram.Label.Contains("25%", StringComparison.Ordinal))
            failures.Add($"VRAM label should include percent, got '{vram.Label}'.");

        string report = failures.Count == 0
            ? "SELF-TEST PASS\n- Settings chrome / TopMost OK\n- Orientation independent of Placement OK\n- Memory/VRAM percent labels OK\n"
            : "SELF-TEST FAIL\n" + string.Join("\n", failures.Select(f => "- " + f)) + "\n";

        string outPath = Path.Combine(Path.GetTempPath(), "taskbar-live-stats-selftest.txt");
        File.WriteAllText(outPath, report);
        Console.Write(report);
        Console.WriteLine("Wrote " + outPath);
        return failures.Count == 0 ? 0 : 1;
    }

    private static AntdUI.Segmented? FindSegmented(Control root, string itemText)
    {
        foreach (Control c in Enumerate(root))
        {
            if (c is AntdUI.Segmented seg)
            {
                foreach (AntdUI.SegmentedItem item in seg.Items)
                {
                    if (string.Equals(item.Text, itemText, StringComparison.OrdinalIgnoreCase))
                        return seg;
                }
            }
        }
        return null;
    }

    private static void ClickNamedButton(Control root, string text)
    {
        foreach (Control c in Enumerate(root))
        {
            if (c is AntdUI.Button btn && string.Equals(btn.Text, text, StringComparison.OrdinalIgnoreCase))
            {
                btn.PerformClick();
                return;
            }
        }
    }

    private static IEnumerable<Control> Enumerate(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var x in Enumerate(child))
                yield return x;
        }
    }
}
