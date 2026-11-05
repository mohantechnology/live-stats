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

    private Bitmap? _bitmap;
    private int _width;
    private int _height;
    private OverlayOrientation _orientation = OverlayOrientation.Horizontal;
    private OverlayDensity _density = OverlayDensity.Default;
    private float _valueFontPt = 12f;
    private float _labelFontPt = 8f;

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
    }

    public Size Measure(MetricBlock[] blocks, int preferredThickness)
    {
        int thickness = Math.Max(28, preferredThickness);
        bool compact = _density == OverlayDensity.Compact;
        int gap = compact ? BlockGapCompact : BlockGapDefault;

        using var valueFont = CreateValueFont();
        using var labelFont = CreateLabelFont();
        using var iconFont = CreateIconFont();
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

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
            int h = Math.Max(compact ? 26 : thickness, (int)Math.Ceiling(maxBlockH + PadY * 2));
            return new Size(w, h);
        }
        else
        {
            float maxW = 0;
            float blockH = 0;
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
                maxW = Math.Max(maxW, sz.Width);
                blockH = Math.Max(blockH, sz.Height);
            }

            int w = Math.Max(100, (int)Math.Ceiling(maxW + PadX * 2));
            int h = Math.Max(60, (int)Math.Ceiling(PadY * 2 + blocks.Length * blockH + Math.Max(0, blocks.Length - 1) * gap));
            return new Size(w, h);
        }
    }

    public void PaintGdi(Graphics g, MetricBlock[] blocks, int width, int height)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        bool compact = _density == OverlayDensity.Compact;
        int gap = compact ? BlockGapCompact : BlockGapDefault;

        using var valueFont = CreateValueFont();
        using var labelFont = CreateLabelFont();
        using var iconFont = CreateIconFont();
        using var valueBrush = new SolidBrush(ThemeHelper.TextColor);
        using var labelBrush = new SolidBrush(ThemeHelper.DimColor);
        using var iconBrush = new SolidBrush(ThemeHelper.DimColor);
        using var iconBg = new SolidBrush(Color.FromArgb(28, ThemeHelper.TextColor));

        if (_orientation == OverlayOrientation.Horizontal)
        {
            float x = PadX;
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
                float y = (height - sz.Height) / 2f;
                DrawBlock(g, block, x, y, sz, valueFont, labelFont, iconFont, valueBrush, labelBrush, iconBrush, iconBg, compact);
                x += sz.Width + gap;
            }
        }
        else
        {
            float y = PadY;
            float colW = Math.Max(0, width - PadX * 2);
            foreach (var block in blocks)
            {
                var sz = MeasureBlockFixed(g, block, valueFont, labelFont, iconFont, compact);
                var drawSize = new SizeF(Math.Max(sz.Width, colW), sz.Height);
                DrawBlock(g, block, PadX, y, drawSize, valueFont, labelFont, iconFont, valueBrush, labelBrush, iconBrush, iconBg, compact);
                y += sz.Height + gap;
            }
        }
    }

    public bool PaintLayered(
        IntPtr hwnd,
        MetricBlock[] blocks,
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
            PaintGdi(g, blocks, width, height);
        }

        return UpdateLayered(_bitmap!, hwnd, width, height, screenX, screenY, asChild, alpha);
    }

    private static SizeF MeasureBlockFixed(
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

        float valueH = valueFont.GetHeight(g);
        float labelH = labelFont.GetHeight(g);
        float textH = valueH + labelH - 2;

        if (compact)
            return new SizeF(textW, textH);

        float iconSide = Math.Max(IconBox, iconFont.GetHeight(g) + 4);
        float h = Math.Max(iconSide, textH);
        float w = iconSide + IconTextGap + textW;
        return new SizeF(w, h);
    }

    private static void DrawBlock(
        Graphics g,
        MetricBlock block,
        float x,
        float y,
        SizeF size,
        Font valueFont,
        Font labelFont,
        Font iconFont,
        Brush valueBrush,
        Brush labelBrush,
        Brush iconBrush,
        Brush iconBg,
        bool compact)
    {
        float textX = x;
        float textW = size.Width;

        if (!compact)
        {
            float iconSide = Math.Max(IconBox, iconFont.GetHeight(g) + 4);
            float iconY = y + (size.Height - iconSide) / 2f;
            var iconRect = new RectangleF(x, iconY, iconSide, iconSide);
            using (var path = RoundedRect(iconRect, 3))
                g.FillPath(iconBg, path);

            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(block.IconGlyph, iconFont, iconBrush, iconRect, center);

            textX = x + iconSide + IconTextGap;
            textW = Math.Max(8, size.Width - iconSide - IconTextGap);
        }

        float valueH = valueFont.GetHeight(g);
        float labelH = labelFont.GetHeight(g);
        float stackH = valueH + labelH - 2;
        float textY = y + (size.Height - stackH) / 2f;

        using var near = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        g.DrawString(block.Value, valueFont, valueBrush, new RectangleF(textX, textY, textW, valueH + 2), near);
        g.DrawString(block.Label, labelFont, labelBrush, new RectangleF(textX, textY + valueH - 2, textW, labelH + 2), near);
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
