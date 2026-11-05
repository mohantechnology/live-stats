using System.Reflection;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

/// <summary>
/// Professional settings UI built with AntdUI (Ant Design for WinForms).
/// </summary>
internal sealed class SettingsForm : AntdUI.Window
{
    private static readonly Color BgApp = Color.FromArgb(22, 22, 24);
    private static readonly Color BgSide = Color.FromArgb(30, 30, 32);
    private static readonly Color BgCard = Color.FromArgb(40, 40, 44);
    private static readonly Color BgFooter = Color.FromArgb(28, 28, 30);
    private static readonly Color TextPrimary = Color.FromArgb(245, 245, 245);
    private static readonly Color TextMuted = Color.FromArgb(170, 170, 175);

    private readonly AppSettings _draft;
    private readonly Action<AppSettings> _apply;

    private readonly AntdUI.Menu _nav;
    private readonly Panel _contentHost;
    private readonly Panel _generalPanel;
    private readonly Panel _monitoringPanel;
    private readonly Panel _appearancePanel;
    private readonly Panel _aboutPanel;

    private AntdUI.Switch? _startWithWindows;
    private AntdUI.Switch? _hotkeyEnabled;
    private AntdUI.InputNumber? _intervalMs;
    private AntdUI.Switch? _cpu, _memory, _gpu, _vram, _netSend, _netRecv, _diskRead, _diskWrite;
    private AntdUI.Segmented? _orientSeg;
    private AntdUI.Segmented? _placeSeg;
    private AntdUI.Segmented? _rateUnitSeg;
    private AntdUI.Segmented? _densitySeg;
    private AntdUI.Select? _edgePreset;
    private AntdUI.Slider? _opacity;
    private AntdUI.Label? _opacityValue;
    private AntdUI.Switch? _alwaysOnTop;
    private AntdUI.InputNumber? _defaultValuePt, _defaultLabelPt, _compactValuePt, _compactLabelPt;

    public SettingsForm(AppSettings current, Action<AppSettings> apply)
    {
        _draft = current.Clone();
        _apply = apply;

        AntdUI.Config.IsDark = true;

        Text = "Taskbar Live Stats — Settings";
        Resizable = true;
        MinimizeBox = true;
        MaximizeBox = true;
        // Not TopMost — live stats overlay stays above this window.
        TopMost = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 640);
        MinimumSize = new Size(780, 540);
        BackColor = BgApp;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.75f);

        var header = new AntdUI.PageHeader
        {
            Dock = DockStyle.Top,
            Height = 48,
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

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0),
            BackColor = BgApp
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        _nav = new AntdUI.Menu
        {
            Dock = DockStyle.Fill,
            BackColor = BgSide,
            ForeColor = TextPrimary,
            Radius = 8,
            Unique = true
        };
        _nav.Items.Add(new AntdUI.MenuItem { Text = "General", ID = "general" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "Monitoring", ID = "monitoring" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "Appearance", ID = "appearance" });
        _nav.Items.Add(new AntdUI.MenuItem { Text = "About", ID = "about" });
        _nav.SelectIndex(0);
        _nav.SelectChanged += (_, e) => ShowSection(e.Value?.ID ?? "general");

        _contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            BackColor = BgApp,
            AutoScroll = true
        };

        _generalPanel = BuildGeneral();
        _monitoringPanel = BuildMonitoring();
        _appearancePanel = BuildAppearance();
        _aboutPanel = BuildAbout();

        foreach (var p in new[] { _generalPanel, _monitoringPanel, _appearancePanel, _aboutPanel })
        {
            p.Visible = false;
            p.BackColor = BgApp;
            _contentHost.Controls.Add(p);
        }

        _contentHost.Resize += (_, _) => LayoutActiveSection();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 14, 20, 14),
            BackColor = BgFooter
        };

        var btnCancel = MakeButton("Cancel", AntdUI.TTypeMini.Default);
        btnCancel.Click += (_, _) => Close();

        var btnOk = MakeButton("OK", AntdUI.TTypeMini.Primary);
        btnOk.Click += (_, _) =>
        {
            PullFromUi();
            _apply(_draft.Clone());
            Close();
        };

        var btnApply = MakeButton("Apply", AntdUI.TTypeMini.Primary);
        btnApply.Ghost = true;
        btnApply.Click += (_, _) =>
        {
            PullFromUi();
            _apply(_draft.Clone());
        };

        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnApply);

        var side = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgSide,
            Padding = new Padding(12, 16, 12, 16)
        };
        side.Controls.Add(_nav);

        root.Controls.Add(side, 0, 0);
        root.SetRowSpan(side, 2);
        root.Controls.Add(_contentHost, 1, 0);
        root.Controls.Add(buttons, 1, 1);

        Controls.Add(root);
        Controls.Add(header);

        ShowSection("general");
        SyncFloatingControls();
        SyncFontEditors();

        Shown += (_, _) =>
        {
            Activate();
            BringToFront();
            LayoutActiveSection();
        };
    }

    private void ShowSection(string id)
    {
        _generalPanel.Visible = id == "general";
        _monitoringPanel.Visible = id == "monitoring";
        _appearancePanel.Visible = id == "appearance";
        _aboutPanel.Visible = id == "about";
        LayoutActiveSection();
    }

    private void LayoutActiveSection()
    {
        Panel? active =
            _generalPanel.Visible ? _generalPanel :
            _monitoringPanel.Visible ? _monitoringPanel :
            _appearancePanel.Visible ? _appearancePanel :
            _aboutPanel.Visible ? _aboutPanel : null;

        if (active is null) return;

        int viewW = Math.Max(360, _contentHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        active.SuspendLayout();
        active.Location = new Point(0, 0);
        active.Width = viewW;

        foreach (Control c in active.Controls)
        {
            if (c is AntdUI.Panel card)
            {
                card.Left = 24;
                card.Width = Math.Max(300, viewW - 48);
                int bottom = 20;
                foreach (Control child in card.Controls)
                    bottom = Math.Max(bottom, child.Bottom);
                card.Height = bottom + 24;
            }
        }

        int sectionBottom = 20;
        foreach (Control c in active.Controls)
            sectionBottom = Math.Max(sectionBottom, c.Bottom);
        active.Height = sectionBottom + 32;
        active.ResumeLayout();

        _contentHost.AutoScrollMinSize = new Size(0, active.Height + 8);
        _contentHost.AutoScrollPosition = Point.Empty;
    }

    private Panel BuildGeneral()
    {
        var p = new Panel();
        p.Controls.Add(SectionTitle("General", 24, 16));

        var card = MakeCard(24, 72, 600, 260);
        int y = 20;
        card.Controls.Add(CardHeading("Startup", 20, y));
        y += 34;
        _startWithWindows = MakeSwitchRow(card, "Start with Windows", 20, y, _draft.StartWithWindows);
        y += 52;

        card.Controls.Add(CardHeading("Update frequency", 20, y));
        y += 34;
        card.Controls.Add(MutedLabel("Sample interval (ms), 100 – 10000", 20, y));
        y += 28;
        _intervalMs = MakeNumber(card, 20, y, 140, _draft.UpdateIntervalMs, 100, 10000);
        y += 52;

        card.Controls.Add(CardHeading("Show / hide shortcut", 20, y));
        y += 34;
        _hotkeyEnabled = MakeSwitchRow(card, "Enable hotkey", 20, y, _draft.HotkeyEnabled);
        y += 44;
        card.Controls.Add(MutedLabel(
            "Default: " + HotkeyHook.Format(_draft.HotkeyModifiers, _draft.HotkeyVirtualKey),
            20, y));

        p.Controls.Add(card);
        return p;
    }

    private Panel BuildMonitoring()
    {
        var p = new Panel();
        p.Controls.Add(SectionTitle("Monitoring", 24, 16));

        var card = MakeCard(24, 72, 600, 620);
        int y = 20;
        y = AddGroupHeading(card, "Processor & Memory", y);
        _cpu = MakeSwitchRow(card, "CPU Usage", 20, y, _draft.Metrics.Cpu); y += 42;
        _memory = MakeSwitchRow(card, "RAM Usage", 20, y, _draft.Metrics.Memory); y += 50;

        y = AddGroupHeading(card, "Graphics", y);
        _gpu = MakeSwitchRow(card, "GPU Usage", 20, y, _draft.Metrics.Gpu); y += 42;
        _vram = MakeSwitchRow(card, "VRAM Usage", 20, y, _draft.Metrics.Vram); y += 50;

        y = AddGroupHeading(card, "Network", y);
        _netSend = MakeSwitchRow(card, "Upload Speed", 20, y, _draft.Metrics.NetSend); y += 42;
        _netRecv = MakeSwitchRow(card, "Download Speed", 20, y, _draft.Metrics.NetRecv); y += 50;

        y = AddGroupHeading(card, "Disk", y);
        _diskRead = MakeSwitchRow(card, "Disk Read", 20, y, _draft.Metrics.DiskRead); y += 42;
        _diskWrite = MakeSwitchRow(card, "Disk Write", 20, y, _draft.Metrics.DiskWrite); y += 50;

        y = AddGroupHeading(card, "Transfer units", y);
        card.Controls.Add(MutedLabel("Network and disk rates", 20, y));
        y += 28;
        _rateUnitSeg = MakeSegmented(card, 20, y, 300, ["Bytes/s", "Bits/s"],
            _draft.RateUnit == RateUnit.Bits ? 1 : 0);

        p.Controls.Add(card);
        return p;
    }

    private Panel BuildAppearance()
    {
        var p = new Panel();
        p.Controls.Add(SectionTitle("Appearance", 24, 16));

        var card = MakeCard(24, 72, 600, 720);
        int y = 20;

        card.Controls.Add(CardHeading("Size", 20, y));
        y += 32;
        _densitySeg = MakeSegmented(card, 20, y, 300, ["Default", "Compact"],
            _draft.Density == OverlayDensity.Compact ? 1 : 0);
        _densitySeg.SelectIndexChanged += (_, _) => SyncFontEditors();
        y += 28;
        card.Controls.Add(MutedLabel("Compact hides icons for a denser bar.", 20, y));
        y += 40;

        card.Controls.Add(CardHeading("Font sizes (Default)", 20, y));
        y += 32;
        card.Controls.Add(MutedLabel("Value  /  Label (pt)", 20, y));
        y += 26;
        _defaultValuePt = MakeNumber(card, 20, y, 90, (decimal)_draft.DefaultValueFontPt, 7, 28);
        _defaultLabelPt = MakeNumber(card, 130, y, 90, (decimal)_draft.DefaultLabelFontPt, 6, 18);
        y += 48;

        card.Controls.Add(CardHeading("Font sizes (Compact)", 20, y));
        y += 32;
        card.Controls.Add(MutedLabel("Value  /  Label (pt)", 20, y));
        y += 26;
        _compactValuePt = MakeNumber(card, 20, y, 90, (decimal)_draft.CompactValueFontPt, 7, 28);
        _compactLabelPt = MakeNumber(card, 130, y, 90, (decimal)_draft.CompactLabelFontPt, 6, 18);
        y += 48;

        card.Controls.Add(CardHeading("Orientation", 20, y));
        y += 32;
        _orientSeg = MakeSegmented(card, 20, y, 300, ["Horizontal", "Vertical"],
            _draft.Orientation == OverlayOrientation.Vertical ? 1 : 0);
        y += 52;

        card.Controls.Add(CardHeading("Placement", 20, y));
        y += 32;
        _placeSeg = MakeSegmented(card, 20, y, 300, ["Taskbar", "Floating"],
            _draft.PlacementMode == PlacementMode.Floating ? 1 : 0);
        _placeSeg.SelectIndexChanged += (_, _) => SyncFloatingControls();
        y += 52;

        card.Controls.Add(CardHeading("Edge position (floating)", 20, y));
        y += 32;
        _edgePreset = new AntdUI.Select
        {
            Location = new Point(20, y),
            Size = new Size(220, 36),
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
        card.Controls.Add(_edgePreset);
        y += 52;

        card.Controls.Add(CardHeading("Opacity", 20, y));
        y += 32;
        _opacity = new AntdUI.Slider
        {
            Location = new Point(20, y),
            Size = new Size(280, 28),
            MinValue = 50,
            MaxValue = 100,
            Value = (int)Math.Round(Math.Clamp(_draft.Opacity, 0.5, 1.0) * 100),
            ShowValue = false
        };
        _opacityValue = new AntdUI.Label
        {
            Text = $"{_opacity.Value}%",
            Location = new Point(314, y + 4),
            AutoSizeMode = AntdUI.TAutoSize.Auto,
            ForeColor = TextMuted
        };
        _opacity.ValueChanged += (_, e) =>
        {
            if (_opacityValue != null)
                _opacityValue.Text = $"{e.Value}%";
        };
        card.Controls.Add(_opacity);
        card.Controls.Add(_opacityValue);
        y += 48;

        _alwaysOnTop = MakeSwitchRow(card, "Always on top (floating)", 20, y, _draft.AlwaysOnTop);
        y += 52;

        var reset = MakeButton("Reset position", AntdUI.TTypeMini.Default);
        reset.Location = new Point(20, y);
        reset.Size = new Size(140, 34);
        reset.Click += (_, _) =>
        {
            if (_edgePreset != null) _edgePreset.SelectedIndex = 0;
            PullFromUi();
            _draft.FloatingX = int.MinValue;
            _draft.FloatingY = int.MinValue;
            _draft.TaskbarOffsetX = 0;
            _draft.EdgePreset = EdgePreset.None;
            _apply(_draft.Clone());
        };
        card.Controls.Add(reset);

        p.Controls.Add(card);
        return p;
    }

    private Panel BuildAbout()
    {
        var p = new Panel();
        p.Controls.Add(SectionTitle("About", 24, 16));
        string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var card = MakeCard(24, 72, 600, 160);
        card.Controls.Add(new AntdUI.Label
        {
            Text = "Taskbar Live Stats",
            Font = new Font("Segoe UI Semibold", 14f),
            Location = new Point(24, 28),
            AutoSizeMode = AntdUI.TAutoSize.Auto,
            ForeColor = TextPrimary
        });
        card.Controls.Add(new AntdUI.Label
        {
            Text = $"Version {ver}",
            Location = new Point(24, 64),
            AutoSizeMode = AntdUI.TAutoSize.Auto,
            ForeColor = TextMuted
        });
        card.Controls.Add(new AntdUI.Label
        {
            Text = "Live CPU, memory, GPU, network, and disk stats on your desktop.",
            Location = new Point(24, 96),
            AutoSizeMode = AntdUI.TAutoSize.Auto,
            ForeColor = TextMuted
        });
        p.Controls.Add(card);
        return p;
    }

    private void SyncFloatingControls()
    {
        bool floating = _placeSeg?.SelectIndex == 1;
        if (_edgePreset != null) _edgePreset.Enabled = floating;
        if (_opacity != null) _opacity.Enabled = floating;
        if (_alwaysOnTop != null) _alwaysOnTop.Enabled = floating;
    }

    private void SyncFontEditors()
    {
        // Both sets are always editable; active density only chooses which pair the overlay uses.
        bool _ = _densitySeg?.SelectIndex == 1;
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
    }

    private static AntdUI.Label SectionTitle(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 22f),
        ForeColor = TextPrimary,
        Location = new Point(x, y),
        AutoSizeMode = AntdUI.TAutoSize.Auto
    };

    private static AntdUI.Label CardHeading(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 11f),
        ForeColor = TextPrimary,
        Location = new Point(x, y),
        AutoSizeMode = AntdUI.TAutoSize.Auto
    };

    private static AntdUI.Label MutedLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSizeMode = AntdUI.TAutoSize.Auto,
        ForeColor = TextMuted
    };

    private static AntdUI.Panel MakeCard(int x, int y, int w, int h) => new()
    {
        Location = new Point(x, y),
        Size = new Size(w, h),
        Radius = 12,
        Shadow = 8,
        ShadowOpacity = 0.22f,
        Back = BgCard,
        BorderWidth = 1,
        BorderColor = Color.FromArgb(55, 55, 60),
        InnerPadding = new Padding(12)
    };

    private static int AddGroupHeading(Control parent, string title, int y)
    {
        parent.Controls.Add(CardHeading(title, 20, y));
        return y + 34;
    }

    private static AntdUI.Switch MakeSwitchRow(Control parent, string text, int x, int y, bool isChecked)
    {
        parent.Controls.Add(new AntdUI.Label
        {
            Text = text,
            Location = new Point(x, y + 6),
            AutoSizeMode = AntdUI.TAutoSize.Auto,
            ForeColor = TextPrimary
        });

        var sw = new AntdUI.Switch
        {
            Location = new Point(x + 320, y),
            Size = new Size(52, 28),
            Checked = isChecked
        };
        parent.Controls.Add(sw);
        return sw;
    }

    private static AntdUI.Segmented MakeSegmented(Control parent, int x, int y, int width, string[] items, int selected)
    {
        var seg = new AntdUI.Segmented
        {
            Location = new Point(x, y),
            Size = new Size(width, 36),
            Full = true
        };
        foreach (string item in items)
            seg.Items.Add(new AntdUI.SegmentedItem { Text = item });
        seg.SelectIndex = selected;
        parent.Controls.Add(seg);
        return seg;
    }

    private static AntdUI.InputNumber MakeNumber(Control parent, int x, int y, int width, decimal value, decimal min, decimal max)
    {
        var num = new AntdUI.InputNumber
        {
            Location = new Point(x, y),
            Size = new Size(width, 34),
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            DecimalPlaces = value % 1m == 0 ? 0 : 1,
            BackColor = Color.FromArgb(48, 48, 52),
            ForeColor = TextPrimary
        };
        parent.Controls.Add(num);
        return num;
    }

    private static AntdUI.Button MakeButton(string text, AntdUI.TTypeMini type) => new()
    {
        Text = text,
        Type = type,
        Size = new Size(96, 34),
        Radius = 6,
        Margin = new Padding(8, 0, 0, 0)
    };
}
