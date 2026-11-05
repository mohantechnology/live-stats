using System.Drawing.Drawing2D;
using System.Reflection;
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

    private readonly AntdUI.PageHeader _header;
    private readonly Panel _side;
    private readonly AntdUI.Menu _nav;
    private readonly Panel _content;
    private readonly Panel _footer;
    private readonly AntdUI.Button _btnApply;
    private readonly AntdUI.Button _btnOk;
    private readonly AntdUI.Button _btnCancel;

    private readonly StackPanel _generalPage;
    private readonly StackPanel _monitoringPage;
    private readonly StackPanel _appearancePage;
    private readonly StackPanel _aboutPage;
    private readonly Dictionary<MetricKind, ColorSwatch> _swatches = new();

    private AntdUI.Switch? _startWithWindows;
    private AntdUI.Switch? _hotkeyEnabled;
    private AntdUI.InputNumber? _intervalMs;
    private AntdUI.Switch? _cpu, _memory, _gpu, _vram, _netSend, _netRecv, _diskRead, _diskWrite, _npu, _npuMemory;
    private AntdUI.Switch? _cpuHide, _memoryHide, _gpuHide, _vramHide, _netSendHide, _netRecvHide, _diskReadHide, _diskWriteHide, _npuHide, _npuMemoryHide;
    private AntdUI.Switch? _showDetails;
    private AntdUI.InputNumber? _detailCount;
    private AntdUI.Segmented? _orientSeg;
    private AntdUI.Segmented? _placeSeg;
    private AntdUI.Segmented? _rateUnitSeg;
    private AntdUI.Segmented? _densitySeg;
    private AntdUI.Select? _edgePreset;
    private AntdUI.Slider? _opacity;
    private Label? _opacityValue;
    private AntdUI.Switch? _alwaysOnTop;
    private AntdUI.InputNumber? _defaultValuePt, _defaultLabelPt, _compactValuePt, _compactLabelPt;

    private bool _chromeReady;
    private bool _inChrome;
    private string _section = "general";

    public SettingsForm(AppSettings current, Action<AppSettings> apply)
    {
        _draft = current.Clone();
        _apply = apply;

        AntdUI.Config.IsDark = true;

        Text = "Taskbar Live Stats — Settings";
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
            ShowIcon = false,
            ShowButton = true,
            MinimizeBox = true,
            MaximizeBox = true,
            FullBox = true,
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
        _nav.Items.Add(new AntdUI.MenuItem { Text = "Appearance", ID = "appearance" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "About", ID = "about" });
        _nav.SelectChanged += (_, e) => ShowSection(e.Value?.ID ?? "general");

        _side = new Panel { BackColor = BgSide, Margin = Padding.Empty };
        _side.Controls.Add(_nav);

        _content = new Panel
        {
            BackColor = BgApp,
            AutoScroll = true,
            Margin = Padding.Empty
        };

        _generalPage = BuildGeneral();
        _monitoringPage = BuildMonitoring();
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
            LayoutActiveSection();
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

    private void ShowSection(string id)
    {
        _section = id;
        foreach (var page in Pages())
        {
            bool on = page == ActivePage();
            page.Visible = on;
            if (!on)
                page.Size = new Size(0, 0);
        }

        LayoutActiveSection();
        if (_content.IsHandleCreated)
        {
            _content.AutoScrollPosition = Point.Empty;
            ActivePage().Location = Point.Empty;
        }
    }

    private void LayoutActiveSection()
    {
        if (_content.ClientSize.Width < 40)
            return;

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

    private StackPanel ActivePage() => _section switch
    {
        "monitoring" => _monitoringPage,
        "appearance" => _appearancePage,
        "about" => _aboutPage,
        _ => _generalPage
    };

    private IEnumerable<StackPanel> Pages()
    {
        yield return _generalPage;
        yield return _monitoringPage;
        yield return _appearancePage;
        yield return _aboutPage;
    }

    private StackPanel BuildGeneral()
    {
        var page = NewPage();
        page.Add(PageTitle("General"));

        var startup = NewCard();
        startup.Add(Heading("Startup"));
        startup.Add(SwitchRow("Start with Windows", _draft.StartWithWindows, out _startWithWindows));
        page.Add(startup);

        var updates = NewCard();
        updates.Add(Heading("Update frequency"));
        updates.Add(Hint("How often the numbers refresh, in milliseconds (100 – 10000).", 36));
        updates.Add(NumberRow(MakeNumber(_draft.UpdateIntervalMs, 100, 10000, 140), out _intervalMs));
        page.Add(updates);

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
        var metrics = NewCard();
        metrics.Add(Heading("Processor and memory"));
        metrics.Add(Hint("Hide at 0 shows that metric only while it is in use. It returns as soon as the value is above zero, and leaves again after 10 seconds at zero.", 52));
        metrics.Add(MetricRow("CPU usage", _draft.Metrics.Cpu, out _cpu, idle.Cpu, out _cpuHide));
        metrics.Add(MetricRow("RAM usage", _draft.Metrics.Memory, out _memory, idle.Memory, out _memoryHide));
        metrics.Add(Heading("Graphics"));
        metrics.Add(MetricRow("GPU usage", _draft.Metrics.Gpu, out _gpu, idle.Gpu, out _gpuHide));
        metrics.Add(MetricRow("VRAM usage", _draft.Metrics.Vram, out _vram, idle.Vram, out _vramHide));
        metrics.Add(MetricRow("NPU usage", _draft.Metrics.Npu, out _npu, idle.Npu, out _npuHide));
        metrics.Add(MetricRow("NPU memory", _draft.Metrics.NpuMemory, out _npuMemory, idle.NpuMemory, out _npuMemoryHide));
        metrics.Add(Heading("Network"));
        metrics.Add(MetricRow("Upload speed", _draft.Metrics.NetSend, out _netSend, idle.NetSend, out _netSendHide));
        metrics.Add(MetricRow("Download speed", _draft.Metrics.NetRecv, out _netRecv, idle.NetRecv, out _netRecvHide));
        metrics.Add(Heading("Disk"));
        metrics.Add(MetricRow("Disk read", _draft.Metrics.DiskRead, out _diskRead, idle.DiskRead, out _diskReadHide));
        metrics.Add(MetricRow("Disk write", _draft.Metrics.DiskWrite, out _diskWrite, idle.DiskWrite, out _diskWriteHide));
        page.Add(metrics);

        var details = NewCard();
        details.Add(Heading("Busiest items"));
        details.Add(SwitchRow("Show busiest items", _draft.ShowUsageDetails, out _showDetails));
        details.Add(Hint("Sits beside the CPU and GPU labels, two per row. The name stays on the left and the percent on the right, so the bar does not shift. Performance cores are marked P and efficiency cores E when Windows reports them. Left-click the bar to show or hide them.", 84));
        details.Add(Heading("How many"));
        details.Add(NumberRow(MakeNumber(_draft.UsageDetailCount, 1, 8, 140), out _detailCount));
        page.Add(details);

        var units = NewCard();
        units.Add(Heading("Transfer units"));
        units.Add(Hint("Used for network and disk speeds.", 24));
        units.Add(SegmentRow(["Bytes/s", "Bits/s"], _draft.RateUnit == RateUnit.Bits ? 1 : 0, out _rateUnitSeg));
        page.Add(units);
        return page;
    }

    private StackPanel BuildAppearance()
    {
        var page = NewPage();
        page.Add(PageTitle("Appearance"));

        var size = NewCard();
        size.Add(Heading("Size"));
        size.Add(SegmentRow(["Default", "Compact"], _draft.Density == OverlayDensity.Compact ? 1 : 0, out _densitySeg));
        size.Add(Hint("Compact hides the icons. Color still marks each metric, so upload and disk write stay distinct.", 40));
        size.Add(Heading("Font sizes (Default)"));
        size.Add(CaptionRow("Value", "Label (pt)"));
        size.Add(FontRow(
            MakeNumber((decimal)_draft.DefaultValueFontPt, 7, 28),
            MakeNumber((decimal)_draft.DefaultLabelFontPt, 6, 18),
            out _defaultValuePt,
            out _defaultLabelPt));
        size.Add(Heading("Font sizes (Compact)"));
        size.Add(CaptionRow("Value", "Label (pt)"));
        size.Add(FontRow(
            MakeNumber((decimal)_draft.CompactValueFontPt, 7, 28),
            MakeNumber((decimal)_draft.CompactLabelFontPt, 6, 18),
            out _compactValuePt,
            out _compactLabelPt));
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

        var colors = NewCard();
        colors.Add(Heading("Metric colors"));
        colors.Add(Hint("Each metric uses this color for its value and the bar beside it. Click a swatch to change it.", 40));
        foreach (var (kind, name) in MetricColors.All)
        {
            var row = ColorRow(name, MetricColors.Resolve(_draft.Colors, kind), out var swatch);
            _swatches[kind] = swatch;
            colors.Add(row);
        }

        var resetColors = MakeButton("Reset colors", AntdUI.TTypeMini.Default);
        resetColors.Size = new Size(140, 34);
        resetColors.Click += (_, _) =>
        {
            var defaults = new MetricPalette();
            foreach (var (kind, swatch) in _swatches)
                swatch.SetColor(MetricColors.Resolve(defaults, kind));
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
        card.Add(new Label
        {
            Text = "Taskbar Live Stats",
            AutoSize = false,
            Height = 32,
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        });
        card.Add(new Label
        {
            Text = $"Version {ver}",
            AutoSize = false,
            Height = 24,
            Font = _bodyFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        });
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

        if (_showDetails != null) _draft.ShowUsageDetails = _showDetails.Checked;
        if (_detailCount != null)
            _draft.UsageDetailCount = (int)Math.Clamp(_detailCount.Value, 1, 8);

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
        if (_compactValuePt != null) _draft.CompactValueFontPt = (float)_compactValuePt.Value;
        if (_compactLabelPt != null) _draft.CompactLabelFontPt = (float)_compactLabelPt.Value;

        if (_edgePreset != null)
            _draft.EdgePreset = (EdgePreset)Math.Clamp(_edgePreset.SelectedIndex, 0, 4);

        if (_opacity != null)
            _draft.Opacity = _opacity.Value / 100.0;

        if (_alwaysOnTop != null)
            _draft.AlwaysOnTop = _alwaysOnTop.Checked;

        _draft.Colors ??= new MetricPalette();
        foreach (var (kind, swatch) in _swatches)
            MetricColors.Assign(_draft.Colors, kind, swatch.BackColor);
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

    private Panel MetricRow(string name, bool shown, out AntdUI.Switch showSwitch, bool hideIdle, out AntdUI.Switch hideSwitch)
    {
        var row = new Panel { Height = 44, BackColor = BgCard, Margin = Padding.Empty };
        var label = new Label
        {
            Text = name,
            AutoSize = false,
            Font = _bodyFont,
            ForeColor = TextPrimary,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var idleLabel = new Label
        {
            Text = "Hide at 0",
            AutoSize = false,
            Size = new Size(72, 28),
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard,
            TextAlign = ContentAlignment.MiddleRight
        };
        hideSwitch = new AntdUI.Switch { Size = new Size(44, 24), Checked = hideIdle };
        showSwitch = new AntdUI.Switch { Size = new Size(52, 28), Checked = shown };
        var show = showSwitch;
        var hide = hideSwitch;
        var idleCaption = idleLabel;
        row.Controls.Add(label);
        row.Controls.Add(idleCaption);
        row.Controls.Add(hide);
        row.Controls.Add(show);
        row.Resize += (_, _) =>
        {
            show.Left = Math.Max(0, row.Width - show.Width - 2);
            show.Top = Math.Max(0, (row.Height - show.Height) / 2);
            hide.Left = Math.Max(0, show.Left - hide.Width - 14);
            hide.Top = Math.Max(0, (row.Height - hide.Height) / 2);
            idleCaption.Left = Math.Max(0, hide.Left - idleCaption.Width - 6);
            idleCaption.Top = Math.Max(0, (row.Height - idleCaption.Height) / 2);
            label.SetBounds(0, 0, Math.Max(20, idleCaption.Left - 8), row.Height);
            show.BringToFront();
            hide.BringToFront();
        };
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
        toggle = new AntdUI.Switch
        {
            Size = new Size(52, 28),
            Checked = isChecked
        };
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
            Size = new Size(Math.Min(maxWidth, 440), 36),
            Full = true
        };
        foreach (string item in items)
            segmented.Items.Add(new AntdUI.SegmentedItem { Text = item });
        segmented.SelectIndex = selected;
        var seg = segmented;
        int cap = maxWidth;
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

    private Panel CaptionRow(string left, string right)
    {
        var row = new Panel { Height = 22, BackColor = BgCard, Margin = Padding.Empty };
        row.Controls.Add(new Label
        {
            Text = left,
            AutoSize = false,
            Bounds = new Rectangle(0, 0, 100, 22),
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard
        });
        row.Controls.Add(new Label
        {
            Text = right,
            AutoSize = false,
            Bounds = new Rectangle(116, 0, 120, 22),
            Font = _hintFont,
            ForeColor = TextMuted,
            BackColor = BgCard
        });
        return row;
    }

    private static Panel FontRow(
        AntdUI.InputNumber valueBox,
        AntdUI.InputNumber labelBox,
        out AntdUI.InputNumber valueField,
        out AntdUI.InputNumber labelField)
    {
        valueField = valueBox;
        labelField = labelBox;
        var row = new Panel { Height = 42, BackColor = BgCard, Margin = Padding.Empty };
        valueBox.Location = new Point(0, 4);
        labelBox.Location = new Point(116, 4);
        row.Controls.Add(valueBox);
        row.Controls.Add(labelBox);
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
        base.Dispose(disposing);
        if (!disposing || _fontsDisposed)
            return;
        _fontsDisposed = true;
        _titleFont.Dispose();
        _headingFont.Dispose();
        _bodyFont.Dispose();
        _hintFont.Dispose();
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
