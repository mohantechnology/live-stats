using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Settings;

namespace TaskbarLiveStats.UI;

internal sealed class StatsRenderer : IDisposable
{
    private const int PadX = 10;
    private const int PadY = 6;
    private const int BlockGapDefault = 14;
    private const int BlockGapCompact = 10;
    private const int IconTextGap = 6;
    private const int IconBox = 18;
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
    private const int DetailColGap = 12;
    private const int DetailRowGap = 1;
    private const int DetailNameGap = 8;
    private const int DetailBlockGap = 10;

    private MetricPalette _colors = new();
    private DetailAnchor _detailAnchor = DetailAnchor.Center;

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
        _colors = (settings.Colors ?? new MetricPalette()).Clone();
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
        using var iconFont = CreateIconFont();
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        return MeasureMain(g, frame.Main, preferredThickness, valueFont, labelFont, iconFont);
    }

    public void PaintGdi(Graphics g, OverlayFrame frame, int width, int height)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var valueFont = CreateValueFont();
        using var labelFont = CreateLabelFont();
        using var iconFont = CreateIconFont();
        PaintMain(g, frame.Main, new Rectangle(0, 0, width, height), valueFont, labelFont, iconFont);
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

    private Size MeasureMain(Graphics g, MetricBlock[] blocks, int preferredThickness, Font valueFont, Font labelFont, Font iconFont)
    {
        int thickness = Math.Max(28, preferredThickness);
        bool compact = _density == OverlayDensity.Compact;
        int gap = compact ? BlockGapCompact : BlockGapDefault;

        if (_orientation == OverlayOrientation.Horizontal)
        {
            float total = PadX;
            float maxBlockH = 0;
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
                total += sz.Width + gap;
                maxBlockH = Math.Max(maxBlockH, sz.Height);
            }

            total += PadX - gap;
            int w = Math.Max(100, (int)Math.Ceiling(total));
            int contentH = (int)Math.Ceiling(maxBlockH + PadY * 2);
            bool details = false;
            foreach (var block in blocks)
            {
                if (block.Details != null)
                {
                    details = true;
                    break;
                }
            }

            // Busiest items may grow the row. With that section off, stay in the
            // taskbar slot unless the chosen font itself no longer fits.
            int slot = compact ? 26 : thickness;
            int h = details
                ? Math.Max(slot, contentH)
                : contentH > slot + 8 ? contentH : slot;
            return new Size(w, h);
        }

        float maxW = 0;
        float blockH = 0;
        foreach (var block in blocks)
        {
            var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
            maxW = Math.Max(maxW, sz.Width);
            blockH = Math.Max(blockH, sz.Height);
        }

        int width = Math.Max(100, (int)Math.Ceiling(maxW + PadX * 2));
        int height = Math.Max(60, (int)Math.Ceiling(PadY * 2 + blocks.Length * blockH + Math.Max(0, blocks.Length - 1) * gap));
        return new Size(width, height);
    }

    private void PaintMain(Graphics g, MetricBlock[] blocks, Rectangle band, Font valueFont, Font labelFont, Font iconFont)
    {
        bool compact = _density == OverlayDensity.Compact;
        int gap = compact ? BlockGapCompact : BlockGapDefault;

        if (_orientation == OverlayOrientation.Horizontal)
        {
            float x = PadX;
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
                float y = _detailAnchor switch
                {
                    DetailAnchor.Bottom => band.Bottom - sz.Height - 2,
                    DetailAnchor.Top => band.Top + 2,
                    _ => band.Y + (band.Height - sz.Height) / 2f
                };
                DrawBlock(g, block, x, y, sz, valueFont, labelFont, iconFont, compact);
                x += sz.Width + gap;
            }

            return;
        }

        float yStack = band.Y + PadY;
        float colW = Math.Max(0, band.Width - PadX * 2);
        foreach (var block in blocks)
        {
            var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
            var drawSize = new SizeF(Math.Max(sz.Width, colW), sz.Height);
            DrawBlock(g, block, PadX, yStack, drawSize, valueFont, labelFont, iconFont, compact);
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

    private static GridMetrics MeasureGrid(Graphics g, DetailGroup group, Font font)
    {
        int nameW = TextWidth(g, string.IsNullOrEmpty(group.ReserveName) ? "P99" : group.ReserveName, font);
        int pctW = TextWidth(g, string.IsNullOrEmpty(group.ReservePercent) ? "100%" : group.ReservePercent, font);
        int cellW = nameW + DetailNameGap + pctW;
        int cellH = (int)Math.Ceiling(font.GetHeight(g));
        int slots = Math.Max(1, group.Slots);
        int cols = Math.Min(2, slots);
        int rows = (slots + cols - 1) / cols;
        int width = cellW * cols + Math.Max(0, cols - 1) * DetailColGap;
        int height = rows * cellH + Math.Max(0, rows - 1) * DetailRowGap;
        return new GridMetrics(nameW, pctW, cellW, cellH, cols, rows, width, height);
    }

    private static int TextWidth(Graphics g, string text, Font font)
    {
        SizeF size = g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);
        return (int)Math.Ceiling(size.Width) + 2;
    }

    private void DrawGrid(Graphics g, MetricKind kind, DetailGroup group, float x, float y, Font font)
    {
        GridMetrics grid = MeasureGrid(g, group, font);
        Color accent = Accent(kind);
        using var nameBrush = new SolidBrush(Blend(accent, ThemeHelper.DimColor, 0.35f));
        using var valueBrush = new SolidBrush(accent);
        using var nameFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        using var valueFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        int shown = 0;
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Columns; col++)
            {
                if (shown >= group.Cells.Length)
                    return;

                DetailCell cell = group.Cells[shown++];
                float cx = x + col * (grid.CellWidth + DetailColGap);
                float cy = y + row * (grid.CellHeight + DetailRowGap);
                var nameRect = new RectangleF(cx, cy, grid.NameWidth, grid.CellHeight);
                var pctRect = new RectangleF(cx + grid.NameWidth + DetailNameGap, cy, grid.PercentWidth, grid.CellHeight);
                g.DrawString(cell.Name, font, nameBrush, nameRect, nameFormat);
                g.DrawString(cell.Percent, font, valueBrush, pctRect, valueFormat);
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
        string reserveLabel = GroupLayout.ReserveLabel(block.Kind);
        float valueW = Math.Max(
            g.MeasureString(block.Value, valueFont).Width,
            g.MeasureString(reserveValue, valueFont).Width);
        float labelW = Math.Max(
            g.MeasureString(block.Label, labelFont).Width,
            g.MeasureString(reserveLabel, labelFont).Width);
        float textW = Math.Max(valueW, labelW);
        float textH = valueFont.GetHeight(g) + labelFont.GetHeight(g) - 2;

        if (compact)
            return new MainMetrics(textW + AccentGutter, textH, textW);

        float iconSide = Math.Max(IconBox, iconFont.GetHeight(g) + 4);
        float h = Math.Max(iconSide, textH);
        float w = AccentGutter + iconSide + IconTextGap + textW;
        return new MainMetrics(w, h, textW);
    }

    private SizeF MeasureBlockFixed(
        Graphics g,
        MetricBlock block,
        Font valueFont,
        Font labelFont,
        Font iconFont,
        bool compact)
    {
        MainMetrics main = MeasureMainPart(g, block, valueFont, labelFont, iconFont, compact);
        if (block.Details == null)
            return new SizeF(main.Width, main.Height);

        GridMetrics grid = MeasureGrid(g, block.Details, labelFont);
        bool hangDown = _detailAnchor == DetailAnchor.Top
            && _orientation == OverlayOrientation.Horizontal;
        float labelH = labelFont.GetHeight(g);
        float h = hangDown
            ? main.Height + Math.Max(0, grid.Height - labelH)
            : Math.Max(main.Height, grid.Height);
        return new SizeF(main.Width + DetailBlockGap + grid.Width, h);
    }

    private void DrawBlock(
        Graphics g,
        MetricBlock block,
        float x,
        float y,
        SizeF size,
        Font valueFont,
        Font labelFont,
        Font iconFont,
        bool compact)
    {
        MainMetrics main = MeasureMainPart(g, block, valueFont, labelFont, iconFont, compact);
        bool hangDown = _detailAnchor == DetailAnchor.Top
            && _orientation == OverlayOrientation.Horizontal;
        float mainTop = hangDown ? y : y + size.Height - main.Height;

        Color accent = Accent(block.Kind);
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
            float iconSide = Math.Max(IconBox, iconFont.GetHeight(g) + 4);
            float iconY = mainTop + (main.Height - iconSide) / 2f;
            var iconRect = new RectangleF(textX, iconY, iconSide, iconSide);
            using (var path = RoundedRect(iconRect, 3))
                g.FillPath(iconBg, path);

            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(block.IconGlyph, iconFont, accentBrush, iconRect, center);

            textX += iconSide + IconTextGap;
        }

        float valueH = valueFont.GetHeight(g);
        float labelH = labelFont.GetHeight(g);
        float stackH = valueH + labelH - 2;
        float textY = mainTop + (main.Height - stackH) / 2f;

        using var near = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        g.DrawString(block.Value, valueFont, accentBrush, new RectangleF(textX, textY, textW, valueH + 2), near);
        g.DrawString(block.Label, labelFont, labelBrush, new RectangleF(textX, textY + valueH - 2, textW, labelH + 2), near);

        if (block.Details == null)
            return;

        GridMetrics grid = MeasureGrid(g, block.Details, labelFont);
        float gridX = textX + textW + DetailBlockGap;
        float gridY = hangDown
            ? textY + valueH - 2
            : mainTop + main.Height - grid.Height;
        DrawGrid(g, block.Kind, block.Details, gridX, gridY, labelFont);
    }

    private Color Accent(MetricKind kind)
    {
        Color color = MetricColors.Resolve(_colors, kind);
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
