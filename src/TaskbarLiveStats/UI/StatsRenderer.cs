using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

internal readonly record struct OverlayHit(Rectangle Bounds, string Title);

internal sealed class StatsRenderer : IDisposable
{
    private const float AccentW = 5f;
    private const float AccentGap = 6f;
    private const float AccentGutter = AccentW + AccentGap;

    private Bitmap? _bitmap;
    private int _width;
    private int _height;
    private OverlayOrientation _orientation = OverlayOrientation.Horizontal;
    private OverlayDensity _density = OverlayDensity.Default;
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
    private DetailAnchor _detailAnchor = DetailAnchor.Center;
    private readonly List<OverlayHit> _hits = new();

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
        ValueFontPt = settings.ActiveValueFontPt;
        LabelFontPt = settings.ActiveLabelFontPt;
        DetailFontPt = settings.ActiveDetailFontPt;
        _colors = (settings.Colors ?? new MetricPalette()).Clone();
        _bands = (settings.UsageColors ?? new UsageColorBands()).Clone();
        _bands.Normalize();
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
            g.Clear(ThemeHelper.TaskbarFillColor);
            PaintGdi(g, frame, width, height);
        }

        return UpdateLayered(_bitmap!, hwnd, width, height, screenX, screenY, asChild, alpha);
    }

    private Size MeasureMain(Graphics g, MetricBlock[] blocks, int preferredThickness, Font valueFont, Font labelFont, Font detailFont, Font iconFont)
    {
        int thickness = Math.Max(28, preferredThickness);
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
            int w = Math.Max(100, (int)Math.Ceiling(total));
            int contentH = (int)Math.Ceiling(maxBlockH + padY * 2);
            // Never keep a slot that is shorter than the text. A few pixels short
            // slices the bottom off the value and the label.
            int slot = compact ? 26 : thickness;
            int h = Math.Max(slot, contentH);
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
        int width = Math.Max(100, (int)Math.Ceiling(maxW + padX * 2));
        int height = Math.Max(60, (int)Math.Ceiling(totalH));
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
        float labelH = labelFont.GetHeight(g);
        float detailH = detailFont.GetHeight(g);
        bool compact = _density == OverlayDensity.Compact;
        bool vertical = _orientation == OverlayOrientation.Vertical;
        float scale = compact ? 0.82f : 1f;
        int Px(float ems, float min) => Math.Max((int)Math.Round(min), (int)Math.Round(ems * scale));

        // Gaps are fractions of the fonts actually in use, so a larger value or
        // label opens the bar instead of packing the same pixels tighter.
        return new Rhythm(
            PadX: Px(valueH * 0.85f, 14),
            PadY: Px(labelH * 0.85f, 10),
            BlockGap: vertical ? Px(valueH * 1.2f, 22) : Px(valueH * 1.15f, 20),
            IconGap: Px(labelH * 0.9f, 10),
            IconSide: Math.Max(20f, valueH * 0.92f),
            ValueLabelGap: Px(Math.Max(labelH * 0.45f, valueH * 0.22f), 6),
            DetailBlockGap: Px(detailH * 1.85f, 20),
            DetailNameGap: Px(detailH * 0.9f, 10),
            DetailColGap: Px(detailH * 1.7f, 20),
            DetailRowGap: Px(detailH * (vertical ? 0.55f : 0.28f), vertical ? 6 : 3),
            NameCap: Math.Max(108, (int)Math.Round(detailH * 10.5f)));
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
        float slackX = Math.Max(6f, font.SizeInPoints * 0.55f);
        float slackY = Math.Max(4f, font.SizeInPoints * 0.5f);
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
        int cols = Math.Min(2, slots);
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

    private readonly record struct MainMetrics(float Width, float Height, float TextWidth);

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
        LineBox label = MeasureLine(g, block.Label, labelFont);
        LineBox labelReserve = MeasureLine(g, reserveLabel, labelFont);
        float textW = Math.Max(Math.Max(value.Width, valueReserve.Width), Math.Max(label.Width, labelReserve.Width));
        float textH = value.Height + _rhythm.ValueLabelGap + label.Height;

        if (compact)
            return new MainMetrics(textW + AccentGutter, textH, textW);

        float iconSide = Math.Max(_rhythm.IconSide, iconFont.GetHeight(g) + 6);
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
        float h = Math.Max(main.Height, grid.Height);
        return new SizeF(main.Width + _rhythm.DetailBlockGap + grid.Width, h);
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
        // With a busiest list, keep the icon and value in the middle of the block
        // instead of sinking them to the bottom beside the last row.
        float mainTop = block.Details != null
            ? y + (size.Height - main.Height) / 2f
            : y;

        Color accent = Accent(block.Kind, block.LoadPercent);
        Color labelColor = Blend(accent, ThemeHelper.DimColor, 0.22f);
        using var accentBrush = new SolidBrush(accent);
        using var labelBrush = new SolidBrush(labelColor);
        using var iconBg = new SolidBrush(Color.FromArgb(48, accent));

        var barRect = new RectangleF(x, mainTop + 1.5f, AccentW, Math.Max(8f, main.Height - 3f));
        float barRadius = Math.Min(AccentW / 2f, Math.Max(1f, barRect.Height / 2f - 0.25f));
        using (var barPath = RoundedRect(barRect, barRadius))
            g.FillPath(accentBrush, barPath);

        float textX = x + AccentGutter;
        float textW = main.TextWidth;

        if (!compact)
        {
            float iconSide = Math.Max(_rhythm.IconSide, iconFont.GetHeight(g) + 6);
            float iconY = mainTop + (main.Height - iconSide) / 2f;
            var iconRect = new RectangleF(textX, iconY, iconSide, iconSide);
            using (var path = RoundedRect(iconRect, 3))
                g.FillPath(iconBg, path);

            using var center = TextFormat(StringAlignment.Center);
            center.LineAlignment = StringAlignment.Center;
            g.DrawString(block.IconGlyph, iconFont, accentBrush, iconRect, center);

            textX += iconSide + _rhythm.IconGap;
        }

        LineBox valueBox = MeasureLine(g, block.Value, valueFont);
        LineBox labelBox = MeasureLine(g, block.Label, labelFont);
        float stackH = valueBox.Height + _rhythm.ValueLabelGap + labelBox.Height;
        float textY = mainTop + (main.Height - stackH) / 2f;

        using var near = TextFormat(StringAlignment.Near);
        g.DrawString(block.Value, valueFont, accentBrush, new RectangleF(textX, textY, textW, valueBox.Height), near);
        g.DrawString(block.Label, labelFont, labelBrush, new RectangleF(textX, textY + valueBox.Height + _rhythm.ValueLabelGap, textW, labelBox.Height), near);
        AddHit(new RectangleF(x, y, size.Width, size.Height), BlockTitle(block));

        if (block.Details == null)
            return;

        GridMetrics grid = MeasureGrid(g, block.Details, detailFont);
        float gridX = textX + textW + _rhythm.DetailBlockGap;
        float gridY = mainTop + (main.Height - grid.Height) / 2f;
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
