using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarLiveStats.Metrics;
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
        AssertLayout(form, failures, "normal");
        AssertChrome(form, failures, "normal");
        AssertScrollPreservedOnSwitch(form, failures, "normal");
        form.WindowState = FormWindowState.Maximized;
        Application.DoEvents();
        AssertLayout(form, failures, "maximized");
        AssertChrome(form, failures, "maximized");
        AssertScrollPreservedOnSwitch(form, failures, "maximized");
        form.WindowState = FormWindowState.Normal;
        form.Hide();

        AssertUsageAndSize(failures);
        AssertTotals(failures);
        AssertSession(failures);
        AssertHistoryStore(failures);

        string report = failures.Count == 0
            ? "SELF-TEST PASS\n- Settings chrome / TopMost OK\n- Orientation independent of Placement OK\n- Memory/VRAM percent labels OK\n"
            : "SELF-TEST FAIL\n" + string.Join("\n", failures.Select(f => "- " + f)) + "\n";

        string outPath = Path.Combine(Path.GetTempPath(), "taskbar-live-stats-selftest.txt");
        File.WriteAllText(outPath, report);
        Console.Write(report);
        Console.WriteLine("Wrote " + outPath);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void AssertTotals(List<string> failures)
    {
        var defaults = AppSettings.CreateDefaults();
        if (!defaults.SaveUsageHistory || !defaults.SaveNetworkHistory || !defaults.SaveDiskHistory || !defaults.SaveComputerUseHistory)
            failures.Add("Network, disk, and computer-use history should be on by default.");
        if (defaults.TotalSince != TotalSince.Restart)
            failures.Add("Total since should default to the since-reboot counter.");
        if (!defaults.TotalUsage.NetSend || !defaults.TotalUsage.NetRecv || !defaults.TotalUsage.DiskRead || !defaults.TotalUsage.DiskWrite)
            failures.Add("Total usage switches should be on by default.");

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}", options);
        if (legacy == null || !legacy.SaveUsageHistory || !legacy.SaveNetworkHistory || !legacy.SaveDiskHistory || !legacy.SaveComputerUseHistory || legacy.TotalUsage?.DiskRead != true)
            failures.Add("Old settings files should keep total usage and history saving on.");

        string settingsPath = Path.Combine(Path.GetTempPath(), "tls-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(settingsPath, """{"saveUsageHistory":false}""");
            var migrated = new SettingsStore(settingsPath).Load(out bool firstRun);
            if (firstRun || migrated.SaveNetworkHistory || migrated.SaveDiskHistory)
                failures.Add("Turning history off in an old settings file should turn both blocks off.");
            // Computer-use is a new key; absent means default on unless explicitly set.
            if (!migrated.SaveComputerUseHistory)
                failures.Add("Missing computer-use history key should stay on by default.");
        }
        catch (Exception ex)
        {
            failures.Add("History migration failed: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(settingsPath)) File.Delete(settingsPath); } catch { /* temp cleanup */ }
        }

        ulong gib = 1024UL * 1024UL * 1024UL;
        var snap = new MetricsSnapshot
        {
            NetSendBytesPerSec = 1500,
            NetRecvBytesPerSec = 1500,
            DiskReadBytesPerSec = 1500,
            DiskWriteBytesPerSec = 1500,
            NetSendTotalBytes = gib,
            NetRecvTotalBytes = 2 * gib,
            DiskReadTotalBytes = 3 * gib,
            DiskWriteTotalBytes = 4 * gib,
            NetSendStartupBytes = 2 * gib,
            NetRecvStartupBytes = 3 * gib,
            DiskReadStartupBytes = 4 * gib,
            DiskWriteStartupBytes = 5 * gib
        };
        var frame = GroupLayout.FromSnapshot(snap, defaults);
        ExpectLabel(frame, MetricKind.NetSend, "Upload  1.0 GB", failures);
        ExpectLabel(frame, MetricKind.NetRecv, "Download  2.0 GB", failures);
        ExpectLabel(frame, MetricKind.DiskRead, "Disk read  3.0 GB", failures);
        ExpectLabel(frame, MetricKind.DiskWrite, "Disk write  4.0 GB", failures);
        var upload = frame.Main.FirstOrDefault(b => b.Kind == MetricKind.NetSend);
        if (upload.LabelReserve == null || !upload.LabelReserve.Contains("888.8 GB", StringComparison.Ordinal))
            failures.Add($"Upload reserve should stay on the GB unit, got '{upload.LabelReserve}'.");
        if (!GroupLayout.Tooltip(snap, defaults).Contains("1.0 GB", StringComparison.Ordinal))
            failures.Add("Tooltip should include the upload total.");

        var startupBasis = defaults.Clone();
        startupBasis.TotalSince = TotalSince.Startup;
        ExpectLabel(GroupLayout.FromSnapshot(snap, startupBasis), MetricKind.NetSend, "Upload  2.0 GB", failures);
        ExpectLabel(GroupLayout.FromSnapshot(snap, startupBasis), MetricKind.DiskWrite, "Disk write  5.0 GB", failures);
        if (!GroupLayout.Tooltip(snap, startupBasis).Contains("2.0 GB", StringComparison.Ordinal))
            failures.Add("Startup tooltip should use the session total.");

        var hidden = defaults.Clone();
        hidden.TotalUsage.NetSend = false;
        hidden.TotalUsage.NetRecv = false;
        hidden.TotalUsage.DiskRead = false;
        hidden.TotalUsage.DiskWrite = false;
        ExpectLabel(GroupLayout.FromSnapshot(snap, hidden), MetricKind.NetSend, "Upload", failures);
        if (!defaults.TotalUsage.NetSend)
            failures.Add("Hiding totals on a clone changed the original.");

        using var disk = new DiskCollector();
        var reading = disk.Sample();
        const ulong absurd = 100UL << 40;
        if (reading.TotalsValid && (reading.ReadBytes > absurd || reading.WriteBytes > absurd))
            failures.Add($"Disk since-boot totals look misread: read={reading.ReadBytes} write={reading.WriteBytes}.");
        if (!reading.TotalsValid)
            failures.Add("Disk since-boot totals should be readable from PDH raw counters.");

        using var network = new NetworkCollector();
        var net = network.Sample();
        if (!net.TotalsValid)
            failures.Add("Network since-boot totals should be readable from PDH raw counters.");
        System.Threading.Thread.Sleep(200);
        net = network.Sample();
        if (!net.TotalsValid)
            failures.Add("Network totals should stay valid after the rate counter primes.");
    }

    private static void ExpectLabel(OverlayFrame frame, MetricKind kind, string label, List<string> failures)
    {
        var block = frame.Main.FirstOrDefault(b => b.Kind == kind);
        if (block.Label != label)
            failures.Add($"Expected {kind} label '{label}', got '{block.Label}'.");
    }

    private static void AssertHistoryStore(List<string> failures)
    {
        string path = Path.Combine(Path.GetTempPath(), "tls-usage-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var now = DateTime.UtcNow;
            using (var store = new UsageHistoryStore(path))
            {
                store.SetRecording(true, true, true);
                store.Add(now, 1000, 2000, 3000, 4000);
                store.SetRecording(false, false, false);
                store.Add(now, 9, 9, 9, 9);
                store.SetRecording(true, false, true);
                store.Add(now, 5, 6, 7, 8);
            }

            if (!UsageHistoryStore.HasAny(path))
            {
                failures.Add("History store did not keep a minute.");
                return;
            }

            var window = UsageHistoryStore.Resolve(UsageRange.Day, DateTime.Now, null, null);
            ulong send = 0, recv = 0, read = 0, write = 0;
            foreach (var bucket in UsageHistoryStore.Read(path, window))
            {
                send += bucket.NetSend;
                recv += bucket.NetRecv;
                read += bucket.DiskRead;
                write += bucket.DiskWrite;
            }

            if (send != 1005 || recv != 2006 || read != 3000 || write != 4000)
                failures.Add($"History buckets were {send}/{recv}/{read}/{write}, expected 1005/2006/3000/4000.");

            var at = new DateTime(2026, 6, 15, 12, 30, 45);
            ExpectWindow(failures, UsageRange.Minutes15, at, 60, 15, 17, "15 min");
            ExpectWindow(failures, UsageRange.Hour, at, 600, 60, 70, "1 hour");
            ExpectWindow(failures, UsageRange.Hours5, at, 1800, 300, 330, "5 hours");
            var day = UsageHistoryStore.Resolve(UsageRange.Day, at, null, null);
            if (day.BucketSeconds != 7200 || day.LocalStart.Date != at.Date)
                failures.Add("Day should stay on today with 2-hour buckets.");
            if ((day.LocalEnd - day.LocalStart).TotalHours < 23.9)
                failures.Add("Day should cover the full calendar day.");
            var dayBuckets = UsageHistoryStore.Read(path, day);
            if (dayBuckets.Length != 12)
                failures.Add($"Day should fill 12 two-hour slots, got {dayBuckets.Length}.");
            if (UsageHistoryStore.Resolve(UsageRange.Week, at, null, null).BucketSeconds != 86_400)
                failures.Add("Week should stay on daily buckets.");
            if (UsageHistoryStore.Resolve(UsageRange.Month, at, null, null).BucketSeconds != 86_400)
                failures.Add("Month should stay on daily buckets.");
            var months3 = UsageHistoryStore.Resolve(UsageRange.Months3, at, null, null);
            if (months3.BucketSeconds != 86_400 || (at.Date - months3.LocalStart).TotalDays < 89.9)
                failures.Add("3 months should use daily buckets over about 90 days.");
            var months6 = UsageHistoryStore.Resolve(UsageRange.Months6, at, null, null);
            if (months6.BucketSeconds != 86_400 || (at.Date - months6.LocalStart).TotalDays < 179.9)
                failures.Add("6 months should use daily buckets over about 180 days.");
            if (UsageHistoryStore.Resolve(UsageRange.Year, at, null, null).BucketSeconds != 86_400)
                failures.Add("Year should stay on daily buckets.");
            var customDay = UsageHistoryStore.Resolve(UsageRange.Custom, at, at.Date, at.Date);
            if (customDay.BucketSeconds != 7200)
                failures.Add("Custom ranges of one day or less should use 2-hour buckets.");
            if ((customDay.LocalEnd - customDay.LocalStart).TotalHours < 23.9)
                failures.Add("Custom single-day should cover the full selected day.");
            var customMulti = UsageHistoryStore.Resolve(UsageRange.Custom, at, at.Date.AddDays(-7), at.Date);
            if (customMulti.BucketSeconds != 86_400)
                failures.Add("Custom ranges longer than one day should use daily buckets.");
        }
        catch (Exception ex)
        {
            failures.Add("History store failed: " + ex.Message);
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(path + suffix)) File.Delete(path + suffix); } catch { /* temp cleanup */ }
            }
        }
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
        using var probe = new Bitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        float line = detailFont.GetHeight(pg);
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
        int slack = Math.Max(36, memSize.Height / 3);
        int limit = cpuSize.Height + others * (memSize.Height + slack) + 40;
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

    private static void AssertLayout(SettingsForm form, List<string> failures, string state)
    {
        var rate = FindSegmented(form, "Bytes/s");
        if (rate == null)
            failures.Add($"Could not find the transfer unit control ({state}).");
        else if (!PageHasTitle(rate, "General"))
            failures.Add($"Transfer units should sit on General ({state}).");

        var transfer = FindLabel(form, "Transfer units");
        if (transfer == null || !PageHasTitle(transfer, "General"))
            failures.Add($"Transfer units heading should sit on General ({state}).");

        form.ShowSection("monitoring");
        Application.DoEvents();

        var ceilings = FindLabel(form, "Speed ceilings");
        if (ceilings == null)
            failures.Add($"Speed ceilings heading is missing ({state}).");
        else
        {
            if (!PageHasTitle(ceilings, "Monitoring"))
                failures.Add($"Speed ceilings should sit on Monitoring ({state}).");
            if (CardHasLabel(ceilings, "Transfer units"))
                failures.Add($"Speed ceilings card should not include Transfer units ({state}).");
        }

        var cpu = FindLabel(form, "CPU");
        if (cpu?.Parent == null)
        {
            failures.Add($"Could not find the CPU metric row ({state}).");
        }
        else
        {
            var switches = new List<AntdUI.Switch>();
            foreach (Control child in cpu.Parent.Controls)
            {
                if (child is AntdUI.Switch toggle)
                    switches.Add(toggle);
            }

            if (switches.Count < 2)
                failures.Add($"CPU row should have Hide and Show switches ({state}).");
            else if (switches[0].Size != switches[1].Size)
                failures.Add($"Hide and Show switches differ ({state}): {switches[0].Size} vs {switches[1].Size}.");

            var startup = FindLabel(form, "Start with Windows");
            AntdUI.Switch? shared = null;
            if (startup?.Parent != null)
            {
                foreach (Control child in startup.Parent.Controls)
                {
                    if (child is AntdUI.Switch toggle)
                        shared = toggle;
                }
            }

            if (shared == null)
                failures.Add($"Could not find the shared settings switch ({state}).");
            else if (switches.Count >= 2 && (switches[0].Size != shared.Size || switches[1].Size != shared.Size))
                failures.Add($"Metric switches should match the other settings switches ({state}): {switches[0].Size} vs {shared.Size}.");
        }

        AssertCaptionInside(FindLabel(form, "Total usage"), failures, state);
        AssertCaptionInside(FindLabel(form, "Hide when idle"), failures, state);
        AssertCaptionInside(FindLabel(form, "Show"), failures, state);

        form.ShowSection("usage");
        foreach (string heading in new[] { "Network usage", "Disk usage", "Computer usage" })
        {
            var block = FindLabel(form, heading);
            if (block == null || !PageHasTitle(block, "Usage"))
                failures.Add($"{heading} should sit on Usage ({state}).");
        }

        foreach (string item in new[] { "15 min", "1 hour", "5 hours", "Day", "Week", "Month", "3 months", "6 months", "Year", "Custom" })
        {
            int historyRanges = CountSegmented(form, item);
            if (historyRanges < 3)
                failures.Add($"Usage history range '{item}' should appear for network, disk, and computer use ({state}), found {historyRanges}.");
        }

        if (FindLabel(form, "Start date") == null || FindLabel(form, "End date") == null)
            failures.Add($"Custom history should use Start date and End date labels ({state}).");

        form.ShowSection("monitoring");
        if (FindSegmented(form, "Restart") == null || FindSegmented(form, "Startup") == null)
            failures.Add($"Total since control is missing ({state}).");

        form.ShowSection("usage");
        int historyPlots = 0;
        foreach (Control control in Enumerate(form))
        {
            if (control.GetType().Name == "FormsPlot")
                historyPlots++;
        }

        if (historyPlots < 3)
            failures.Add($"Expected network, disk, and computer-use charts ({state}), found {historyPlots}.");
    }

    private static void ExpectWindow(List<string> failures, UsageRange range, DateTime at, int bucket, double minMinutes, double maxMinutes, string name)
    {
        var window = UsageHistoryStore.Resolve(range, at, null, null);
        if (window.BucketSeconds != bucket)
            failures.Add($"{name} bucket was {window.BucketSeconds}, expected {bucket}.");
        double span = (window.LocalEnd - window.LocalStart).TotalMinutes;
        if (span < minMinutes || span > maxMinutes)
            failures.Add($"{name} span was {span:0.##} minutes.");
    }

    private static void AssertSession(List<string> failures)
    {
        var origin = new ByteTotals(1000, 0, 0, 0);
        var last = new ByteTotals(1500, 0, 0, 0);
        var saved = new SessionLedger(10, 20, ByteTotals.Zero, origin, last);
        if (saved.Display(new ByteTotals(1800, 0, 0, 0)).Send != 800)
            failures.Add("Startup total should be the bytes since the session origin.");

        var fast = SessionMath.Cross(saved, SessionEdge.Shutdown, false, 10, 30, new ByteTotals(2000, 0, 0, 0));
        if (fast.Display(new ByteTotals(2000, 0, 0, 0)).Send != 500)
            failures.Add("Fast startup should start the session from the counter saved at shutdown.");

        var fresh = new ByteTotals(40, 0, 0, 0);
        if (SessionMath.Cross(saved, SessionEdge.Restart, true, 99, 40, fresh).Display(fresh).Send != 40)
            failures.Add("Restart should start a new session.");
        if (SessionMath.Cross(saved, SessionEdge.Shutdown, true, 99, 40, fresh).Display(fresh).Send != 40)
            failures.Add("Shutdown should start a new session.");

        var kept = new ByteTotals(1800, 0, 0, 0);
        if (SessionMath.Cross(saved, SessionEdge.Hibernate, false, 99, 20, kept).Display(kept).Send != 800)
            failures.Add("Hibernate should keep the startup total.");
        if (SessionMath.Cross(saved, SessionEdge.Hibernate, true, 99, 20, fresh).Display(fresh).Send != 540)
            failures.Add("Hibernate should keep the bytes from before a counter reset.");
        if (SessionMath.Cross(saved, SessionEdge.Sleep, false, 10, 20, kept).Display(kept).Send != 800)
            failures.Add("Sleep should keep the startup total.");
        if (SessionMath.Classify(10, 20, 10, 20, SessionEdge.Same) != SessionEdge.Same)
            failures.Add("Logout and lock should keep the startup session.");
        if (SessionMath.Classify(10, 20, 10, 30, SessionEdge.Same) != SessionEdge.Shutdown)
            failures.Add("Fast startup should count as a shutdown.");
        if (SessionMath.Classify(10, 20, 99, 40, SessionEdge.Same) != SessionEdge.Restart)
            failures.Add("An unclassified new boot should start a new session.");
        if (SessionMath.Fold([SessionEdge.Sleep]) != SessionEdge.Same)
            failures.Add("A sleep event should not clear the session.");
        if (SessionMath.Fold([SessionEdge.Hibernate]) != SessionEdge.Hibernate)
            failures.Add("Hibernate should be recognized.");
        if (SessionMath.Fold([SessionEdge.Restart]) != SessionEdge.Restart)
            failures.Add("Restart should be recognized.");
        if (SessionMath.Fold([SessionEdge.Shutdown]) != SessionEdge.Shutdown)
            failures.Add("Shutdown should be recognized.");
        if (SessionMath.Fold([SessionEdge.Restart, SessionEdge.Hibernate]) != SessionEdge.Restart)
            failures.Add("Hibernate after a restart should not restore the previous session.");

        ulong big = 10_000_000;
        if (!SessionMath.CountersReset(new ByteTotals(big, big, big, big), new ByteTotals(10, 10, 10, 10)))
            failures.Add("A fresh boot should count as a counter reset.");
        if (SessionMath.CountersReset(new ByteTotals(big, big, big, big), new ByteTotals(big + 100, big, big, big)))
            failures.Add("Growing counters are not a reset.");

        // Inflated network origin with a corrected lower counter must not stay at 0.
        var inflated = new SessionLedger(
            10, 20, ByteTotals.Zero,
            new ByteTotals(5_000_000, 40_000_000_000, 1, 1),
            new ByteTotals(1_000_000, 30_000_000_000, 2, 2));
        var corrected = new ByteTotals(1_200_000, 30_100_000_000, 3, 3);
        var rebased = SessionMath.RebaseRegressed(inflated, corrected);
        ByteTotals after = rebased.Display(corrected);
        if (after.Send != corrected.Send || after.Recv != corrected.Recv)
            failures.Add("Incompatible network origin should fall back to the live since-boot counter.");
        ByteTotals grown = rebased.Display(new ByteTotals(1_200_500, 30_100_000_400, 4, 4));
        if (grown.Send != 1_200_500 || grown.Recv != 30_100_000_400)
            failures.Add("After rebase, Startup totals should follow the corrected counter.");

        // Adapter reset mid-session should keep bytes already counted in this session.
        var active = new SessionLedger(
            10, 20, ByteTotals.Zero,
            new ByteTotals(1_000_000, 0, 0, 0),
            new ByteTotals(1_500_000, 0, 0, 0));
        var dipped = new ByteTotals(20_000, 0, 0, 0);
        var afterReset = SessionMath.RebaseRegressed(active, dipped);
        if (afterReset.Display(dipped).Send != 500_000)
            failures.Add("An adapter reset should keep the session bytes already counted.");
        if (SessionMath.CountersReset(inflated.Last, corrected))
            failures.Add("A network-only drop with growing disk must not look like a full counter reset.");
    }

    private static void AssertCaptionInside(Label? caption, List<string> failures, string state)
    {
        if (caption?.Parent == null)
        {
            failures.Add($"Missing header caption ({state}).");
            return;
        }

        Rectangle parent = caption.Parent.ClientRectangle;
        Rectangle bounds = caption.Bounds;
        if (bounds.Width < 8 || bounds.Left < 0 || bounds.Top < -1 || bounds.Right > parent.Right + 1 || bounds.Bottom > parent.Bottom + 1)
            failures.Add($"{caption.Text} is outside its header ({state}). caption={bounds} header={parent}.");

        int textW = TextRenderer.MeasureText(
            caption.Text,
            caption.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        if (caption.Width + 1 < textW)
            failures.Add($"{caption.Text} is clipped ({state}): column {caption.Width}px, text {textW}px.");
    }

    private static bool PageHasTitle(Control control, string title)
    {
        Control? page = control;
        while (page != null && page.Parent != null && page.Parent.Parent is not SettingsForm)
            page = page.Parent;
        if (page == null)
            return false;

        foreach (Control child in page.Controls)
        {
            if (child is Label label && string.Equals(label.Text, title, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool CardHasLabel(Control heading, string text)
    {
        if (heading.Parent == null)
            return false;

        foreach (Control child in heading.Parent.Controls)
        {
            if (child is Label label && string.Equals(label.Text, text, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Label? FindLabel(Control root, string text)
    {
        foreach (Control c in Enumerate(root))
        {
            if (c is Label label && string.Equals(label.Text, text, StringComparison.Ordinal))
                return label;
        }

        return null;
    }

    private static void AssertScrollPreservedOnSwitch(SettingsForm form, List<string> failures, string state)
    {
        form.ShowSection("monitoring");
        Application.DoEvents();

        ScrollableControl? scroll = FindScrollHost(form);
        AntdUI.Switch? toggle = FindLabeledSwitch(form, "Hide when idle")
            ?? FindLabeledSwitch(form, "Enabled")
            ?? FindFirstSwitch(form);
        if (scroll == null || toggle == null)
        {
            failures.Add($"Could not find scroll host or switch to verify scroll ({state}).");
            return;
        }

        // Monitoring must be tall enough to scroll; force a known offset if possible.
        scroll.AutoScrollMinSize = new Size(0, Math.Max(scroll.AutoScrollMinSize.Height, scroll.ClientSize.Height + 1200));
        Application.DoEvents();
        Application.DoEvents();

        int target = Math.Min(360, Math.Max(120, Math.Max(1, scroll.VerticalScroll.Maximum) / 2));
        scroll.AutoScrollPosition = new Point(0, target);
        Application.DoEvents();
        if (scroll is SettingsForm.SettingsScrollPanel panel)
            panel.RememberScroll();
        int before = Math.Abs(scroll.AutoScrollPosition.Y);
        if (before < 40)
            before = scroll.VerticalScroll.Value;
        if (before < 40)
        {
            failures.Add($"Could not scroll the settings page before toggling a switch ({state}): max={scroll.VerticalScroll.Maximum}, value={scroll.VerticalScroll.Value}, pos={scroll.AutoScrollPosition}.");
            return;
        }

        if (toggle.CanFocus)
        {
            toggle.Focus();
            PumpScrollRestore();
            int afterFocus = scroll.VerticalScroll.Value;
            if (Math.Abs(afterFocus - before) > 40)
                failures.Add($"Focusing a switch scrolled the page ({state}): before={before}, after={afterFocus}.");
            before = scroll.VerticalScroll.Value;
        }

        bool previous = toggle.Checked;
        toggle.Checked = !previous;
        PumpScrollRestore();
        int afterToggle = scroll.VerticalScroll.Value;
        toggle.Checked = previous;
        PumpScrollRestore();
        int afterRestore = scroll.VerticalScroll.Value;

        if (Math.Abs(afterToggle - before) > 40)
            failures.Add($"Toggling a switch scrolled the page ({state}): before={before}, after={afterToggle}.");
        if (Math.Abs(afterRestore - before) > 40)
            failures.Add($"Restoring a switch scrolled the page ({state}): before={before}, after={afterRestore}.");

        // History filter segment should also keep scroll.
        var range = FindSegmented(form, "Week");
        if (range != null)
        {
            SetScrollForTest(scroll, before);
            before = scroll.VerticalScroll.Value;
            int prior = range.SelectIndex;
            range.SelectIndex = prior == 4 ? 5 : 4;
            PumpScrollRestore();
            int afterRange = scroll.VerticalScroll.Value;
            range.SelectIndex = prior;
            PumpScrollRestore();
            if (Math.Abs(afterRange - before) > 40)
                failures.Add($"Changing a history filter scrolled the page ({state}): before={before}, after={afterRange}.");
        }

        form.ShowSection("usage");
        Application.DoEvents();
        scroll.AutoScrollMinSize = new Size(0, Math.Max(scroll.AutoScrollMinSize.Height, scroll.ClientSize.Height + 1600));
        Application.DoEvents();
        SetScrollForTest(scroll, Math.Max(before, 200));
        before = scroll.VerticalScroll.Value;

        var usageSwitches = FindLabeledSwitches(form, "Enabled");
        if (usageSwitches.Count < 3)
            failures.Add($"Usage page should have Enabled switches for network, disk, and computer use ({state}), found {usageSwitches.Count}.");
        foreach (var historySwitch in usageSwitches.Take(3))
        {
            SetScrollForTest(scroll, Math.Max(before, 200));
            int historyBefore = scroll.VerticalScroll.Value;
            bool wasOn = historySwitch.Checked;
            historySwitch.Checked = !wasOn;
            PumpScrollRestore();
            int historyAfter = scroll.VerticalScroll.Value;
            historySwitch.Checked = wasOn;
            PumpScrollRestore();
            if (Math.Abs(historyAfter - historyBefore) > 40)
                failures.Add($"Toggling a usage Enabled switch scrolled the page ({state}): before={historyBefore}, after={historyAfter}.");
        }

        var customRange = FindSegmented(form, "Custom");
        if (customRange != null)
        {
            SetScrollForTest(scroll, Math.Max(before, 200));
            int customBefore = scroll.VerticalScroll.Value;
            int prior = customRange.SelectIndex;
            customRange.SelectIndex = 9;
            PumpScrollRestore();
            int afterCustom = scroll.VerticalScroll.Value;
            customRange.SelectIndex = prior == 9 ? 3 : prior;
            PumpScrollRestore();
            if (Math.Abs(afterCustom - customBefore) > 40)
                failures.Add($"Switching to Custom scrolled the page ({state}): before={customBefore}, after={afterCustom}.");
        }
    }

    private static void SetScrollForTest(ScrollableControl scroll, int y)
    {
        if (scroll is SettingsForm.SettingsScrollPanel panel)
            panel.SetScrollForTests(0, Math.Max(0, y));
        else
            scroll.AutoScrollPosition = new Point(0, Math.Max(0, y));
        Application.DoEvents();
        Application.DoEvents();
    }

    private static void PumpScrollRestore()
    {
        Application.DoEvents();
        Application.DoEvents();
        System.Threading.Thread.Sleep(500);
        Application.DoEvents();
        Application.DoEvents();
    }

    private static ScrollableControl? FindScrollHost(Control root)
    {
        foreach (Control c in Enumerate(root))
        {
            if (c is ScrollableControl scroll && scroll.AutoScroll && c.Controls.Count > 0 && c.Parent == root)
                return scroll;
        }

        foreach (Control c in Enumerate(root))
        {
            if (c is ScrollableControl scroll && scroll.AutoScroll)
                return scroll;
        }

        return null;
    }

    private static AntdUI.Switch? FindLabeledSwitch(Control root, string nearbyLabel)
        => FindLabeledSwitches(root, nearbyLabel).FirstOrDefault();

    private static List<AntdUI.Switch> FindLabeledSwitches(Control root, string nearbyLabel)
    {
        var found = new List<AntdUI.Switch>();
        foreach (Control c in Enumerate(root))
        {
            if (c is not Label label || !string.Equals(label.Text, nearbyLabel, StringComparison.Ordinal) || label.Parent == null)
                continue;
            AntdUI.Switch? toggle = null;
            foreach (Control child in label.Parent.Controls)
            {
                if (child is AntdUI.Switch sw)
                {
                    toggle = sw;
                    break;
                }
            }

            if (toggle != null)
            {
                found.Add(toggle);
                continue;
            }

            // Caption may sit on a header row; search the next sibling rows in the same card.
            Control? card = label.Parent.Parent;
            if (card == null)
                continue;
            bool seen = false;
            foreach (Control row in card.Controls)
            {
                if (row == label.Parent)
                {
                    seen = true;
                    continue;
                }

                if (!seen)
                    continue;
                foreach (Control child in row.Controls)
                {
                    if (child is AntdUI.Switch sw)
                    {
                        found.Add(sw);
                        toggle = sw;
                        break;
                    }
                }

                if (toggle != null)
                    break;
            }
        }

        return found;
    }

    private static AntdUI.Switch? FindFirstSwitch(Control root)
    {
        foreach (Control c in Enumerate(root))
        {
            if (c is AntdUI.Switch toggle)
                return toggle;
        }

        return null;
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

    private static int CountSegmented(Control root, string itemText)
    {
        int count = 0;
        foreach (Control c in Enumerate(root))
        {
            if (c is not AntdUI.Segmented seg)
                continue;
            foreach (AntdUI.SegmentedItem item in seg.Items)
            {
                if (string.Equals(item.Text, itemText, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
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
