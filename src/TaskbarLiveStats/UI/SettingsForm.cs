using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using ScottPlot.WinForms;
using TaskbarLiveStats.Metrics;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

/// <summary>
/// Settings window. Sections are stacked and scrolled so controls stay readable
/// at the default size and when the window is maximized.
/// </summary>
internal sealed class SettingsForm : AntdUI.Window
{
    private static readonly Color BgApp = Color.FromArgb(22, 22, 24);
    private static readonly Color BgSide = Color.FromArgb(30, 30, 32);
    private static readonly Color BgCard = Color.FromArgb(40, 40, 44);
    private static readonly Color BgFooter = Color.FromArgb(28, 28, 30);
    private static readonly Color TextPrimary = Color.FromArgb(245, 245, 245);
    private static readonly Color TextMuted = Color.FromArgb(170, 170, 175);

    private const int HeaderH = 48;
    private const int FooterH = 68;
    private const int SideW = 208;

    private readonly AppSettings _draft;
    private readonly Action<AppSettings> _apply;
    private readonly Font _titleFont = new("Segoe UI Semibold", 20f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _headingFont = new("Segoe UI Semibold", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _bodyFont = new("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _hintFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _sectionFont = new("Segoe UI Semibold", 9f, FontStyle.Bold, GraphicsUnit.Point);

    private readonly AntdUI.PageHeader _header;
    private readonly Panel _side;
    private readonly AntdUI.Menu _nav;
    private readonly SettingsScrollPanel _content;
    private readonly Panel _footer;
    private readonly AntdUI.Button _btnApply;
    private readonly AntdUI.Button _btnOk;
    private readonly AntdUI.Button _btnCancel;

    private readonly StackPanel _generalPage;
    private readonly StackPanel _monitoringPage;
    private readonly StackPanel _usagePage;
    private readonly StackPanel _appearancePage;
    private readonly StackPanel _aboutPage;
    private readonly Dictionary<MetricKind, ColorSwatch> _swatches = new();

    private AntdUI.Switch? _startWithWindows;
    private AntdUI.Switch? _hideOnAutostart;
    private Panel? _hideOnAutostartRow;
    private Label? _hideOnAutostartHint;
    private AntdUI.Switch? _showOnFullscreen;
    private AntdUI.Switch? _hotkeyEnabled;
    private AntdUI.InputNumber? _intervalMs;
    private AntdUI.Switch? _cpu, _memory, _gpu, _vram, _netSend, _netRecv, _diskRead, _diskWrite, _npu, _npuMemory;
    private AntdUI.Switch? _cpuHide, _memoryHide, _gpuHide, _vramHide, _netSendHide, _netRecvHide, _diskReadHide, _diskWriteHide, _npuHide, _npuMemoryHide;
    private AntdUI.Switch? _netSendTotal, _netRecvTotal, _diskReadTotal, _diskWriteTotal;
    private AntdUI.Segmented? _totalSinceSeg;
    private AntdUI.Switch? _saveNetwork, _saveDisk, _saveComputerUse;
    private AntdUI.Segmented? _networkRange, _diskRange, _computerUseRange;
    private AntdUI.DatePicker? _networkFrom, _networkTo, _diskFrom, _diskTo, _computerUseFrom, _computerUseTo;
    private Panel? _networkFilterRow, _diskFilterRow, _computerUseFilterRow;
    private Panel? _networkCustomRow, _diskCustomRow, _computerUseCustomRow;
    private Panel? _networkTotalRow, _diskTotalRow, _computerUseTotalRow;
    private Label? _networkTotal, _diskTotal, _computerUseTotal;
    private Panel? _networkHost, _diskHost, _computerUseHost;
    private FormsPlot? _networkPlot, _diskPlot, _computerUsePlot;
    private Label? _networkEmpty, _diskEmpty, _computerUseEmpty;
    private int _networkPlotAttempts, _diskPlotAttempts, _computerUsePlotAttempts;
    private const int HistoryDayIndex = 3;
    private const int HistoryCustomIndex = 9;
    private const int HistoryFilterWidth = 980;
    private static readonly string[] HistoryRangeLabels =
        ["15 min", "1 hour", "5 hours", "Day", "Week", "Month", "3 months", "6 months", "Year", "Custom"];
    private readonly System.Windows.Forms.Timer _historyTimer = new() { Interval = 60_000 };
    private AntdUI.Switch? _showDetails;
    private AntdUI.Switch? _showLabelsCollapsed;
    private AntdUI.Switch? _showLabelsExpanded;
    private AntdUI.InputNumber? _detailCount;
    private AntdUI.Segmented? _orientSeg;
    private AntdUI.Segmented? _placeSeg;
    private AntdUI.Segmented? _rateUnitSeg;
    private AntdUI.Segmented? _densitySeg;
    private AntdUI.Select? _edgePreset;
    private AntdUI.Slider? _opacity;
    private Label? _opacityValue;
    private AntdUI.Switch? _alwaysOnTop;
    private AntdUI.InputNumber? _defaultValuePt, _defaultLabelPt, _defaultDetailPt, _compactValuePt, _compactLabelPt, _compactDetailPt;
    private AntdUI.InputNumber? _idleBelow, _highAbove;
    private AntdUI.InputNumber? _ceilSend, _ceilRecv, _ceilRead, _ceilWrite;
    private Label? _ceilSendUnit, _ceilRecvUnit, _ceilReadUnit, _ceilWriteUnit;
    private ColorSwatch? _normalSwatch, _highSwatch;
    private ColorSwatch? _overlayBgSwatch;
    private RateUnit _ceilingUnit = RateUnit.Bytes;

    private const int SwitchW = 52;
    private const int SwitchH = 28;
    private const int ColInset = 24;
    private const int ColGap = 28;
    private const int MetricRowH = 44;

    private bool _chromeReady;
    private bool _inChrome;
    private string _section = "general";

    public SettingsForm(AppSettings current, Action<AppSettings> apply)
    {
        _draft = current.Clone();
        _apply = apply;

        AntdUI.Config.IsDark = true;

        Text = "Taskbar Live Stats — Settings";
        Icon = AppIcons.AppIcon;
        Resizable = true;
        MinimizeBox = true;
        MaximizeBox = true;
        TopMost = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 580);
        BackColor = BgApp;
        ForeColor = TextPrimary;
        Font = _bodyFont;
        Padding = Padding.Empty;

        _header = new AntdUI.PageHeader
        {
            Dock = DockStyle.None,
            Text = "Taskbar Live Stats",
            SubText = "Settings",
            ShowIcon = true,
            ShowButton = true,
            MinimizeBox = true,
            MaximizeBox = true,
            FullBox = false,
            DragMove = true,
            DividerShow = true,
            BackColor = BgSide,
            ForeColor = TextPrimary
        };

        _nav = new AntdUI.Menu
        {
            Dock = DockStyle.None,
            BackColor = BgSide,
            ForeColor = TextPrimary,
            Radius = 8,
            Unique = true
        };
        _nav.Items.Add(new AntdUI.MenuItem { Text = "General", ID = "general" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "Monitoring", ID = "monitoring" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "Usage", ID = "usage" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "Appearance", ID = "appearance" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "About", ID = "about" });
        _nav.SelectChanged += (_, e) => ShowSection(e.Value?.ID ?? "general");

        _side = new Panel { BackColor = BgSide, Margin = Padding.Empty };
        _side.Controls.Add(_nav);

        _content = new SettingsScrollPanel
        {
            BackColor = BgApp,
            Margin = Padding.Empty
        };

        _generalPage = BuildGeneral();
        _monitoringPage = BuildMonitoring();
        _usagePage = BuildUsage();
        _appearancePage = BuildAppearance();
        _aboutPage = BuildAbout();
        foreach (var page in Pages())
            _content.Controls.Add(page);

        _btnCancel = MakeButton("Cancel", AntdUI.TTypeMini.Default);
        _btnCancel.Click += (_, _) => Close();

        _btnOk = MakeButton("OK", AntdUI.TTypeMini.Primary);
        _btnOk.Click += (_, _) =>
        {
            PullFromUi();
            _apply(_draft.Clone());
            Close();
        };

        _btnApply = MakeButton("Apply", AntdUI.TTypeMini.Primary);
        _btnApply.Ghost = true;
        _btnApply.Click += (_, _) =>
        {
            PullFromUi();
            _apply(_draft.Clone());
        };

        _footer = new Panel { BackColor = BgFooter, Margin = Padding.Empty };
        _footer.Controls.Add(_btnApply);
        _footer.Controls.Add(_btnOk);
        _footer.Controls.Add(_btnCancel);
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(58, 58, 64));
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };

        Controls.Add(_content);
        Controls.Add(_side);
        Controls.Add(_footer);
        Controls.Add(_header);

        _historyTimer.Tick += (_, _) => RefreshHistoryChart();
        _nav.SelectIndex(0);
        ShowSection("general");
        SyncFloatingControls();

        ClientSize = new Size(1000, 720);
        _chromeReady = true;
        LayoutChrome();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutChrome();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        BringToFront();
        LayoutChrome();
    }

    private void LayoutChrome()
    {
        if (!_chromeReady || _inChrome)
            return;

        int w = ClientSize.Width;
        int h = ClientSize.Height;
        if (w < 200 || h < 200)
            return;

        _inChrome = true;
        try
        {
            _header.Dock = DockStyle.None;
            _header.SetBounds(0, 0, w, HeaderH);

            int bodyTop = HeaderH;
            int bodyH = Math.Max(80, h - HeaderH - FooterH);
            _side.SetBounds(0, bodyTop, SideW, bodyH);
            _nav.SetBounds(10, 12, Math.Max(40, _side.Width - 20), Math.Max(40, _side.Height - 24));
            _content.SetBounds(SideW, bodyTop, Math.Max(80, w - SideW), bodyH);
            _footer.SetBounds(0, h - FooterH, w, FooterH);
            LayoutFooter();
            LayoutActiveSection(preserveScroll: true);
        }
        finally
        {
            _inChrome = false;
        }
    }

    private void LayoutFooter()
    {
        int x = _footer.Width - 20;
        foreach (var button in new[] { _btnCancel, _btnOk, _btnApply })
        {
            x -= button.Width;
            button.Location = new Point(Math.Max(12, x), Math.Max(0, (_footer.Height - button.Height) / 2));
            x -= 10;
        }
    }

    internal void ShowSection(string id)
    {
        _section = id;
        foreach (var page in Pages())
        {
            bool on = page == ActivePage();
            page.Visible = on;
            if (!on)
                page.Size = new Size(0, 0);
        }

        LayoutActiveSection(preserveScroll: false);
        if (id == "usage")
        {
            RefreshHistoryChart();
            _historyTimer.Start();
        }
        else
        {
            _historyTimer.Stop();
        }

        if (_content.IsHandleCreated)
        {
            _content.AutoScrollPosition = Point.Empty;
            ActivePage().Location = Point.Empty;
        }
    }

    private void LayoutActiveSection(bool preserveScroll, bool refreshRememberedScroll = true)
    {
        if (_content.ClientSize.Width < 40)
            return;

        // Callers that mutate visibility must RememberScroll before the mutation; otherwise
        // refreshing the remembered offset here would capture an already-reset scroll.
        if (preserveScroll)
            _content.BeginScrollPreserve(remember: refreshRememberedScroll);

        try
        {
            var active = ActivePage();
            active.Visible = true;

            int viewW = _content.ClientSize.Width;
            int height = active.Reflow(viewW);
            if (height > _content.ClientSize.Height && _content.ClientSize.Height > 0)
            {
                int bar = SystemInformation.VerticalScrollBarWidth;
                viewW = Math.Max(240, _content.ClientSize.Width - bar - 2);
                height = active.Reflow(viewW);
            }

            active.Location = Point.Empty;
            _content.AutoScrollMinSize = new Size(0, height + 8);
        }
        finally
        {
            if (preserveScroll)
                _content.EndScrollPreserve();
        }
    }

    private StackPanel ActivePage() => _section switch
    {
        "monitoring" => _monitoringPage,
        "usage" => _usagePage,
        "appearance" => _appearancePage,
        "about" => _aboutPage,
        _ => _generalPage
    };

    private IEnumerable<StackPanel> Pages()
    {
        yield return _generalPage;
        yield return _monitoringPage;
        yield return _usagePage;
        yield return _appearancePage;
        yield return _aboutPage;
    }

    private StackPanel BuildGeneral()
    {
        var page = NewPage();
        page.Add(PageTitle("General"));

        var startup = NewCard();
        startup.Add(Heading("Startup"));
        startup.Add(SwitchRow("Autostart when Windows boots", _draft.StartWithWindows, out _startWithWindows));
        _hideOnAutostartRow = SwitchRow("Hide live stats on autostart", _draft.HideOnAutostart, out _hideOnAutostart);
        _hideOnAutostartHint = Hint("When on, the bar stays hidden after Windows starts this app. Open it from the tray icon or the show/hide shortcut.", 48);
        bool showHideOnAutostart = _draft.StartWithWindows;
        _hideOnAutostartRow.Visible = showHideOnAutostart;
        _hideOnAutostartHint.Visible = showHideOnAutostart;
        startup.Add(_hideOnAutostartRow);
        startup.Add(_hideOnAutostartHint);
        if (_startWithWindows != null)
        {
            _startWithWindows.CheckedChanged += (_, _) =>
            {
                bool on = _startWithWindows.Checked;
                bool changed = (_hideOnAutostartRow?.Visible ?? false) != on
                    || (_hideOnAutostartHint?.Visible ?? false) != on;
                if (_hideOnAutostartRow != null)
                    _hideOnAutostartRow.Visible = on;
                if (_hideOnAutostartHint != null)
                    _hideOnAutostartHint.Visible = on;
                if (changed && _chromeReady)
                    LayoutActiveSection(preserveScroll: true, refreshRememberedScroll: false);
            };
        }

        page.Add(startup);

        var fullscreen = NewCard();
        fullscreen.Add(Heading("Fullscreen"));
        fullscreen.Add(SwitchRow("Show on full screen", _draft.ShowOnFullscreen, out _showOnFullscreen));
        fullscreen.Add(Hint("When on, live stats stay visible over fullscreen apps. When off, the overlay hides while any app is fullscreen.", 48));
        page.Add(fullscreen);

        var updates = NewCard();
        updates.Add(Heading("Update frequency"));
        updates.Add(Hint("How often the numbers refresh, in milliseconds (100 – 10000).", 36));
        updates.Add(NumberRow(MakeNumber(_draft.UpdateIntervalMs, 100, 10000, 140), out _intervalMs));
        page.Add(updates);

        var units = NewCard();
        units.Add(Heading("Transfer units"));
        units.Add(Hint("Used for network and disk speeds on the bar, and for the speed ceiling numbers in Monitoring.", 44));
        units.Add(SegmentRow(["Bytes/s", "Bits/s"], _draft.RateUnit == RateUnit.Bits ? 1 : 0, out _rateUnitSeg));
        page.Add(units);

        string hotkey = HotkeyHook.Format(_draft.HotkeyModifiers, _draft.HotkeyVirtualKey);
        var shortcut = NewCard();
        shortcut.Add(Heading("Show / hide shortcut"));
        shortcut.Add(SwitchRow("Enable hotkey", _draft.HotkeyEnabled, out _hotkeyEnabled));
        shortcut.Add(Hint($"{hotkey} removes the overlay, or brings it back. Sampling pauses while it is hidden.", 48));
        page.Add(shortcut);
        return page;
    }

    private StackPanel BuildMonitoring()
    {
        var page = NewPage();
        page.Add(PageTitle("Monitoring"));

        var idle = _draft.HideWhenIdle ?? new IdleHideToggles();
        var totals = _draft.TotalUsage ?? new TotalUsageToggles();
        var metrics = NewCard();
        metrics.Add(Heading("Metrics"));
        metrics.Add(Hint("Hide when idle shows a metric only while it is in use. It returns as soon as the value is above zero, and leaves again after 10 seconds at zero. Total usage adds the byte total on upload, download, disk read, and disk write.", 72));
        metrics.Add(SegmentRow(["Restart", "Startup"], _draft.TotalSince == TotalSince.Startup ? 1 : 0, out _totalSinceSeg));
        metrics.Add(Hint("Restart counts bytes since the last reboot. Startup counts bytes since this PC last came back from a shutdown or a restart. Hibernation, sleep, logout, and lock keep the Startup total.", 56));
        metrics.Add(MetricHeader());
        metrics.Add(SectionLabel("Processor and memory"));
        metrics.Add(MetricRow("CPU", _draft.Metrics.Cpu, out _cpu, idle.Cpu, out _cpuHide, null, out _));
        metrics.Add(MetricRow("RAM", _draft.Metrics.Memory, out _memory, idle.Memory, out _memoryHide, null, out _));
        metrics.Add(SectionLabel("Graphics"));
        metrics.Add(MetricRow("GPU", _draft.Metrics.Gpu, out _gpu, idle.Gpu, out _gpuHide, null, out _));
        metrics.Add(MetricRow("VRAM", _draft.Metrics.Vram, out _vram, idle.Vram, out _vramHide, null, out _));
        metrics.Add(MetricRow("NPU", _draft.Metrics.Npu, out _npu, idle.Npu, out _npuHide, null, out _));
        metrics.Add(MetricRow("NPU memory", _draft.Metrics.NpuMemory, out _npuMemory, idle.NpuMemory, out _npuMemoryHide, null, out _));
        metrics.Add(SectionLabel("Network"));
        metrics.Add(MetricRow("Upload", _draft.Metrics.NetSend, out _netSend, idle.NetSend, out _netSendHide, totals.NetSend, out _netSendTotal));
        metrics.Add(MetricRow("Download", _draft.Metrics.NetRecv, out _netRecv, idle.NetRecv, out _netRecvHide, totals.NetRecv, out _netRecvTotal));
        metrics.Add(SectionLabel("Disk"));
        metrics.Add(MetricRow("Disk read", _draft.Metrics.DiskRead, out _diskRead, idle.DiskRead, out _diskReadHide, totals.DiskRead, out _diskReadTotal));
        metrics.Add(MetricRow("Disk write", _draft.Metrics.DiskWrite, out _diskWrite, idle.DiskWrite, out _diskWriteHide, totals.DiskWrite, out _diskWriteTotal));
        page.Add(metrics);

        var labels = NewCard();
        labels.Add(Heading("Labels"));
        labels.Add(SwitchRow("Show label in minimize mode", _draft.ShowLabelsCollapsed, out _showLabelsCollapsed));
        labels.Add(Hint("When busiest items are hidden. Shows names like CPU and VRAM under each value. Hover still shows the full title when labels are off.", 56));
        labels.Add(SwitchRow("Show label in maximize mode", _draft.ShowLabelsExpanded, out _showLabelsExpanded));
        labels.Add(Hint("When busiest items are shown (left-click the bar). Same names under each value; hover still works when labels are off.", 48));
        page.Add(labels);

        var details = NewCard();
        details.Add(Heading("Busiest items"));
        details.Add(SwitchRow("Show busiest items", _draft.ShowUsageDetails, out _showDetails));
        details.Add(Hint("Sits beside the CPU and GPU block, centered on the icon and value, two per row. The list stops at the cores and engines this PC actually has, even when the maximum is higher. The name stays on the left and the percent on the right. Performance cores are marked P and efficiency cores E when Windows reports them. Left-click the bar to show or hide them.", 100));
        details.Add(Heading("How many"));
        details.Add(NumberRow(MakeNumber(_draft.UsageDetailCount, 1, AppSettings.MaxUsageDetailCount, 140), out _detailCount));
        page.Add(details);

        _ceilingUnit = _draft.RateUnit;
        var ceilings = _draft.RateCeilings ?? new RateCeilings();
        var ceilingCard = NewCard();
        ceilingCard.Add(Heading("Speed ceilings"));
        ceilingCard.Add(Hint("Sets the 100% mark for level colors on upload, download, and disk. 0 leaves that speed on its own color. The number uses the transfer unit from General.", 64));
        ceilingCard.Add(CeilingRow("Upload", CeilingDisplay(ceilings.NetSendBytesPerSec), out _ceilSend, out _ceilSendUnit));
        ceilingCard.Add(CeilingRow("Download", CeilingDisplay(ceilings.NetRecvBytesPerSec), out _ceilRecv, out _ceilRecvUnit));
        ceilingCard.Add(CeilingRow("Disk read", CeilingDisplay(ceilings.DiskReadBytesPerSec), out _ceilRead, out _ceilReadUnit));
        ceilingCard.Add(CeilingRow("Disk write", CeilingDisplay(ceilings.DiskWriteBytesPerSec), out _ceilWrite, out _ceilWriteUnit));
        if (_rateUnitSeg != null)
            _rateUnitSeg.SelectIndexChanged += (_, _) => SyncCeilingUnit();
        page.Add(ceilingCard);
        return page;
    }

    private StackPanel BuildUsage()
    {
        var page = NewPage();
        page.Add(PageTitle("Usage"));
        page.Add(BuildNetworkUsageCard());
        page.Add(BuildDiskUsageCard());
        page.Add(BuildComputerUsageCard());
        return page;
    }

    private StackPanel BuildAppearance()
    {
        var page = NewPage();
        page.Add(PageTitle("Appearance"));

        var size = NewCard();
        size.Add(Heading("Size"));
        size.Add(SegmentRow(["Default", "Compact"], _draft.Density == OverlayDensity.Compact ? 1 : 0, out _densitySeg));
        size.Add(Hint("Both densities use single-line chips sized for the taskbar. Compact hides glyphs and tightens type further; accent color still separates each metric.", 52));
        size.Add(Heading("Font sizes (Default)"));
        size.Add(CaptionRow("Value", "Label (pt)", "Details (pt)"));
        size.Add(FontRow(
            MakeNumber((decimal)_draft.DefaultValueFontPt, 7, 28),
            MakeNumber((decimal)_draft.DefaultLabelFontPt, 6, 18),
            MakeNumber((decimal)_draft.DefaultDetailFontPt, 6, 18),
            out _defaultValuePt,
            out _defaultLabelPt,
            out _defaultDetailPt));
        size.Add(Heading("Font sizes (Compact)"));
        size.Add(CaptionRow("Value", "Label (pt)", "Details (pt)"));
        size.Add(FontRow(
            MakeNumber((decimal)_draft.CompactValueFontPt, 7, 28),
            MakeNumber((decimal)_draft.CompactLabelFontPt, 6, 18),
            MakeNumber((decimal)_draft.CompactDetailFontPt, 6, 18),
            out _compactValuePt,
            out _compactLabelPt,
            out _compactDetailPt));
        page.Add(size);

        var layout = NewCard();
        layout.Add(Heading("Orientation"));
        layout.Add(SegmentRow(["Horizontal", "Vertical"], _draft.Orientation == OverlayOrientation.Vertical ? 1 : 0, out _orientSeg));
        layout.Add(Heading("Placement"));
        layout.Add(SegmentRow(["Taskbar", "Floating"], _draft.PlacementMode == PlacementMode.Floating ? 1 : 0, out var placeSeg));
        _placeSeg = placeSeg;
        placeSeg.SelectIndexChanged += (_, _) => SyncFloatingControls();
        layout.Add(Heading("Edge position (floating)"));
        layout.Add(EdgeRow());
        layout.Add(Heading("Opacity"));
        layout.Add(OpacityRow());
        layout.Add(SwitchRow("Always on top (floating)", _draft.AlwaysOnTop, out _alwaysOnTop));
        var reset = MakeButton("Reset position", AntdUI.TTypeMini.Default);
        reset.Size = new Size(150, 34);
        reset.Click += (_, _) =>
        {
            if (_edgePreset != null)
                _edgePreset.SelectedIndex = 0;
            PullFromUi();
            _draft.FloatingX = int.MinValue;
            _draft.FloatingY = int.MinValue;
            _draft.TaskbarOffsetX = 0;
            _draft.EdgePreset = EdgePreset.None;
            _apply(_draft.Clone());
        };
        layout.Add(ButtonRow(reset));
        page.Add(layout);

        var background = NewCard();
        background.Add(Heading("Background"));
        background.Add(Hint("Fill color of the live stats panel. Reset colors restores the Windows taskbar theme default.", 40));
        background.Add(ColorRow(
            "Panel background",
            ThemeHelper.ResolveOverlayFill(_draft.OverlayBackground),
            out var overlayBgSwatch));
        _overlayBgSwatch = overlayBgSwatch;
        page.Add(background);

        var bands = _draft.UsageColors ?? new UsageColorBands();
        bands.Normalize();
        var colors = NewCard();
        colors.Add(Heading("Metric colors"));
        colors.Add(Hint("Each metric uses this color while it is idle. Click a swatch to change it.", 40));
        foreach (var (kind, name) in MetricColors.All)
        {
            var row = ColorRow(name, MetricColors.Resolve(_draft.Colors, kind), out var swatch);
            _swatches[kind] = swatch;
            colors.Add(row);
        }

        colors.Add(Heading("Usage levels"));
        colors.Add(Hint("Below the idle mark, each metric keeps its own color. From that mark through the high mark uses Normal. Above the high mark uses High.", 48));
        colors.Add(LabeledNumberRow("Idle below (%)", MakeNumber(bands.IdleBelowPercent, 0, 99, 100), out _idleBelow));
        colors.Add(LabeledNumberRow("High above (%)", MakeNumber(bands.HighAbovePercent, 1, 100, 100), out _highAbove));
        colors.Add(ColorRow("Normal", MetricPalette.Parse(bands.Normal, UsageColorBands.DefaultNormal), out var normalSwatch));
        colors.Add(ColorRow("High", MetricPalette.Parse(bands.High, UsageColorBands.DefaultHigh), out var highSwatch));
        _normalSwatch = normalSwatch;
        _highSwatch = highSwatch;

        var resetColors = MakeButton("Reset colors", AntdUI.TTypeMini.Default);
        resetColors.Size = new Size(140, 34);
        resetColors.Click += (_, _) =>
        {
            var defaults = new MetricPalette();
            foreach (var (kind, swatch) in _swatches)
                swatch.SetColor(MetricColors.Resolve(defaults, kind));
            var levelDefaults = new UsageColorBands();
            _normalSwatch?.SetColor(MetricPalette.Parse(levelDefaults.Normal, UsageColorBands.DefaultNormal));
            _highSwatch?.SetColor(MetricPalette.Parse(levelDefaults.High, UsageColorBands.DefaultHigh));
            if (_idleBelow != null) _idleBelow.Value = UsageColorBands.DefaultIdleBelow;
            if (_highAbove != null) _highAbove.Value = UsageColorBands.DefaultHighAbove;
            _draft.OverlayBackground = "";
            _overlayBgSwatch?.SetColor(ThemeHelper.TaskbarFillColor);
        };
        colors.Add(ButtonRow(resetColors));
        page.Add(colors);
        return page;
    }

    private StackPanel BuildAbout()
    {
        var page = NewPage();
        page.Add(PageTitle("About"));
        string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var card = NewCard();

        var brandRow = new Panel
        {
            Height = 88,
            BackColor = BgCard,
            Padding = new Padding(0, 4, 0, 4)
        };
        brandRow.Controls.Add(new PictureBox
        {
            Image = AppIcons.Logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(72, 72),
            Location = new Point(0, 8),
            BackColor = Color.Transparent
        });
        brandRow.Controls.Add(new Label
        {
            Text = "Taskbar Live Stats",
            AutoSize = false,
            Bounds = new Rectangle(88, 18, 420, 32),
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        });
        brandRow.Controls.Add(new Label
        {
            Text = $"Version {ver}",
            AutoSize = false,
            Bounds = new Rectangle(88, 50, 420, 24),
            Font = _bodyFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        });
        card.Add(brandRow);
        card.Add(Hint("Live CPU, memory, GPU, network, and disk stats on your desktop.", 36));
        page.Add(card);
        return page;
    }

    private void SyncFloatingControls()
    {
        bool floating = _placeSeg?.SelectIndex == 1;
        if (_edgePreset != null) _edgePreset.Enabled = floating;
        if (_opacity != null) _opacity.Enabled = floating;
        if (_alwaysOnTop != null) _alwaysOnTop.Enabled = floating;
    }

    private void PullFromUi()
    {
        if (_startWithWindows != null)
            _draft.StartWithWindows = _startWithWindows.Checked;
        if (_hideOnAutostart != null)
            _draft.HideOnAutostart = _hideOnAutostart.Checked;
        if (_showOnFullscreen != null)
            _draft.ShowOnFullscreen = _showOnFullscreen.Checked;
        if (_hotkeyEnabled != null)
            _draft.HotkeyEnabled = _hotkeyEnabled.Checked;
        if (_intervalMs != null)
            _draft.UpdateIntervalMs = (int)Math.Clamp(_intervalMs.Value, 100, 10000);

        if (_cpu != null) _draft.Metrics.Cpu = _cpu.Checked;
        if (_memory != null) _draft.Metrics.Memory = _memory.Checked;
        if (_gpu != null) _draft.Metrics.Gpu = _gpu.Checked;
        if (_vram != null) _draft.Metrics.Vram = _vram.Checked;
        if (_netSend != null) _draft.Metrics.NetSend = _netSend.Checked;
        if (_netRecv != null) _draft.Metrics.NetRecv = _netRecv.Checked;
        if (_diskRead != null) _draft.Metrics.DiskRead = _diskRead.Checked;
        if (_diskWrite != null) _draft.Metrics.DiskWrite = _diskWrite.Checked;
        if (_npu != null) _draft.Metrics.Npu = _npu.Checked;
        if (_npuMemory != null) _draft.Metrics.NpuMemory = _npuMemory.Checked;

        _draft.HideWhenIdle ??= new IdleHideToggles();
        if (_cpuHide != null) _draft.HideWhenIdle.Cpu = _cpuHide.Checked;
        if (_memoryHide != null) _draft.HideWhenIdle.Memory = _memoryHide.Checked;
        if (_gpuHide != null) _draft.HideWhenIdle.Gpu = _gpuHide.Checked;
        if (_vramHide != null) _draft.HideWhenIdle.Vram = _vramHide.Checked;
        if (_netSendHide != null) _draft.HideWhenIdle.NetSend = _netSendHide.Checked;
        if (_netRecvHide != null) _draft.HideWhenIdle.NetRecv = _netRecvHide.Checked;
        if (_diskReadHide != null) _draft.HideWhenIdle.DiskRead = _diskReadHide.Checked;
        if (_diskWriteHide != null) _draft.HideWhenIdle.DiskWrite = _diskWriteHide.Checked;
        if (_npuHide != null) _draft.HideWhenIdle.Npu = _npuHide.Checked;
        if (_npuMemoryHide != null) _draft.HideWhenIdle.NpuMemory = _npuMemoryHide.Checked;

        _draft.TotalUsage ??= new TotalUsageToggles();
        if (_netSendTotal != null) _draft.TotalUsage.NetSend = _netSendTotal.Checked;
        if (_netRecvTotal != null) _draft.TotalUsage.NetRecv = _netRecvTotal.Checked;
        if (_diskReadTotal != null) _draft.TotalUsage.DiskRead = _diskReadTotal.Checked;
        if (_diskWriteTotal != null) _draft.TotalUsage.DiskWrite = _diskWriteTotal.Checked;
        if (_totalSinceSeg != null)
            _draft.TotalSince = _totalSinceSeg.SelectIndex == 1 ? TotalSince.Startup : TotalSince.Restart;
        if (_saveNetwork != null) _draft.SaveNetworkHistory = _saveNetwork.Checked;
        if (_saveDisk != null) _draft.SaveDiskHistory = _saveDisk.Checked;
        if (_saveComputerUse != null) _draft.SaveComputerUseHistory = _saveComputerUse.Checked;
        _draft.SaveUsageHistory = _draft.SaveNetworkHistory || _draft.SaveDiskHistory || _draft.SaveComputerUseHistory;

        SyncCeilingUnit();
        WriteCeilings(_ceilingUnit);

        if (_showDetails != null) _draft.ShowUsageDetails = _showDetails.Checked;
        if (_showLabelsCollapsed != null) _draft.ShowLabelsCollapsed = _showLabelsCollapsed.Checked;
        if (_showLabelsExpanded != null) _draft.ShowLabelsExpanded = _showLabelsExpanded.Checked;
        if (_detailCount != null)
            _draft.UsageDetailCount = (int)Math.Clamp(_detailCount.Value, 1, AppSettings.MaxUsageDetailCount);

        _draft.Orientation = _orientSeg?.SelectIndex == 1
            ? OverlayOrientation.Vertical
            : OverlayOrientation.Horizontal;

        _draft.PlacementMode = _placeSeg?.SelectIndex == 1
            ? PlacementMode.Floating
            : PlacementMode.Taskbar;

        _draft.RateUnit = _rateUnitSeg?.SelectIndex == 1
            ? RateUnit.Bits
            : RateUnit.Bytes;

        _draft.Density = _densitySeg?.SelectIndex == 1
            ? OverlayDensity.Compact
            : OverlayDensity.Default;

        if (_defaultValuePt != null) _draft.DefaultValueFontPt = (float)_defaultValuePt.Value;
        if (_defaultLabelPt != null) _draft.DefaultLabelFontPt = (float)_defaultLabelPt.Value;
        if (_defaultDetailPt != null) _draft.DefaultDetailFontPt = (float)_defaultDetailPt.Value;
        if (_compactValuePt != null) _draft.CompactValueFontPt = (float)_compactValuePt.Value;
        if (_compactLabelPt != null) _draft.CompactLabelFontPt = (float)_compactLabelPt.Value;
        if (_compactDetailPt != null) _draft.CompactDetailFontPt = (float)_compactDetailPt.Value;

        if (_edgePreset != null)
            _draft.EdgePreset = (EdgePreset)Math.Clamp(_edgePreset.SelectedIndex, 0, 4);

        if (_opacity != null)
            _draft.Opacity = _opacity.Value / 100.0;

        if (_alwaysOnTop != null)
            _draft.AlwaysOnTop = _alwaysOnTop.Checked;

        _draft.Colors ??= new MetricPalette();
        foreach (var (kind, swatch) in _swatches)
            MetricColors.Assign(_draft.Colors, kind, swatch.BackColor);

        if (_overlayBgSwatch != null)
            _draft.OverlayBackground = MetricPalette.ToHex(_overlayBgSwatch.BackColor);

        _draft.UsageColors ??= new UsageColorBands();
        if (_idleBelow != null) _draft.UsageColors.IdleBelowPercent = (int)_idleBelow.Value;
        if (_highAbove != null) _draft.UsageColors.HighAbovePercent = (int)_highAbove.Value;
        if (_normalSwatch != null) _draft.UsageColors.Normal = MetricPalette.ToHex(_normalSwatch.BackColor);
        if (_highSwatch != null) _draft.UsageColors.High = MetricPalette.ToHex(_highSwatch.BackColor);
        _draft.UsageColors.Normalize();
        if (_idleBelow != null) _idleBelow.Value = _draft.UsageColors.IdleBelowPercent;
        if (_highAbove != null) _highAbove.Value = _draft.UsageColors.HighAbovePercent;
    }

    private StackPanel NewPage() => new(card: false);

    private StackPanel NewCard() => new(card: true);

    private Label PageTitle(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Height = 46,
        Font = _titleFont,
        ForeColor = TextPrimary,
        BackColor = BgApp,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Height = 32,
        Font = _headingFont,
        ForeColor = TextPrimary,
        BackColor = BgCard,
        TextAlign = ContentAlignment.BottomLeft
    };

    private Label Hint(string text, int height) => new()
    {
        Text = text,
        AutoSize = false,
        Height = height,
        Font = _hintFont,
        ForeColor = TextMuted,
        BackColor = BgCard,
        TextAlign = ContentAlignment.TopLeft
    };

    private readonly record struct SwitchCols(
        int TotalLeft, int TotalWidth,
        int HideLeft, int HideWidth,
        int ShowLeft, int ShowWidth);

    private SwitchCols ColumnsFor(int rowWidth, int totalText, int hideText, int showText)
    {
        int showW = Math.Max(SwitchW, showText);
        int hideW = Math.Max(SwitchW, hideText);
        int totalW = Math.Max(SwitchW, totalText);
        int showLeft = Math.Max(0, rowWidth - ColInset - showW);
        int hideLeft = Math.Max(0, showLeft - ColGap - hideW);
        int totalLeft = Math.Max(0, hideLeft - ColGap - totalW);
        return new SwitchCols(totalLeft, totalW, hideLeft, hideW, showLeft, showW);
    }

    private int CaptionWidth(Label? caption, string fallback)
    {
        Font font = caption?.Font ?? _hintFont;
        int measured = TextRenderer.MeasureText(
            fallback,
            font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.LeftAndRightPadding).Width;
        int preferred = caption?.PreferredSize.Width ?? 0;
        return Math.Max(measured, preferred) + 10;
    }

    private AntdUI.Switch MakeSwitch(bool isChecked)
    {
        var sw = new AntdUI.Switch
        {
            Size = new Size(SwitchW, SwitchH),
            Checked = isChecked
        };
        KeepScrollOnInteract(sw);
        sw.CheckedChanged += (_, _) =>
        {
            // Capture before layout handlers run; a later visibility change may clamp to 0.
            if (_content.VerticalScroll.Value > 0 || _content.HorizontalScroll.Value > 0)
                _content.RememberScroll();
            _content.RestoreScrollSoon();
        };
        return sw;
    }

    private void KeepScrollOnInteract(Control control)
    {
        control.MouseDown += (_, _) => _content.RememberScroll();
        control.GotFocus += (_, _) =>
        {
            if (_content.VerticalScroll.Value > 0)
                _content.RememberScroll();
            _content.RestoreScrollSoon();
        };
    }

    private void PlaceMetricColumns(
        Control row,
        Label? metric,
        Control? total,
        Control? hide,
        Control? show,
        Label? totalCaption,
        Label? hideCaption,
        Label? showCaption)
    {
        SwitchCols cols = ColumnsFor(
            Math.Max(0, row.Width),
            CaptionWidth(totalCaption, "Total usage"),
            CaptionWidth(hideCaption, "Hide when idle"),
            CaptionWidth(showCaption, "Show"));
        PlaceColumnCaption(totalCaption, cols.TotalLeft, cols.TotalWidth, row);
        PlaceColumnCaption(hideCaption, cols.HideLeft, cols.HideWidth, row);
        PlaceColumnCaption(showCaption, cols.ShowLeft, cols.ShowWidth, row);
        PlaceColumnSwitch(total, cols.TotalLeft, cols.TotalWidth, row);
        PlaceColumnSwitch(hide, cols.HideLeft, cols.HideWidth, row);
        PlaceColumnSwitch(show, cols.ShowLeft, cols.ShowWidth, row);

        if (metric != null)
            metric.SetBounds(0, 0, Math.Max(20, cols.TotalLeft - 12), row.Height);
        totalCaption?.BringToFront();
        hideCaption?.BringToFront();
        showCaption?.BringToFront();
        total?.BringToFront();
        hide?.BringToFront();
        show?.BringToFront();
    }

    private static void PlaceColumnCaption(Label? caption, int left, int width, Control row)
    {
        if (caption == null)
            return;
        caption.SetBounds(left, 0, width, Math.Max(1, row.Height - 1));
    }

    private static void PlaceColumnSwitch(Control? toggle, int left, int width, Control row)
    {
        if (toggle == null)
            return;
        toggle.Left = left + Math.Max(0, (width - toggle.Width) / 2);
        toggle.Top = Math.Max(0, (row.Height - toggle.Height) / 2);
    }

    private Panel MetricHeader()
    {
        var row = new Panel { Height = 36, BackColor = BgCard, Margin = Padding.Empty };
        var metric = new Label
        {
            Text = "Metric",
            AutoSize = false,
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var total = new Label
        {
            Text = "Total usage",
            AutoSize = false,
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleCenter
        };
        var hide = new Label
        {
            Text = "Hide when idle",
            AutoSize = false,
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleCenter
        };
        var show = new Label
        {
            Text = "Show",
            AutoSize = false,
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleCenter
        };
        row.Controls.Add(metric);
        row.Controls.Add(total);
        row.Controls.Add(hide);
        row.Controls.Add(show);
        row.Resize += (_, _) => PlaceMetricColumns(row, metric, null, null, null, total, hide, show);
        row.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(58, 58, 64));
            int y = row.Height - 1;
            e.Graphics.DrawLine(pen, 0, y, row.Width, y);
        };
        return row;
    }

    private Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Height = 40,
        Font = _sectionFont,
        ForeColor = TextMuted,
        BackColor = BgCard,
        TextAlign = ContentAlignment.BottomLeft
    };

    private Panel MetricRow(
        string name,
        bool shown,
        out AntdUI.Switch showSwitch,
        bool hideIdle,
        out AntdUI.Switch hideSwitch,
        bool? showTotal,
        out AntdUI.Switch? totalSwitch)
    {
        var row = new Panel { Height = MetricRowH, BackColor = BgCard, Margin = Padding.Empty };
        var label = new Label
        {
            Text = name,
            AutoSize = false,
            Font = _bodyFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        hideSwitch = MakeSwitch(hideIdle);
        showSwitch = MakeSwitch(shown);
        totalSwitch = showTotal is bool on ? MakeSwitch(on) : null;
        var show = showSwitch;
        var hide = hideSwitch;
        var total = totalSwitch;
        row.Controls.Add(label);
        row.Controls.Add(hide);
        row.Controls.Add(show);
        if (total != null)
            row.Controls.Add(total);
        row.Resize += (_, _) => PlaceMetricColumns(row, label, total, hide, show, null, null, null);
        return row;
    }

    private StackPanel BuildNetworkUsageCard()
    {
        var card = NewCard();
        card.Add(Heading("Network usage"));
        card.Add(Hint("Records upload and download. Turning it off stops new samples and collapses the chart. Saved samples stay.", 48));
        card.Add(SwitchRow("Enabled", _draft.SaveNetworkHistory, out _saveNetwork));
        _networkFilterRow = SegmentRow(HistoryRangeLabels, HistoryDayIndex, out _networkRange, HistoryFilterWidth);
        _networkFilterRow.Visible = _draft.SaveNetworkHistory;
        card.Add(_networkFilterRow);
        _networkCustomRow = BuildHistoryCustomRow(out _networkFrom, out _networkTo, RefreshNetworkHistory);
        card.Add(_networkCustomRow);
        _networkTotalRow = BuildHistoryTotalRow(out _networkTotal);
        _networkTotalRow.Visible = _draft.SaveNetworkHistory;
        card.Add(_networkTotalRow);
        _networkHost = CreateHistoryHost(out _networkEmpty);
        _networkHost.Visible = _draft.SaveNetworkHistory;
        EnsureHistoryPlot(_networkHost, ref _networkPlot, ref _networkPlotAttempts);
        card.Add(_networkHost);
        BindHistorySection(_saveNetwork, _networkFilterRow, _networkCustomRow, _networkTotalRow, _networkHost, _networkRange, RefreshNetworkHistory);
        return card;
    }

    private StackPanel BuildDiskUsageCard()
    {
        var card = NewCard();
        card.Add(Heading("Disk usage"));
        card.Add(Hint("Records disk read and write. Turning it off stops new samples and collapses the chart. Saved samples stay.", 48));
        card.Add(SwitchRow("Enabled", _draft.SaveDiskHistory, out _saveDisk));
        _diskFilterRow = SegmentRow(HistoryRangeLabels, HistoryDayIndex, out _diskRange, HistoryFilterWidth);
        _diskFilterRow.Visible = _draft.SaveDiskHistory;
        card.Add(_diskFilterRow);
        _diskCustomRow = BuildHistoryCustomRow(out _diskFrom, out _diskTo, RefreshDiskHistory);
        card.Add(_diskCustomRow);
        _diskTotalRow = BuildHistoryTotalRow(out _diskTotal);
        _diskTotalRow.Visible = _draft.SaveDiskHistory;
        card.Add(_diskTotalRow);
        _diskHost = CreateHistoryHost(out _diskEmpty);
        _diskHost.Visible = _draft.SaveDiskHistory;
        EnsureHistoryPlot(_diskHost, ref _diskPlot, ref _diskPlotAttempts);
        card.Add(_diskHost);
        BindHistorySection(_saveDisk, _diskFilterRow, _diskCustomRow, _diskTotalRow, _diskHost, _diskRange, RefreshDiskHistory);
        return card;
    }

    private StackPanel BuildComputerUsageCard()
    {
        var card = NewCard();
        card.Add(Heading("Computer usage"));
        card.Add(Hint("Counts awake time while this app is running. Sleep and hibernate pause counting. Turning it off stops new samples and collapses the chart. Saved samples stay.", 56));
        card.Add(SwitchRow("Enabled", _draft.SaveComputerUseHistory, out _saveComputerUse));
        _computerUseFilterRow = SegmentRow(HistoryRangeLabels, HistoryDayIndex, out _computerUseRange, HistoryFilterWidth);
        _computerUseFilterRow.Visible = _draft.SaveComputerUseHistory;
        card.Add(_computerUseFilterRow);
        _computerUseCustomRow = BuildHistoryCustomRow(out _computerUseFrom, out _computerUseTo, RefreshComputerUseHistory);
        card.Add(_computerUseCustomRow);
        _computerUseTotalRow = BuildHistoryTotalRow(out _computerUseTotal);
        _computerUseTotalRow.Visible = _draft.SaveComputerUseHistory;
        card.Add(_computerUseTotalRow);
        _computerUseHost = CreateHistoryHost(out _computerUseEmpty);
        _computerUseHost.Visible = _draft.SaveComputerUseHistory;
        EnsureHistoryPlot(_computerUseHost, ref _computerUsePlot, ref _computerUsePlotAttempts);
        card.Add(_computerUseHost);
        BindHistorySection(_saveComputerUse, _computerUseFilterRow, _computerUseCustomRow, _computerUseTotalRow, _computerUseHost, _computerUseRange, RefreshComputerUseHistory);
        return card;
    }

    private void BindHistorySection(
        AntdUI.Switch? toggle,
        Panel filterRow,
        Panel customRow,
        Panel totalRow,
        Panel host,
        AntdUI.Segmented? range,
        Action refresh)
    {
        if (range != null)
        {
            range.SelectIndexChanged += (_, _) =>
            {
                _content.BeginScrollPreserve(remember: true);
                try
                {
                    bool custom = range.SelectIndex == HistoryCustomIndex;
                    bool showCustom = custom && (toggle?.Checked ?? false);
                    if (customRow.Visible != showCustom)
                    {
                        customRow.Visible = showCustom;
                        if (_chromeReady)
                            LayoutActiveSection(preserveScroll: true, refreshRememberedScroll: false);
                    }

                    refresh();
                }
                finally
                {
                    _content.EndScrollPreserve(holdMs: 800);
                }
            };
        }

        if (toggle == null)
            return;
        toggle.CheckedChanged += (_, _) =>
        {
            _content.BeginScrollPreserve(remember: true);
            try
            {
                bool on = toggle.Checked;
                bool custom = (range?.SelectIndex ?? HistoryDayIndex) == HistoryCustomIndex;
                bool changed = filterRow.Visible != on
                    || host.Visible != on
                    || totalRow.Visible != on
                    || customRow.Visible != (on && custom);
                filterRow.Visible = on;
                host.Visible = on;
                totalRow.Visible = on;
                customRow.Visible = on && custom;
                if (changed && _chromeReady)
                    LayoutActiveSection(preserveScroll: true, refreshRememberedScroll: false);
                refresh();
            }
            finally
            {
                _content.EndScrollPreserve(holdMs: 800);
            }
        };
    }

    private Panel CreateHistoryHost(out Label empty)
    {
        var host = new Panel { Height = 280, BackColor = BgCard, Margin = Padding.Empty };
        empty = new Label
        {
            Text = "No history yet.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted,
            BackColor = BgCard,
            Font = _bodyFont,
            Visible = false
        };
        host.Controls.Add(empty);
        return host;
    }

    private Panel BuildHistoryTotalRow(out Label total)
    {
        var row = new Panel { Height = 32, BackColor = BgCard, Margin = Padding.Empty };
        total = new Label
        {
            Text = "",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = _headingFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        row.Controls.Add(total);
        return row;
    }

    private Panel BuildHistoryCustomRow(out AntdUI.DatePicker fromPicker, out AntdUI.DatePicker toPicker, Action refresh)
    {
        var row = new Panel { Height = 44, BackColor = BgCard, Margin = Padding.Empty, Visible = false };
        const int startWidth = 88;
        const int endWidth = 80;
        var fromCaption = HistoryCaption("Start date", startWidth);
        var toCaption = HistoryCaption("End date", endWidth);
        fromPicker = new AntdUI.DatePicker
        {
            Size = new Size(150, 36),
            Format = "yyyy-MM-dd",
            Value = DateTime.Today.AddDays(-7)
        };
        toPicker = new AntdUI.DatePicker
        {
            Size = new Size(150, 36),
            Format = "yyyy-MM-dd",
            Value = DateTime.Today
        };
        row.Controls.Add(fromCaption);
        row.Controls.Add(fromPicker);
        row.Controls.Add(toCaption);
        row.Controls.Add(toPicker);
        var from = fromPicker;
        var to = toPicker;
        row.Resize += (_, _) =>
        {
            int y = Math.Max(0, (row.Height - 36) / 2);
            fromCaption.SetBounds(0, y, startWidth, 36);
            from.Location = new Point(fromCaption.Right + 8, y);
            toCaption.SetBounds(from.Right + 16, y, endWidth, 36);
            to.Location = new Point(toCaption.Right + 8, y);
        };
        KeepScrollOnInteract(from);
        KeepScrollOnInteract(to);
        from.ValueChanged += (_, _) =>
        {
            _content.RestoreScrollSoon();
            refresh();
        };
        to.ValueChanged += (_, _) =>
        {
            _content.RestoreScrollSoon();
            refresh();
        };
        return row;
    }

    private Label HistoryCaption(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(width, 36),
        Font = _bodyFont,
        ForeColor = TextMuted,
        BackColor = BgCard,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static void EnsureHistoryPlot(Panel? host, ref FormsPlot? plot, ref int attempts)
    {
        if (plot != null || host == null || !host.Visible || attempts >= 2)
            return;

        attempts++;
        try
        {
            var created = new FormsPlot { Dock = DockStyle.Fill };
            created.UserInputProcessor.Disable();
            var bg = ScottPlot.Color.FromHex("#28282C");
            created.Plot.FigureBackground.Color = bg;
            created.Plot.DataBackground.Color = bg;
            created.MouseMove += HistoryPlotMouseMove;
            created.MouseLeave += (_, _) =>
            {
                if (created.Tag is HistoryHoverState hover && hover.Visible)
                {
                    ClearHistoryHover(hover);
                    created.Refresh();
                }
            };
            host.Controls.Add(created);
            created.SendToBack();
            plot = created;
        }
        catch
        {
            // Skia may be unavailable until the page is shown. The next call retries once.
        }
    }

    private static UsageRange RangeFromIndex(int index) => index switch
    {
        0 => UsageRange.Minutes15,
        1 => UsageRange.Hour,
        2 => UsageRange.Hours5,
        3 => UsageRange.Day,
        4 => UsageRange.Week,
        5 => UsageRange.Month,
        6 => UsageRange.Months3,
        7 => UsageRange.Months6,
        8 => UsageRange.Year,
        9 => UsageRange.Custom,
        _ => UsageRange.Day
    };

    private void RefreshHistoryChart()
    {
        RefreshNetworkHistory();
        RefreshDiskHistory();
        RefreshComputerUseHistory();
    }

    private void RefreshNetworkHistory() => RefreshHistorySection(
        _saveNetwork,
        _networkRange,
        _networkFrom,
        _networkTo,
        _networkHost,
        ref _networkPlot,
        ref _networkPlotAttempts,
        _networkEmpty,
        _networkTotal,
        network: true);

    private void RefreshDiskHistory() => RefreshHistorySection(
        _saveDisk,
        _diskRange,
        _diskFrom,
        _diskTo,
        _diskHost,
        ref _diskPlot,
        ref _diskPlotAttempts,
        _diskEmpty,
        _diskTotal,
        network: false);

    private void RefreshComputerUseHistory()
    {
        if (_computerUseHost == null || !(_saveComputerUse?.Checked ?? false))
        {
            if (_computerUseTotal != null)
                _computerUseTotal.Text = "";
            return;
        }

        EnsureHistoryPlot(_computerUseHost, ref _computerUsePlot, ref _computerUsePlotAttempts);
        try
        {
            var usageRange = RangeFromIndex(_computerUseRange?.SelectIndex ?? HistoryDayIndex);
            var window = UsageHistoryStore.Resolve(usageRange, DateTime.Now, _computerUseFrom?.Value, _computerUseTo?.Value);
            bool any = UsageHistoryStore.HasAnyUptime(UsageHistoryStore.DefaultPath);
            UptimeBucket[]? buckets = any ? UsageHistoryStore.ReadUptime(UsageHistoryStore.DefaultPath, window) : null;
            PaintUptimeHistory(_computerUseHost, _computerUsePlot, _computerUseEmpty, _computerUseTotal, buckets, any, window.BucketSeconds);
        }
        catch
        {
            if (_computerUseTotal != null)
                _computerUseTotal.Text = "";
            ShowHistoryEmpty(_computerUseEmpty, _computerUsePlot, "No history yet.");
        }
    }

    private void RefreshHistorySection(
        AntdUI.Switch? toggle,
        AntdUI.Segmented? range,
        AntdUI.DatePicker? from,
        AntdUI.DatePicker? to,
        Panel? host,
        ref FormsPlot? plot,
        ref int plotAttempts,
        Label? empty,
        Label? total,
        bool network)
    {
        if (host == null || !(toggle?.Checked ?? false))
        {
            if (total != null)
                total.Text = "";
            return;
        }

        EnsureHistoryPlot(host, ref plot, ref plotAttempts);
        try
        {
            var usageRange = RangeFromIndex(range?.SelectIndex ?? HistoryDayIndex);
            var window = UsageHistoryStore.Resolve(usageRange, DateTime.Now, from?.Value, to?.Value);
            bool any = UsageHistoryStore.HasAny(UsageHistoryStore.DefaultPath);
            UsageBucket[]? buckets = any ? UsageHistoryStore.Read(UsageHistoryStore.DefaultPath, window) : null;
            PaintHistory(host, plot, empty, total, buckets, any, network, window.BucketSeconds);
        }
        catch
        {
            if (total != null)
                total.Text = "";
            ShowHistoryEmpty(empty, plot, "No history yet.");
        }
    }

    private static void PaintHistory(
        Panel? host,
        FormsPlot? plot,
        Label? empty,
        Label? total,
        UsageBucket[]? buckets,
        bool any,
        bool network,
        int bucketSeconds)
    {
        if (host == null || !host.Visible)
            return;
        if (!any || plot == null || buckets == null)
        {
            if (total != null)
                total.Text = "";
            if (plot != null)
            {
                plot.Tag = null;
                plot.Visible = false;
            }

            ShowHistoryEmpty(empty, plot, plot == null && any ? "History chart is unavailable." : "No history yet.");
            return;
        }

        empty?.Hide();
        plot.Visible = true;
        var chart = plot.Plot;
        chart.Clear();

        string firstName = network ? "Upload" : "Disk read";
        string secondName = network ? "Download" : "Disk write";
        string firstHex = network ? "#FF6E40" : "#E040FB";
        string secondHex = network ? "#448AFF" : "#EEFF41";

        double firstSum = 0;
        double secondSum = 0;
        HistoryHoverPoint[] hoverPoints = [];
        double hoverStep = Math.Max(bucketSeconds, 60) / 86_400.0;
        if (buckets.Length > 0)
        {
            double[] xs = new double[buckets.Length];
            double[] first = new double[buckets.Length];
            double[] second = new double[buckets.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                xs[i] = buckets[i].LocalStart.ToOADate();
                if (network)
                {
                    first[i] = buckets[i].NetSend;
                    second[i] = buckets[i].NetRecv;
                }
                else
                {
                    first[i] = buckets[i].DiskRead;
                    second[i] = buckets[i].DiskWrite;
                }

                firstSum += first[i];
                secondSum += second[i];
            }

            double step = xs.Length > 1
                ? Math.Abs(xs[1] - xs[0])
                : Math.Max(bucketSeconds, 60) / 86_400.0;
            // Coarser buckets (10 min+) get wider paired bars; dense minute views stay narrower.
            bool coarse = bucketSeconds >= 600 && bucketSeconds < 86_400;
            double barSize = coarse ? step * 0.34 : Math.Max(step * 0.38, step * 0.18);
            double offset = coarse ? step * 0.19 : step * 0.22;
            AddHistoryBars(chart, xs, first, firstName, firstHex, -offset, barSize);
            AddHistoryBars(chart, xs, second, secondName, secondHex, offset, barSize);

            hoverPoints = new HistoryHoverPoint[buckets.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                hoverPoints[i] = new HistoryHoverPoint(
                    xs[i],
                    first[i],
                    second[i],
                    buckets[i].LocalStart,
                    firstName,
                    secondName);
            }

            hoverStep = step;
        }

        if (total != null)
        {
            total.Text = "Total: "
                + firstName + " " + GroupLayout.FormatVolume(firstSum)
                + "  ·  "
                + secondName + " " + GroupLayout.FormatVolume(secondSum);
        }

        chart.Axes.DateTimeTicksBottom();
        ConfigureHistoryBottomTicks(chart, bucketSeconds);

        chart.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = GroupLayout.FormatVolume
        };
        StyleHistoryPlot(chart);
        chart.ShowLegend(ScottPlot.Alignment.UpperRight);

        var highlight = chart.Add.HorizontalSpan(0, 0);
        highlight.FillColor = ScottPlot.Color.FromHex("#FFFFFF").WithAlpha(28);
        highlight.LineColor = ScottPlot.Color.FromHex("#F2F2F5").WithAlpha(90);
        highlight.LineWidth = 1;
        highlight.EnableAutoscale = false;
        highlight.IsVisible = false;

        var tip = chart.Add.Annotation("");
        tip.LabelFontColor = ScottPlot.Color.FromHex("#F2F2F5");
        tip.LabelBackgroundColor = ScottPlot.Color.FromHex("#1E1E22");
        tip.LabelBorderColor = ScottPlot.Color.FromHex("#5A5A62");
        tip.LabelBorderWidth = 1;
        tip.LabelShadowColor = ScottPlot.Colors.Transparent;
        tip.LabelFontSize = 16;
        tip.OffsetX = 12;
        tip.OffsetY = 12;
        tip.IsVisible = false;
        plot.Tag = new HistoryHoverState(hoverPoints, tip, highlight, hoverStep, duration: false);
        plot.Refresh();
    }

    private static void PaintUptimeHistory(
        Panel? host,
        FormsPlot? plot,
        Label? empty,
        Label? total,
        UptimeBucket[]? buckets,
        bool any,
        int bucketSeconds)
    {
        if (host == null || !host.Visible)
            return;
        if (!any || plot == null || buckets == null)
        {
            if (total != null)
                total.Text = "";
            if (plot != null)
            {
                plot.Tag = null;
                plot.Visible = false;
            }

            ShowHistoryEmpty(empty, plot, plot == null && any ? "History chart is unavailable." : "No history yet.");
            return;
        }

        empty?.Hide();
        plot.Visible = true;
        var chart = plot.Plot;
        chart.Clear();

        const string seriesName = "Awake";
        const string seriesHex = "#69F0AE";
        double totalSeconds = 0;
        HistoryHoverPoint[] hoverPoints = [];
        double hoverStep = Math.Max(bucketSeconds, 60) / 86_400.0;
        if (buckets.Length > 0)
        {
            double[] xs = new double[buckets.Length];
            double[] ys = new double[buckets.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                xs[i] = buckets[i].LocalStart.ToOADate();
                ys[i] = buckets[i].AwakeSeconds;
                totalSeconds += ys[i];
            }

            double step = xs.Length > 1
                ? Math.Abs(xs[1] - xs[0])
                : Math.Max(bucketSeconds, 60) / 86_400.0;
            bool coarse = bucketSeconds >= 600 && bucketSeconds < 86_400;
            double barSize = coarse ? step * 0.55 : Math.Max(step * 0.62, step * 0.28);
            AddHistoryBars(chart, xs, ys, seriesName, seriesHex, 0, barSize);

            hoverPoints = new HistoryHoverPoint[buckets.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                hoverPoints[i] = new HistoryHoverPoint(
                    xs[i],
                    ys[i],
                    0,
                    buckets[i].LocalStart,
                    seriesName,
                    "");
            }

            hoverStep = step;
        }

        if (total != null)
            total.Text = "Total: " + FormatDuration(totalSeconds);

        chart.Axes.DateTimeTicksBottom();
        ConfigureHistoryBottomTicks(chart, bucketSeconds);

        chart.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = FormatDuration
        };
        StyleHistoryPlot(chart);
        chart.ShowLegend(ScottPlot.Alignment.UpperRight);

        var highlight = chart.Add.HorizontalSpan(0, 0);
        highlight.FillColor = ScottPlot.Color.FromHex("#FFFFFF").WithAlpha(28);
        highlight.LineColor = ScottPlot.Color.FromHex("#F2F2F5").WithAlpha(90);
        highlight.LineWidth = 1;
        highlight.EnableAutoscale = false;
        highlight.IsVisible = false;

        var tip = chart.Add.Annotation("");
        tip.LabelFontColor = ScottPlot.Color.FromHex("#F2F2F5");
        tip.LabelBackgroundColor = ScottPlot.Color.FromHex("#1E1E22");
        tip.LabelBorderColor = ScottPlot.Color.FromHex("#5A5A62");
        tip.LabelBorderWidth = 1;
        tip.LabelShadowColor = ScottPlot.Colors.Transparent;
        tip.LabelFontSize = 16;
        tip.OffsetX = 12;
        tip.OffsetY = 12;
        tip.IsVisible = false;
        plot.Tag = new HistoryHoverState(hoverPoints, tip, highlight, hoverStep, duration: true);
        plot.Refresh();
    }

    private static string FormatDuration(double seconds)
    {
        long total = (long)Math.Round(Math.Max(0, seconds));
        if (total < 60)
            return total + "s";
        long minutes = total / 60;
        long remSeconds = total % 60;
        if (minutes < 60)
            return remSeconds > 0 ? minutes + "m " + remSeconds + "s" : minutes + "m";
        long hours = minutes / 60;
        long remMinutes = minutes % 60;
        if (hours < 48)
            return remMinutes > 0 ? hours + "h " + remMinutes + "m" : hours + "h";
        long days = hours / 24;
        long remHours = hours % 24;
        return remHours > 0 ? days + "d " + remHours + "h" : days + "d";
    }

    private static void ConfigureHistoryBottomTicks(ScottPlot.Plot chart, int bucketSeconds)
    {
        ScottPlot.TickGenerators.DateTimeFixedInterval? ticks = bucketSeconds switch
        {
            // 1 hour → 10-minute bars, label each bar.
            600 => new ScottPlot.TickGenerators.DateTimeFixedInterval(
                new ScottPlot.TickGenerators.TimeUnits.Minute(),
                10,
                new ScottPlot.TickGenerators.TimeUnits.Minute(),
                10),
            // 5 hours → 30-minute bars, label each bar.
            1800 => new ScottPlot.TickGenerators.DateTimeFixedInterval(
                new ScottPlot.TickGenerators.TimeUnits.Minute(),
                30,
                new ScottPlot.TickGenerators.TimeUnits.Minute(),
                30),
            // Day / custom-day → 2-hour bars, label every 2 hours.
            7200 => new ScottPlot.TickGenerators.DateTimeFixedInterval(
                new ScottPlot.TickGenerators.TimeUnits.Hour(),
                2,
                new ScottPlot.TickGenerators.TimeUnits.Hour(),
                2),
            _ => null
        };
        if (ticks == null)
            return;
        ticks.LabelFormatter = dt => dt.ToString("HH:mm", CultureInfo.InvariantCulture);
        chart.Axes.Bottom.TickGenerator = ticks;
    }

    private static void ShowHistoryEmpty(Label? empty, FormsPlot? plot, string text)
    {
        if (plot != null)
        {
            plot.Tag = null;
            plot.Visible = false;
        }

        if (empty == null)
            return;
        empty.Visible = true;
        empty.Text = text;
        empty.BringToFront();
    }

    private static void AddHistoryBars(
        ScottPlot.Plot plot,
        double[] xs,
        double[] ys,
        string name,
        string hex,
        double offset,
        double size)
    {
        var bars = new ScottPlot.Bar[xs.Length];
        var color = ScottPlot.Color.FromHex(hex);
        for (int i = 0; i < xs.Length; i++)
        {
            bars[i] = new ScottPlot.Bar
            {
                Position = xs[i] + offset,
                Value = ys[i],
                FillColor = color,
                LineWidth = 0,
                Size = size
            };
        }

        var barPlot = plot.Add.Bars(bars);
        barPlot.LegendText = name;
    }

    private static void HistoryPlotMouseMove(object? sender, MouseEventArgs e)
    {
        if (sender is not FormsPlot plot || plot.Tag is not HistoryHoverState hover || hover.Points.Length == 0)
            return;

        var coords = plot.Plot.GetCoordinates(e.X, e.Y);
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < hover.Points.Length; i++)
        {
            double dx = Math.Abs(hover.Points[i].X - coords.X);
            if (dx < bestDist)
            {
                bestDist = dx;
                best = i;
            }
        }

        double maxX = plot.Plot.Axes.Bottom.Max;
        double minX = plot.Plot.Axes.Bottom.Min;
        double span = Math.Max(maxX - minX, 1.0 / 86_400.0);
        double hit = Math.Max(
            Math.Max(hover.Step * 0.55, span / Math.Max(hover.Points.Length * 1.5, 1)),
            span * 0.03);
        if (best < 0 || bestDist > hit)
        {
            if (!hover.Visible)
                return;
            ClearHistoryHover(hover);
            plot.Refresh();
            return;
        }

        var point = hover.Points[best];
        string when = point.At.ToString(
            point.At.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture);
        string text = hover.Duration
            ? when + "\n" + point.FirstName + ": " + FormatDuration(point.First)
            : when
                + "\n" + point.FirstName + ": " + GroupLayout.FormatVolume(point.First)
                + "\n" + point.SecondName + ": " + GroupLayout.FormatVolume(point.Second);
        if (hover.Visible && hover.Index == best && hover.Annotation.Text == text)
            return;

        double half = hover.Step * 0.5;
        hover.Highlight.X1 = point.X - half;
        hover.Highlight.X2 = point.X + half;
        hover.Highlight.IsVisible = true;
        hover.Index = best;
        hover.Visible = true;
        hover.Annotation.Text = text;
        hover.Annotation.IsVisible = true;
        plot.Refresh();
    }

    private static void ClearHistoryHover(HistoryHoverState hover)
    {
        hover.Visible = false;
        hover.Index = -1;
        hover.Annotation.IsVisible = false;
        hover.Highlight.IsVisible = false;
    }

    private sealed class HistoryHoverState(
        HistoryHoverPoint[] points,
        ScottPlot.Plottables.Annotation annotation,
        ScottPlot.Plottables.HorizontalSpan highlight,
        double step,
        bool duration)
    {
        public HistoryHoverPoint[] Points { get; } = points;
        public ScottPlot.Plottables.Annotation Annotation { get; } = annotation;
        public ScottPlot.Plottables.HorizontalSpan Highlight { get; } = highlight;
        public double Step { get; } = Math.Max(step, 1.0 / 86_400.0);
        public bool Duration { get; } = duration;
        public bool Visible;
        public int Index = -1;
    }

    private readonly record struct HistoryHoverPoint(
        double X,
        double First,
        double Second,
        DateTime At,
        string FirstName,
        string SecondName);

    private static void StyleHistoryPlot(ScottPlot.Plot plot)
    {
        var bg = ScottPlot.Color.FromHex("#28282C");
        var fg = ScottPlot.Color.FromHex("#C8C8CD");
        var grid = ScottPlot.Color.FromHex("#3A3A40");
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color = bg;
        plot.Axes.Color(fg);
        plot.Axes.Bottom.TickLabelStyle.FontSize = 16;
        plot.Axes.Left.TickLabelStyle.FontSize = 16;
        plot.Axes.Bottom.Label.FontSize = 15;
        plot.Axes.Left.Label.FontSize = 15;
        plot.Grid.MajorLineColor = grid;
        plot.Legend.BackgroundColor = bg;
        plot.Legend.FontColor = fg;
        plot.Legend.OutlineColor = grid;
        plot.Legend.FontSize = 16;
    }

    private decimal CeilingDisplay(double bytesPerSec)
    {
        double shown = RateCeilings.ToDisplay(bytesPerSec, _ceilingUnit);
        decimal value = (decimal)Math.Round(Math.Clamp(shown, 0, 100000), 1, MidpointRounding.AwayFromZero);
        return value;
    }

    private Panel CeilingRow(string name, decimal value, out AntdUI.InputNumber field, out Label unit)
    {
        field = MakeNumber(value, 0, 100000, 120);
        field.DecimalPlaces = 1;
        field.Increment = 1;
        var row = new Panel { Height = MetricRowH, BackColor = BgCard, Margin = Padding.Empty };
        var label = new Label
        {
            Text = name,
            AutoSize = false,
            Font = _bodyFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        unit = new Label
        {
            Text = RateCeilings.UnitLabel(_ceilingUnit),
            AutoSize = false,
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var number = field;
        var unitLabel = unit;
        const int labelW = 150;
        const int gap = 12;
        row.Controls.Add(label);
        row.Controls.Add(number);
        row.Controls.Add(unitLabel);
        row.Resize += (_, _) =>
        {
            label.SetBounds(0, 0, labelW, row.Height);
            int numY = Math.Max(0, (row.Height - number.Height) / 2);
            number.Location = new Point(labelW + gap, numY);
            int unitX = number.Right + gap;
            unitLabel.SetBounds(unitX, 0, Math.Max(48, row.Width - unitX), row.Height);
        };
        return row;
    }

    private void SyncCeilingUnit()
    {
        RateUnit next = _rateUnitSeg?.SelectIndex == 1 ? RateUnit.Bits : RateUnit.Bytes;
        if (next == _ceilingUnit)
        {
            RefreshCeilingUnits();
            return;
        }

        WriteCeilings(_ceilingUnit);
        _ceilingUnit = next;
        PushCeilingBoxes();
    }

    private void WriteCeilings(RateUnit unit)
    {
        _draft.RateCeilings ??= new RateCeilings();
        if (_ceilSend != null)
            _draft.RateCeilings.NetSendBytesPerSec = RateCeilings.FromDisplay((double)_ceilSend.Value, unit);
        if (_ceilRecv != null)
            _draft.RateCeilings.NetRecvBytesPerSec = RateCeilings.FromDisplay((double)_ceilRecv.Value, unit);
        if (_ceilRead != null)
            _draft.RateCeilings.DiskReadBytesPerSec = RateCeilings.FromDisplay((double)_ceilRead.Value, unit);
        if (_ceilWrite != null)
            _draft.RateCeilings.DiskWriteBytesPerSec = RateCeilings.FromDisplay((double)_ceilWrite.Value, unit);
        _draft.RateCeilings.Normalize();
    }

    private void PushCeilingBoxes()
    {
        var ceilings = _draft.RateCeilings ?? new RateCeilings();
        SetCeiling(_ceilSend, ceilings.NetSendBytesPerSec);
        SetCeiling(_ceilRecv, ceilings.NetRecvBytesPerSec);
        SetCeiling(_ceilRead, ceilings.DiskReadBytesPerSec);
        SetCeiling(_ceilWrite, ceilings.DiskWriteBytesPerSec);
        RefreshCeilingUnits();
    }

    private void SetCeiling(AntdUI.InputNumber? box, double bytesPerSec)
    {
        if (box == null)
            return;
        decimal min = box.Minimum ?? 0;
        decimal max = box.Maximum ?? 100000;
        box.Value = Math.Clamp(CeilingDisplay(bytesPerSec), min, max);
    }

    private void RefreshCeilingUnits()
    {
        string text = RateCeilings.UnitLabel(_ceilingUnit);
        if (_ceilSendUnit != null) _ceilSendUnit.Text = text;
        if (_ceilRecvUnit != null) _ceilRecvUnit.Text = text;
        if (_ceilReadUnit != null) _ceilReadUnit.Text = text;
        if (_ceilWriteUnit != null) _ceilWriteUnit.Text = text;
    }

    private Panel LabeledNumberRow(string caption, AntdUI.InputNumber number, out AntdUI.InputNumber field)
    {
        field = number;
        var row = new Panel { Height = 42, BackColor = BgCard, Margin = Padding.Empty };
        var label = new Label
        {
            Text = caption,
            AutoSize = false,
            Bounds = new Rectangle(0, 4, 150, 34),
            Font = _bodyFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        number.Location = new Point(160, 4);
        row.Controls.Add(label);
        row.Controls.Add(number);
        return row;
    }

    private Panel SwitchRow(string text, bool isChecked, out AntdUI.Switch toggle)
    {
        var row = new Panel { Height = 40, BackColor = BgCard, Margin = Padding.Empty };
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Font = _bodyFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        toggle = MakeSwitch(isChecked);
        var sw = toggle;
        row.Controls.Add(label);
        row.Controls.Add(sw);
        row.Resize += (_, _) =>
        {
            sw.Left = Math.Max(0, row.Width - sw.Width - 2);
            sw.Top = Math.Max(0, (row.Height - sw.Height) / 2);
            label.SetBounds(0, 0, Math.Max(20, sw.Left - 12), row.Height);
            sw.BringToFront();
        };
        return row;
    }

    private Panel SegmentRow(string[] items, int selected, out AntdUI.Segmented segmented, int maxWidth = 440)
    {
        var row = new Panel { Height = 52, BackColor = BgCard, Margin = Padding.Empty };
        segmented = new AntdUI.Segmented
        {
            Size = new Size(Math.Clamp(maxWidth, 200, 1100), 36),
            Full = true
        };
        foreach (string item in items)
            segmented.Items.Add(new AntdUI.SegmentedItem { Text = item });
        segmented.SelectIndex = selected;
        var seg = segmented;
        int cap = maxWidth;
        KeepScrollOnInteract(seg);
        seg.SelectIndexChanged += (_, _) =>
        {
            if (_content.VerticalScroll.Value > 0 || _content.HorizontalScroll.Value > 0)
                _content.RememberScroll();
            _content.RestoreScrollSoon();
        };
        row.Controls.Add(seg);
        row.Resize += (_, _) =>
        {
            seg.Width = Math.Min(cap, Math.Max(200, row.Width));
            seg.Height = 36;
            seg.Left = 0;
            seg.Top = Math.Max(0, (row.Height - seg.Height) / 2);
        };
        return row;
    }

    private static AntdUI.InputNumber MakeNumber(decimal value, decimal min, decimal max, int width = 100) => new()
    {
        Size = new Size(width, 34),
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        DecimalPlaces = value % 1m == 0 ? 0 : 1,
        BackColor = Color.FromArgb(48, 48, 52),
        ForeColor = TextPrimary
    };

    private Panel NumberRow(AntdUI.InputNumber number, out AntdUI.InputNumber field)
    {
        field = number;
        var row = new Panel { Height = 42, BackColor = BgCard, Margin = Padding.Empty };
        number.Location = new Point(0, 4);
        row.Controls.Add(number);
        return row;
    }

    private Panel CaptionRow(string left, string middle, string right)
    {
        var row = new Panel { Height = 22, BackColor = BgCard, Margin = Padding.Empty };
        row.Controls.Add(CaptionLabel(left, 0));
        row.Controls.Add(CaptionLabel(middle, 116));
        row.Controls.Add(CaptionLabel(right, 232));
        return row;
    }

    private Label CaptionLabel(string text, int x) => new()
    {
        Text = text,
        AutoSize = false,
        Bounds = new Rectangle(x, 0, 110, 22),
        Font = _hintFont,
        ForeColor = TextMuted,
        BackColor = BgCard
    };

    private static Panel FontRow(
        AntdUI.InputNumber valueBox,
        AntdUI.InputNumber labelBox,
        AntdUI.InputNumber detailBox,
        out AntdUI.InputNumber valueField,
        out AntdUI.InputNumber labelField,
        out AntdUI.InputNumber detailField)
    {
        valueField = valueBox;
        labelField = labelBox;
        detailField = detailBox;
        var row = new Panel { Height = 42, BackColor = BgCard, Margin = Padding.Empty };
        valueBox.Location = new Point(0, 4);
        labelBox.Location = new Point(116, 4);
        detailBox.Location = new Point(232, 4);
        row.Controls.Add(valueBox);
        row.Controls.Add(labelBox);
        row.Controls.Add(detailBox);
        return row;
    }

    private Panel EdgeRow()
    {
        _edgePreset = new AntdUI.Select
        {
            Size = new Size(240, 36),
            List = true,
            BackColor = BgCard,
            ForeColor = TextPrimary
        };
        _edgePreset.Items.Add(new AntdUI.SelectItem("None"));
        _edgePreset.Items.Add(new AntdUI.SelectItem("Left"));
        _edgePreset.Items.Add(new AntdUI.SelectItem("Right"));
        _edgePreset.Items.Add(new AntdUI.SelectItem("Top"));
        _edgePreset.Items.Add(new AntdUI.SelectItem("Bottom"));
        _edgePreset.SelectedIndex = Math.Clamp((int)_draft.EdgePreset, 0, 4);

        var row = new Panel { Height = 48, BackColor = BgCard, Margin = Padding.Empty };
        var select = _edgePreset;
        select.Location = new Point(0, 6);
        row.Controls.Add(select);
        return row;
    }

    private Panel OpacityRow()
    {
        int percent = (int)Math.Round(Math.Clamp(_draft.Opacity, 0.5, 1.0) * 100);
        _opacity = new AntdUI.Slider
        {
            Size = new Size(300, 28),
            MinValue = 50,
            MaxValue = 100,
            Value = percent,
            ShowValue = false
        };
        _opacityValue = new Label
        {
            Text = $"{percent}%",
            AutoSize = false,
            Size = new Size(52, 28),
            Font = _bodyFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var slider = _opacity;
        var caption = _opacityValue;
        slider.ValueChanged += (_, e) => caption.Text = $"{e.Value}%";

        var row = new Panel { Height = 40, BackColor = BgCard, Margin = Padding.Empty };
        row.Controls.Add(slider);
        row.Controls.Add(caption);
        row.Resize += (_, _) =>
        {
            int captionW = 52;
            int sliderW = Math.Min(360, Math.Max(120, row.Width - captionW - 12));
            slider.SetBounds(0, Math.Max(0, (row.Height - slider.Height) / 2), sliderW, slider.Height);
            caption.Location = new Point(slider.Right + 8, Math.Max(0, (row.Height - caption.Height) / 2));
        };
        return row;
    }

    private Panel ColorRow(string name, Color color, out ColorSwatch swatch)
    {
        var row = new Panel { Height = 40, BackColor = BgCard, Margin = Padding.Empty };
        var label = new Label
        {
            Text = name,
            AutoSize = false,
            Font = _bodyFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        swatch = new ColorSwatch(color);
        var chip = swatch;
        row.Controls.Add(label);
        row.Controls.Add(chip);
        row.Resize += (_, _) =>
        {
            chip.Left = Math.Max(0, row.Width - chip.Width - 2);
            chip.Top = Math.Max(0, (row.Height - chip.Height) / 2);
            label.SetBounds(0, 0, Math.Max(20, chip.Left - 12), row.Height);
            chip.BringToFront();
        };
        return row;
    }

    private static Panel ButtonRow(AntdUI.Button button)
    {
        var row = new Panel { Height = 44, BackColor = BgCard, Margin = Padding.Empty };
        button.Location = new Point(0, 5);
        row.Controls.Add(button);
        return row;
    }

    private static AntdUI.Button MakeButton(string text, AntdUI.TTypeMini type) => new()
    {
        Text = text,
        Type = type,
        Size = new Size(96, 34),
        Radius = 6,
        Margin = Padding.Empty
    };

    private bool _fontsDisposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _historyTimer.Stop();
            _historyTimer.Dispose();
        }

        base.Dispose(disposing);
        if (!disposing || _fontsDisposed)
            return;
        _fontsDisposed = true;
        _titleFont.Dispose();
        _headingFont.Dispose();
        _bodyFont.Dispose();
        _hintFont.Dispose();
        _sectionFont.Dispose();
    }

    private sealed class StackPanel : Panel
    {
        private readonly List<Control> _order = new();

        public bool Bordered { get; }
        public int Gap { get; }
        public Padding Insets { get; }

        public StackPanel(bool card)
        {
            Bordered = card;
            BackColor = BgApp;
            Gap = card ? 10 : 14;
            Insets = card ? new Padding(20, 14, 20, 16) : new Padding(24, 12, 24, 24);
            Margin = Padding.Empty;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void Add(Control control)
        {
            _order.Add(control);
            Controls.Add(control);
        }

        public int Reflow(int width)
        {
            Width = Math.Max(1, width);
            int y = Insets.Top;
            int inner = Math.Max(40, Width - Insets.Left - Insets.Right);
            bool any = false;
            foreach (Control child in _order)
            {
                if (!child.Visible)
                    continue;
                any = true;
                child.Left = Insets.Left;
                child.Top = y;
                int height;
                if (child is StackPanel nested)
                    height = nested.Reflow(inner);
                else
                {
                    child.Width = inner;
                    height = child.Height;
                }

                y += height + Gap;
            }

            if (any)
                y -= Gap;
            Height = Math.Max(1, y + Insets.Bottom);
            if (Bordered)
                Invalidate();
            return Height;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!Bordered || Width < 2 || Height < 2)
            {
                base.OnPaintBackground(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BgApp);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using var path = Round(rect, 12f);
            using var brush = new SolidBrush(BgCard);
            e.Graphics.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(72, 72, 78));
            e.Graphics.DrawPath(pen, path);
        }

        private static GraphicsPath Round(RectangleF rect, float radius)
        {
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            if (d <= 1f)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Scroll panel that does not jump when a child control takes focus.</summary>
    internal sealed class SettingsScrollPanel : Panel
    {
        private int _savedX;
        private int _savedY;
        private int _restoreGeneration;
        private bool _restoring;
        private int _suppressRemember;

        public SettingsScrollPanel()
        {
            AutoScroll = true;
            DoubleBuffered = true;
        }

        protected override Point ScrollToControl(Control activeControl)
            => DisplayRectangle.Location;

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            // Visibility/layout changes also fire OnScroll after WinForms clamps to 0.
            // Capturing that would overwrite the real offset and RestoreScrollSoon would jump to top.
            if (!_restoring && _suppressRemember == 0)
                RememberScroll();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_suppressRemember == 0)
                RememberScroll();
        }

        public void RememberScroll()
        {
            _savedX = HorizontalScroll.Value;
            _savedY = VerticalScroll.Value;
        }

        public void SetScrollForTests(int x, int y)
        {
            // Same positive-offset convention the form already uses successfully for scrolling.
            AutoScrollPosition = new Point(Math.Max(0, x), Math.Max(0, y));
            RememberScroll();
        }

        public void BeginScrollPreserve(bool remember = true)
        {
            if (remember)
            {
                // Trust a live non-zero offset. If layout already clamped to 0, keep the last user offset.
                if (VerticalScroll.Value > 0 || HorizontalScroll.Value > 0)
                    RememberScroll();
            }

            _suppressRemember++;
        }

        public void EndScrollPreserve(int holdMs = 450)
        {
            if (_suppressRemember > 0)
                _suppressRemember--;
            RestoreScrollSoon(holdMs);
        }

        public void RestoreScrollSoon(int holdMs = 450)
        {
            int x = _savedX;
            int y = _savedY;
            ApplyScroll(x, y);
            if (!IsHandleCreated)
                return;
            int generation = ++_restoreGeneration;
            BeginInvoke(() =>
            {
                if (generation != _restoreGeneration || IsDisposed)
                    return;
                ApplyScroll(x, y);
            });

            // Chart host show/hide and ScottPlot refresh can keep shifting scroll for a few frames.
            int hold = Math.Clamp(holdMs, 50, 2000);
            var timer = new System.Windows.Forms.Timer { Interval = 16 };
            long until = Environment.TickCount64 + hold;
            timer.Tick += (_, _) =>
            {
                if (generation != _restoreGeneration || IsDisposed || Environment.TickCount64 >= until)
                {
                    timer.Stop();
                    timer.Dispose();
                    return;
                }

                ApplyScroll(x, y);
            };
            timer.Start();
        }

        private void ApplyScroll(int x, int y)
        {
            if (!IsHandleCreated)
                return;
            _restoring = true;
            try
            {
                // AutoScrollPosition setter wants positive offsets; getter returns negatives.
                AutoScrollPosition = new Point(Math.Max(0, x), Math.Max(0, y));
            }
            finally
            {
                _restoring = false;
            }
        }
    }

    private sealed class ColorSwatch : Panel
    {
        private readonly Label _hex;

        public ColorSwatch(Color color)
        {
            Size = new Size(96, 28);
            Cursor = Cursors.Hand;
            Margin = Padding.Empty;
            _hex = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
                Cursor = Cursors.Hand
            };
            Controls.Add(_hex);
            SetColor(color);
            Click += (_, _) => Pick();
            _hex.Click += (_, _) => Pick();
        }

        public void SetColor(Color color)
        {
            BackColor = color;
            _hex.BackColor = color;
            _hex.ForeColor = MetricColors.ContrastText(color);
            _hex.Text = MetricPalette.ToHex(color);
        }

        private void Pick()
        {
            var owner = FindForm();
            if (owner == null)
                return;

            using var dialog = new ColorDialog
            {
                Color = BackColor,
                FullOpen = true,
                AnyColor = true
            };
            if (dialog.ShowDialog(owner) == DialogResult.OK)
                SetColor(dialog.Color);
        }
    }
}
