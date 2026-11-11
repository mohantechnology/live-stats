using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

internal readonly record struct OverlayHit(Rectangle Bounds, string Title);

internal sealed class StatsRenderer : IDisposable
{
    private const float AccentW = 3f;
    private const float AccentGap = 4f;
    private const float AccentGutter = AccentW + AccentGap;

    private Bitmap? _bitmap;
    private int _width;
    private int _height;
    private OverlayOrientation _orientation = OverlayOrientation.Horizontal;
    private OverlayDensity _density = OverlayDensity.Default;
    private bool _showLabels = true;
    private float _valueFontPt = 12f;
    private float _labelFontPt = 8f;
    private float _detailFontPt = 7.5f;
    private readonly record struct Rhythm(
        int PadX,
        int PadY,
        int BlockGap,
        int IconGap,
        float IconSide,
        int ValueLabelGap,
        int DetailBlockGap,
        int DetailNameGap,
        int DetailColGap,
        int DetailRowGap,
        int NameCap);

    private Rhythm _rhythm;
    private MetricPalette _colors = new();
    private UsageColorBands _bands = new();
    private Color _fillColor = ThemeHelper.TaskbarFillColor;
    private DetailAnchor _detailAnchor = DetailAnchor.Center;
    private readonly List<OverlayHit> _hits = new();

    public Color FillColor => _fillColor;

    public string? TitleAt(Point point)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Bounds.Contains(point))
                return _hits[i].Title;
        }

        return null;
    }

    public OverlayOrientation Orientation
    {
        get => _orientation;
        set => _orientation = value;
    }

    public OverlayDensity Density
    {
        get => _density;
        set => _density = value;
    }

    public float ValueFontPt
    {
        get => _valueFontPt;
        set => _valueFontPt = Math.Clamp(value, 7f, 28f);
    }

    public float LabelFontPt
    {
        get => _labelFontPt;
        set => _labelFontPt = Math.Clamp(value, 6f, 18f);
    }

    public float DetailFontPt
    {
        get => _detailFontPt;
        set => _detailFontPt = Math.Clamp(value, 6f, 18f);
    }

    public void ResetSizeLock()
    {
        // Size is recomputed every measure from fonts/density; no growth lock.
    }

    public void ApplyTypography(AppSettings settings)
    {
        Orientation = settings.Orientation;
        Density = settings.Density;
        _showLabels = settings.ShowUsageDetails
            ? settings.ShowLabelsExpanded
            : settings.ShowLabelsCollapsed;
        ValueFontPt = settings.ActiveValueFontPt;
        LabelFontPt = settings.ActiveLabelFontPt;
        DetailFontPt = settings.ActiveDetailFontPt;
        _colors = (settings.Colors ?? new MetricPalette()).Clone();
        _bands = (settings.UsageColors ?? new UsageColorBands()).Clone();
        _bands.Normalize();
        _fillColor = ThemeHelper.ResolveOverlayFill(settings.OverlayBackground);
    }

    public DetailAnchor DetailAnchor
    {
        get => _detailAnchor;
        set => _detailAnchor = value;
    }

    public Size Measure(OverlayFrame frame, int preferredThickness)
    {
        using var valueFont = CreateValueFont();
        using var labelFont = CreateLabelFont();
        using var detailFont = CreateDetailFont();
        using var iconFont = CreateIconFont();
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _rhythm = RhythmFor(g, valueFont, labelFont, detailFont);

        return MeasureMain(g, frame.Main, preferredThickness, valueFont, labelFont, detailFont, iconFont);
    }

    public void PaintGdi(Graphics g, OverlayFrame frame, int width, int height)
    {
        _hits.Clear();
        // Grayscale antialiasing. ClearType fringes are clipped by the line box and
        // turn into a cut through the glyph on a layered window.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var valueFont = CreateValueFont();
        using var labelFont = CreateLabelFont();
        using var detailFont = CreateDetailFont();
        using var iconFont = CreateIconFont();
        _rhythm = RhythmFor(g, valueFont, labelFont, detailFont);
        PaintMain(g, frame.Main, new Rectangle(0, 0, width, height), valueFont, labelFont, detailFont, iconFont);
    }

    public bool PaintLayered(
        IntPtr hwnd,
        OverlayFrame frame,
        int width,
        int height,
        int screenX,
        int screenY,
        bool asChild,
        byte alpha = 255)
    {
        EnsureBitmap(width, height);
        using (var g = Graphics.FromImage(_bitmap!))
        {
            g.Clear(_fillColor);
            PaintGdi(g, frame, width, height);
        }

        return UpdateLayered(_bitmap!, hwnd, width, height, screenX, screenY, asChild, alpha);
    }

    private Size MeasureMain(Graphics g, MetricBlock[] blocks, int preferredThickness, Font valueFont, Font labelFont, Font detailFont, Font iconFont)
    {
        int thickness = Math.Max(24, preferredThickness);
        bool compact = _density == OverlayDensity.Compact;
        int gap = _rhythm.BlockGap;
        int padX = _rhythm.PadX;
        int padY = _rhythm.PadY;

        if (_orientation == OverlayOrientation.Horizontal)
        {
            float total = padX;
            float maxBlockH = 0;
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, detailFont, iconFont, compact);
                total += sz.Width + gap;
                maxBlockH = Math.Max(maxBlockH, sz.Height);
            }

            total += padX - gap;
            int w = Math.Max(80, (int)Math.Ceiling(total));
            int contentH = (int)Math.Ceiling(maxBlockH + padY * 2);
            // Fill the taskbar slot when chips are shorter; grow only when content needs it.
            int h = Math.Max(contentH, thickness);
            return new Size(w, h);
        }

        float maxW = 0;
        float totalH = padY;
        int counted = 0;
        foreach (var block in blocks)
        {
            var sz = MeasureBlockFixed(g, block, valueFont, labelFont, detailFont, iconFont, compact);
            maxW = Math.Max(maxW, sz.Width);
            if (counted > 0)
                totalH += gap;
            totalH += sz.Height;
            counted++;
        }

        totalH += padY;
        int width = Math.Max(80, (int)Math.Ceiling(maxW + padX * 2));
        int height = Math.Max(24, (int)Math.Ceiling(totalH));
        return new Size(width, height);
    }

    private void PaintMain(Graphics g, MetricBlock[] blocks, Rectangle band, Font valueFont, Font labelFont, Font detailFont, Font iconFont)
    {
        bool compact = _density == OverlayDensity.Compact;
        int gap = _rhythm.BlockGap;
        int padX = _rhythm.PadX;
        int padY = _rhythm.PadY;
        bool expanded = false;
        foreach (var block in blocks)
        {
            if (block.Details != null)
            {
                expanded = true;
                break;
            }
        }

        if (_orientation == OverlayOrientation.Horizontal)
        {
            float x = padX;
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, detailFont, iconFont, compact);
                // An open busiest list centers the whole row. Collapsed metrics share the taskbar edge.
                float y = expanded
                    ? band.Y + (band.Height - sz.Height) / 2f
                    : _detailAnchor switch
                    {
                        DetailAnchor.Bottom => band.Bottom - sz.Height - 2,
                        DetailAnchor.Top => band.Top + 2,
                        _ => band.Y + (band.Height - sz.Height) / 2f
                    };
                DrawBlock(g, block, x, y, sz, valueFont, labelFont, detailFont, iconFont, compact);
                x += sz.Width + gap;
            }

            return;
        }

        float yStack = band.Y + padY;
        float colW = Math.Max(0, band.Width - padX * 2);
        foreach (var block in blocks)
        {
            var sz = MeasureBlockFixed(g, block, valueFont, labelFont, detailFont, iconFont, compact);
            var drawSize = new SizeF(Math.Max(sz.Width, colW), sz.Height);
            DrawBlock(g, block, padX, yStack, drawSize, valueFont, labelFont, detailFont, iconFont, compact);
            yStack += sz.Height + gap;
        }
    }

    private readonly record struct GridMetrics(
        int NameWidth,
        int PercentWidth,
        int CellWidth,
        int CellHeight,
        int Columns,
        int Rows,
        int Width,
        int Height);

    private Rhythm RhythmFor(Graphics g, Font valueFont, Font labelFont, Font detailFont)
    {
        float valueH = valueFont.GetHeight(g);
        float detailH = detailFont.GetHeight(g);
        bool compact = _density == OverlayDensity.Compact;
        bool vertical = _orientation == OverlayOrientation.Vertical;
        float scale = compact ? 0.9f : 1f;
        int Px(float pixels) => Math.Max(1, (int)Math.Round(pixels * scale));

        // Balanced rhythm: stacked value/label with a little air, still taskbar-friendly.
        return new Rhythm(
            PadX: Px(vertical ? 8 : 10),
            PadY: Px(vertical ? 5 : 3),
            BlockGap: Px(vertical ? 8 : 12),
            IconGap: Px(6),
            IconSide: Math.Clamp(valueH * 0.95f, 16f, 18f),
            ValueLabelGap: Px(2),
            DetailBlockGap: Px(8),
            DetailNameGap: Px(4),
            DetailColGap: Px(10),
            DetailRowGap: Px(2),
            NameCap: Math.Max(72, Math.Min(88, (int)Math.Round(detailH * 7.5f))));
    }

    private readonly record struct LineBox(float Width, float Height);

    private static StringFormat TextFormat(StringAlignment align) => new(StringFormat.GenericTypographic)
    {
        Alignment = align,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.None,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces
    };

    /// <summary>
    /// Width and height that actually contain the ink, including the right bearing
    /// and the part of the glyph that sits on the baseline. A box of GetHeight()
    /// cuts U and B through the middle.
    /// </summary>
    private static LineBox MeasureLine(Graphics g, string text, Font font)
    {
        using var format = TextFormat(StringAlignment.Near);
        string sample = string.IsNullOrEmpty(text) ? " " : text;
        SizeF size = g.MeasureString(sample, font, int.MaxValue, format);
        float slackX = Math.Max(3f, font.SizeInPoints * 0.28f);
        float slackY = Math.Max(2f, font.SizeInPoints * 0.2f);
        float height = Math.Max(size.Height, font.GetHeight(g)) + slackY;
        return new LineBox(size.Width + slackX, height);
    }

    private GridMetrics MeasureGrid(Graphics g, DetailGroup group, Font font)
    {
        LineBox name = MeasureLine(g, string.IsNullOrEmpty(group.ReserveName) ? "P99" : group.ReserveName, font);
        foreach (DetailCell cell in group.Cells)
            name = new LineBox(Math.Max(name.Width, MeasureLine(g, cell.Name, font).Width), name.Height);
        float nameW = Math.Min(name.Width, _rhythm.NameCap);
        LineBox percent = MeasureLine(g, string.IsNullOrEmpty(group.ReservePercent) ? "100%" : group.ReservePercent, font);
        float cellW = nameW + _rhythm.DetailNameGap + percent.Width;
        int cellH = (int)Math.Ceiling(percent.Height);
        int slots = Math.Max(1, group.Slots);
        // Vertical stacks stay narrow with one column; horizontal keeps a tight 2-col grid.
        int cols = _orientation == OverlayOrientation.Vertical ? 1 : Math.Min(2, slots);
        int rows = (slots + cols - 1) / cols;
        int rowGap = _rhythm.DetailRowGap;
        int width = (int)Math.Ceiling(cellW * cols + Math.Max(0, cols - 1) * _rhythm.DetailColGap);
        int height = rows * cellH + Math.Max(0, rows - 1) * rowGap;
        return new GridMetrics((int)Math.Ceiling(nameW), (int)Math.Ceiling(percent.Width), (int)Math.Ceiling(cellW), cellH, cols, rows, width, height);
    }

    private void DrawGrid(Graphics g, MetricKind kind, DetailGroup group, float x, float y, Font font)
    {
        GridMetrics grid = MeasureGrid(g, group, font);
        using var nameFormat = TextFormat(StringAlignment.Near);
        using var valueFormat = TextFormat(StringAlignment.Far);

        int shown = 0;
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Columns; col++)
            {
                if (shown >= group.Cells.Length)
                    return;

                DetailCell cell = group.Cells[shown++];
                float cx = x + col * (grid.CellWidth + _rhythm.DetailColGap);
                float cy = y + row * (grid.CellHeight + _rhythm.DetailRowGap);
                var nameRect = new RectangleF(cx, cy, grid.NameWidth, grid.CellHeight);
                var pctRect = new RectangleF(cx + grid.NameWidth + _rhythm.DetailNameGap, cy, grid.PercentWidth, grid.CellHeight);
                Color accent = Accent(kind, cell.LoadPercent);
                using var nameBrush = new SolidBrush(Blend(accent, ThemeHelper.DimColor, 0.35f));
                using var valueBrush = new SolidBrush(accent);
                g.DrawString(cell.Name, font, nameBrush, nameRect, nameFormat);
                g.DrawString(cell.Percent, font, valueBrush, pctRect, valueFormat);
                AddHit(new RectangleF(cx, cy, grid.CellWidth, grid.CellHeight), CellTitle(cell));
            }
        }
    }

    private readonly record struct MainMetrics(
        float Width,
        float Height,
        float TextWidth);

    private bool StackDetailsBelow => _orientation == OverlayOrientation.Vertical;

    private MainMetrics MeasureMainPart(
        Graphics g,
        MetricBlock block,
        Font valueFont,
        Font labelFont,
        Font iconFont,
        bool compact)
    {
        string reserveValue = GroupLayout.ReserveValue(block.Kind);
        string reserveLabel = string.IsNullOrEmpty(block.LabelReserve)
            ? GroupLayout.ReserveLabel(block.Kind)
            : block.LabelReserve;
        LineBox value = MeasureLine(g, block.Value, valueFont);
        LineBox valueReserve = MeasureLine(g, reserveValue, valueFont);
        float valueW = Math.Max(value.Width, valueReserve.Width);
        float textW = valueW;
        float textH = value.Height;

        if (_showLabels)
        {
            LineBox label = MeasureLine(g, block.Label, labelFont);
            LineBox labelReserve = MeasureLine(g, reserveLabel, labelFont);
            float labelW = Math.Max(label.Width, labelReserve.Width);
            textW = Math.Max(valueW, labelW);
            textH = value.Height + _rhythm.ValueLabelGap + label.Height;
        }

        if (compact)
            return new MainMetrics(AccentGutter + textW, textH, textW);

        float iconSide = Math.Max(_rhythm.IconSide, iconFont.GetHeight(g) + 4);
        float h = Math.Max(iconSide, textH);
        float w = AccentGutter + iconSide + _rhythm.IconGap + textW;
        return new MainMetrics(w, h, textW);
    }

    private SizeF MeasureBlockFixed(
        Graphics g,
        MetricBlock block,
        Font valueFont,
        Font labelFont,
        Font detailFont,
        Font iconFont,
        bool compact)
    {
        MainMetrics main = MeasureMainPart(g, block, valueFont, labelFont, iconFont, compact);
        if (block.Details == null)
            return new SizeF(main.Width, main.Height);

        GridMetrics grid = MeasureGrid(g, block.Details, detailFont);
        if (StackDetailsBelow)
        {
            float indent = AccentGutter;
            float w = Math.Max(main.Width, indent + grid.Width);
            float h = main.Height + _rhythm.DetailBlockGap + grid.Height;
            return new SizeF(w, h);
        }

        float sideH = Math.Max(main.Height, grid.Height);
        return new SizeF(main.Width + _rhythm.DetailBlockGap + grid.Width, sideH);
    }

    private void DrawBlock(
        Graphics g,
        MetricBlock block,
        float x,
        float y,
        SizeF size,
        Font valueFont,
        Font labelFont,
        Font detailFont,
        Font iconFont,
        bool compact)
    {
        MainMetrics main = MeasureMainPart(g, block, valueFont, labelFont, iconFont, compact);
        bool below = block.Details != null && StackDetailsBelow;
        // Side-by-side details center the chip in the block; stacked details keep the chip on top.
        float mainTop = block.Details != null && !below
            ? y + (size.Height - main.Height) / 2f
            : y;

        Color accent = Accent(block.Kind, block.LoadPercent);
        Color labelColor = Blend(accent, ThemeHelper.DimColor, 0.22f);
        using var accentBrush = new SolidBrush(accent);
        using var labelBrush = new SolidBrush(labelColor);
        using var iconBg = new SolidBrush(Color.FromArgb(48, accent));

        float barH = Math.Max(10f, main.Height - 2f);
        var barRect = new RectangleF(x, mainTop + (main.Height - barH) / 2f, AccentW, barH);
        float barRadius = Math.Min(AccentW / 2f, Math.Max(1f, barRect.Height / 2f - 0.25f));
        using (var barPath = RoundedRect(barRect, barRadius))
            g.FillPath(accentBrush, barPath);

        float cursorX = x + AccentGutter;

        if (!compact)
        {
            float iconSide = Math.Max(_rhythm.IconSide, iconFont.GetHeight(g) + 4);
            float iconY = mainTop + (main.Height - iconSide) / 2f;
            var iconRect = new RectangleF(cursorX, iconY, iconSide, iconSide);
            using (var path = RoundedRect(iconRect, 3))
                g.FillPath(iconBg, path);
            using var center = TextFormat(StringAlignment.Center);
            center.LineAlignment = StringAlignment.Center;
            g.DrawString(block.IconGlyph, iconFont, accentBrush, iconRect, center);
            cursorX += iconSide + _rhythm.IconGap;
        }

        LineBox valueBox = MeasureLine(g, block.Value, valueFont);
        float stackH = valueBox.Height;
        LineBox labelBox = default;
        if (_showLabels)
        {
            labelBox = MeasureLine(g, block.Label, labelFont);
            stackH += _rhythm.ValueLabelGap + labelBox.Height;
        }

        float textY = mainTop + (main.Height - stackH) / 2f;
        using var near = TextFormat(StringAlignment.Near);
        g.DrawString(
            block.Value,
            valueFont,
            accentBrush,
            new RectangleF(cursorX, textY, main.TextWidth, valueBox.Height),
            near);
        if (_showLabels)
        {
            g.DrawString(
                block.Label,
                labelFont,
                labelBrush,
                new RectangleF(cursorX, textY + valueBox.Height + _rhythm.ValueLabelGap, main.TextWidth, labelBox.Height),
                near);
        }

        AddHit(new RectangleF(x, y, size.Width, size.Height), BlockTitle(block));

        if (block.Details == null)
            return;

        GridMetrics grid = MeasureGrid(g, block.Details, detailFont);
        float gridX;
        float gridY;
        if (below)
        {
            gridX = x + AccentGutter;
            gridY = mainTop + main.Height + _rhythm.DetailBlockGap;
        }
        else
        {
            gridX = cursorX + main.TextWidth + _rhythm.DetailBlockGap;
            gridY = mainTop + (main.Height - grid.Height) / 2f;
        }

        DrawGrid(g, block.Kind, block.Details, gridX, gridY, detailFont);
    }

    private static string BlockTitle(MetricBlock block)
    {
        if (string.IsNullOrEmpty(block.Value) || block.Label.Contains(block.Value, StringComparison.Ordinal))
            return block.Label;
        return block.Label + "  " + block.Value;
    }

    private static string CellTitle(DetailCell cell)
    {
        if (string.IsNullOrEmpty(cell.Percent))
            return cell.Name;
        return cell.Name + "  " + cell.Percent;
    }

    private void AddHit(RectangleF bounds, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || bounds.Width < 1f || bounds.Height < 1f)
            return;
        _hits.Add(new OverlayHit(Rectangle.Round(bounds), title));
    }

    internal Color Accent(MetricKind kind, double? loadPercent = null)
    {
        Color color = MetricColors.Resolve(_colors, kind);
        if (loadPercent is double pct)
            color = _bands.ColorFor(pct, color);
        if (ThemeHelper.IsDarkTaskbar())
            return color;

        return Color.FromArgb(
            color.A,
            Math.Clamp((int)(color.R * 0.62), 0, 255),
            Math.Clamp((int)(color.G * 0.62), 0, 255),
            Math.Clamp((int)(color.B * 0.62), 0, 255));
    }

    private static Color Blend(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != null && _width == width && _height == height)
            return;

        _bitmap?.Dispose();
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
    }

    private Font CreateValueFont() =>
        new("Segoe UI Semibold", _valueFontPt, FontStyle.Bold, GraphicsUnit.Point);

    private Font CreateLabelFont() =>
        new("Segoe UI", _labelFontPt, FontStyle.Regular, GraphicsUnit.Point);

    private Font CreateDetailFont() =>
        new("Segoe UI", _detailFontPt, FontStyle.Regular, GraphicsUnit.Point);

    private static Font CreateIconFont()
    {
        const float size = 11f;
        if (FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons"))
            return new Font("Segoe Fluent Icons", size, FontStyle.Regular, GraphicsUnit.Point);
        if (FontFamily.Families.Any(f => f.Name == "Segoe MDL2 Assets"))
            return new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Point);
        return new Font("Segoe UI Symbol", size, FontStyle.Regular, GraphicsUnit.Point);
    }

    private static bool UpdateLayered(
        Bitmap bitmap,
        IntPtr hwnd,
        int width,
        int height,
        int screenX,
        int screenY,
        bool asChild,
        byte alpha)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap();
        IntPtr oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

        bool ok;
        try
        {
            var size = new NativeMethods.SIZE { cx = width, cy = height };
            var pointSource = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            if (asChild)
            {
                ok = NativeMethods.UpdateLayeredWindowChild(
                    hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref pointSource, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            else
            {
                var topPos = new NativeMethods.POINT { X = screenX, Y = screenY };
                ok = NativeMethods.UpdateLayeredWindow(
                    hwnd, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        return ok;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
