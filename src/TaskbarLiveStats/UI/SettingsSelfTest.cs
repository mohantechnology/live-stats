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
        var blocks = GroupLayout.FromSnapshot(snap, settings).Main;
        var mem = blocks.FirstOrDefault(b => b.Kind == MetricKind.Memory);
        var vram = blocks.FirstOrDefault(b => b.Kind == MetricKind.Vram);
        if (!mem.Label.Contains("50%", StringComparison.Ordinal))
            failures.Add($"Memory label should include percent, got '{mem.Label}'.");
        if (!vram.Label.Contains("25%", StringComparison.Ordinal))
            failures.Add($"VRAM label should include percent, got '{vram.Label}'.");

        var palette = new MetricPalette();
        palette.DiskWrite = "#112233";
        var diskWrite = MetricColors.Resolve(palette, MetricKind.DiskWrite);
        if (diskWrite.R != 0x11 || diskWrite.G != 0x22 || diskWrite.B != 0x33)
            failures.Add($"Disk write color round-trip failed: {diskWrite}.");
        var upload = MetricColors.Resolve(new MetricPalette(), MetricKind.NetSend);
        if (upload.ToArgb() == diskWrite.ToArgb())
            failures.Add("Upload and disk write colors must differ.");

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.Show();
        Application.DoEvents();
        AssertChrome(form, failures, "normal");
        form.WindowState = FormWindowState.Maximized;
        Application.DoEvents();
        AssertChrome(form, failures, "maximized");
        form.WindowState = FormWindowState.Normal;
        form.Hide();

        AssertUsageAndSize(failures);

        string report = failures.Count == 0
            ? "SELF-TEST PASS\n- Settings chrome / TopMost OK\n- Orientation independent of Placement OK\n- Memory/VRAM percent labels OK\n"
            : "SELF-TEST FAIL\n" + string.Join("\n", failures.Select(f => "- " + f)) + "\n";

        string outPath = Path.Combine(Path.GetTempPath(), "taskbar-live-stats-selftest.txt");
        File.WriteAllText(outPath, report);
        Console.Write(report);
        Console.WriteLine("Wrote " + outPath);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void AssertUsageAndSize(List<string> failures)
    {
        var bands = new UsageColorBands();
        bands.Normalize();
        var idle = MetricPalette.Parse(MetricPalette.DefaultCpu, MetricPalette.DefaultCpu);
        var normal = MetricPalette.Parse(UsageColorBands.DefaultNormal, UsageColorBands.DefaultNormal);
        var high = MetricPalette.Parse(UsageColorBands.DefaultHigh, UsageColorBands.DefaultHigh);
        if (bands.ColorFor(9.9, idle).ToArgb() != idle.ToArgb())
            failures.Add("Usage under the idle mark should keep the metric color.");
        if (bands.ColorFor(10, idle).ToArgb() != normal.ToArgb())
            failures.Add("10% should use the normal color.");
        if (bands.ColorFor(70, idle).ToArgb() != normal.ToArgb())
            failures.Add("70% should still use the normal color.");
        if (bands.ColorFor(70.1, idle).ToArgb() != high.ToArgb())
            failures.Add("Above the high mark should use the high color.");

        var swapped = new UsageColorBands { IdleBelowPercent = 80, HighAbovePercent = 70 };
        swapped.Normalize();
        if (swapped.IdleBelowPercent >= swapped.HighAbovePercent)
            failures.Add("Idle mark should stay below the high mark.");

        double mb = 1024.0 * 1024.0;
        double bytes = RateCeilings.FromDisplay(100, RateUnit.Bytes);
        if (Math.Abs(RateCeilings.ToDisplay(bytes, RateUnit.Bytes) - 100) > 0.05)
            failures.Add("MB/s ceiling should round-trip.");
        if (Math.Abs(RateCeilings.ToDisplay(bytes, RateUnit.Bits) - 100) < 1)
            failures.Add("Switching transfer unit should convert the ceiling instead of keeping the digits.");

        var ratedSettings = AppSettings.CreateDefaults();
        ratedSettings.RateCeilings.NetSendBytesPerSec = 100 * mb;
        var rated = GroupLayout.FromSnapshot(new Metrics.MetricsSnapshot
        {
            CpuPercent = 4,
            NetSendBytesPerSec = 50 * mb,
            NetRecvBytesPerSec = 80 * mb
        }, ratedSettings);
        var cpuRated = rated.Main.First(b => b.Kind == MetricKind.Cpu);
        var upload = rated.Main.First(b => b.Kind == MetricKind.NetSend);
        var download = rated.Main.First(b => b.Kind == MetricKind.NetRecv);
        if (cpuRated.LoadPercent is not double cpuLoad || cpuLoad >= 10)
            failures.Add($"CPU under 10% should report a low load, got {cpuRated.LoadPercent}.");
        if (upload.LoadPercent is not double up || Math.Abs(up - 50) > 0.2)
            failures.Add($"Upload should be 50% of its ceiling, got {upload.LoadPercent}.");
        if (download.LoadPercent is not null)
            failures.Add("Download with no ceiling should keep its own color.");

        using var renderer = new StatsRenderer();
        renderer.ApplyTypography(ratedSettings);
        if (renderer.Accent(MetricKind.Cpu, 5).ToArgb() != renderer.Accent(MetricKind.Cpu, null).ToArgb())
            failures.Add("A percent under the idle mark should match the metric color.");
        if (renderer.Accent(MetricKind.Cpu, 40).ToArgb() == renderer.Accent(MetricKind.Cpu, null).ToArgb())
            failures.Add("A mid-range percent should leave the metric color.");
        if (renderer.Accent(MetricKind.Cpu, 90).ToArgb() == renderer.Accent(MetricKind.Cpu, 40).ToArgb())
            failures.Add("A high percent should use a different color from normal.");

        var cpuSettings = AppSettings.CreateDefaults();
        cpuSettings.ShowUsageDetails = true;
        cpuSettings.Density = OverlayDensity.Default;
        cpuSettings.DefaultValueFontPt = 8f;
        cpuSettings.DefaultLabelFontPt = 6f;
        cpuSettings.DefaultDetailFontPt = 16f;
        cpuSettings.Metrics = new MetricToggles { Cpu = true };
        var cores8 = Enumerable.Range(0, 8).Select(i => new Metrics.CoreUsage(i, 40)).ToArray();

        Size MeasureCores(int count, int available)
        {
            var sample = cpuSettings.Clone();
            sample.Orientation = OverlayOrientation.Horizontal;
            sample.UsageDetailCount = count;
            renderer.ApplyTypography(sample);
            var snap = new Metrics.MetricsSnapshot
            {
                CpuPercent = 40,
                BusiestCores = Enumerable.Range(0, available).Select(i => new Metrics.CoreUsage(i, 40)).ToArray()
            };
            return renderer.Measure(GroupLayout.FromSnapshot(snap, sample), 48);
        }

        var one = MeasureCores(1, 8);
        var two = MeasureCores(2, 8);
        var six = MeasureCores(6, 8);
        var cap50 = MeasureCores(50, 8);
        var cap8 = MeasureCores(8, 8);
        if (Math.Abs(one.Height - two.Height) > 2)
            failures.Add($"One and two busiest cells should share one row. {one.Height} vs {two.Height}.");

        using var detailFont = new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Point);
        float line = detailFont.GetHeight(96f);
        int rowDelta = six.Height - two.Height;
        if (rowDelta < line * 1.4f)
            failures.Add($"Six cells should add two rows, grew only {rowDelta}px.");
        if (rowDelta > line * 3.2f)
            failures.Add($"Six cells grew by {rowDelta}px, more than two rows.");
        if (cap50.Height != cap8.Height || cap50.Width != cap8.Width)
            failures.Add($"A count above the real core count changed the size. 50={cap50.Width}x{cap50.Height} 8={cap8.Width}x{cap8.Height}.");

        cpuSettings.UsageDetailCount = 50;
        var capped = GroupLayout.FromSnapshot(new Metrics.MetricsSnapshot
        {
            CpuPercent = 40,
            BusiestCores = cores8
        }, cpuSettings);
        var cappedCpu = capped.Main.First(b => b.Kind == MetricKind.Cpu);
        if (cappedCpu.Details == null || cappedCpu.Details.Slots != 8 || cappedCpu.Details.Cells.Length != 8)
            failures.Add($"Expected 8 busiest cells, got {cappedCpu.Details?.Cells.Length}.");

        var fullSettings = AppSettings.CreateDefaults();
        fullSettings.Orientation = OverlayOrientation.Vertical;
        fullSettings.ShowUsageDetails = true;
        fullSettings.UsageDetailCount = 40;
        fullSettings.DefaultDetailFontPt = 14f;
        var fullSnap = new Metrics.MetricsSnapshot
        {
            CpuPercent = 40,
            BusiestCores = Enumerable.Range(0, 40).Select(i => new Metrics.CoreUsage(i, 20)).ToArray(),
            MemoryUsedBytes = 8UL << 30,
            MemoryTotalBytes = 16UL << 30,
            MemoryPercent = 50,
            GpuPercent = 30,
            BusiestEngines = [new Metrics.EngineUsage("3D", 30), new Metrics.EngineUsage("Copy", 10)],
            VramUsedBytes = 2UL << 30,
            VramTotalBytes = 8UL << 30,
            VramPercent = 25,
            NpuPresent = true,
            NpuPercent = 12,
            NpuMemoryUsedBytes = 1UL << 30,
            NpuMemoryTotalBytes = 4UL << 30,
            NpuMemoryPercent = 25,
            NetSendBytesPerSec = 1000,
            NetRecvBytesPerSec = 2000,
            DiskReadBytesPerSec = 3000,
            DiskWriteBytesPerSec = 4000
        };
        renderer.ApplyTypography(fullSettings);
        var full = GroupLayout.FromSnapshot(fullSnap, fullSettings);
        var fullSize = renderer.Measure(full, 40);
        var cpuOnly = fullSettings.Clone();
        cpuOnly.Metrics = new MetricToggles { Cpu = true };
        renderer.ApplyTypography(cpuOnly);
        var cpuSize = renderer.Measure(GroupLayout.FromSnapshot(fullSnap, cpuOnly), 40);
        var memOnly = fullSettings.Clone();
        memOnly.Metrics = new MetricToggles { Memory = true };
        memOnly.ShowUsageDetails = false;
        renderer.ApplyTypography(memOnly);
        var memSize = renderer.Measure(GroupLayout.FromSnapshot(fullSnap, memOnly), 40);
        int others = Math.Max(1, full.Main.Length - 1);
        int limit = cpuSize.Height + others * (memSize.Height + 20) + 24;
        if (fullSize.Height > limit)
            failures.Add($"Vertical bar is {fullSize.Height}px, taller than the items need ({limit}).");
        if (fullSize.Height >= cpuSize.Height * others)
            failures.Add("Vertical bar still repeats the busiest list for every metric.");

        var horizontalSettings = fullSettings.Clone();
        horizontalSettings.Orientation = OverlayOrientation.Horizontal;
        renderer.ApplyTypography(horizontalSettings);
        var horizontal = renderer.Measure(GroupLayout.FromSnapshot(fullSnap, horizontalSettings), 48);
        var cpuHorizontal = cpuOnly.Clone();
        cpuHorizontal.Orientation = OverlayOrientation.Horizontal;
        renderer.ApplyTypography(cpuHorizontal);
        var cpuBar = renderer.Measure(GroupLayout.FromSnapshot(fullSnap, cpuHorizontal), 48);
        if (Math.Abs(horizontal.Height - cpuBar.Height) > 4)
            failures.Add($"Horizontal bar height {horizontal.Height} should match the tallest block {cpuBar.Height}.");
    }

    private static void AssertChrome(SettingsForm form, List<string> failures, string state)
    {
        Control? apply = null;
        Control? title = null;
        foreach (Control c in Enumerate(form))
        {
            if (apply == null && c is AntdUI.Button btn && string.Equals(btn.Text, "Apply", StringComparison.OrdinalIgnoreCase))
                apply = btn;
            if (title == null && c is Label label && string.Equals(label.Text, "General", StringComparison.Ordinal))
                title = label;
        }

        if (apply?.Parent == null || title?.Parent?.Parent == null)
        {
            failures.Add($"Could not resolve settings chrome ({state}).");
            return;
        }

        Control footer = apply.Parent;
        Control host = title.Parent.Parent;
        if (footer.Parent != form || host.Parent != form)
        {
            failures.Add($"Unexpected settings parent chain ({state}).");
            return;
        }

        Rectangle overlap = Rectangle.Intersect(footer.Bounds, host.Bounds);
        if (overlap.Width > 2 && overlap.Height > 2)
            failures.Add($"Content overlaps footer ({state}). content={host.Bounds} footer={footer.Bounds}.");

        Rectangle applyScreen = apply.RectangleToScreen(apply.ClientRectangle);
        Rectangle clientScreen = form.RectangleToScreen(form.ClientRectangle);
        if (!clientScreen.Contains(applyScreen))
            failures.Add($"Apply is outside the window ({state}). apply={applyScreen} client={clientScreen}.");
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
