using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TaskbarLiveStats.Native;
using TaskbarLiveStats.Taskbar;

namespace TaskbarLiveStats.UI;

internal sealed class StatsRenderer : IDisposable
{
    private const int PadX = 8;
    private const int GroupGap = 12;
    private const int ColGap = 3;

    private Bitmap? _bitmap;
    private int _width;
    private int _height;
    private int _lockedWidth;

    public Size Measure(MetricGroup[] groups, int taskbarHeight)
    {
        // Fill nearly the full taskbar thickness so the reserved slot looks flush
        int height = Math.Clamp(taskbarHeight - 2, 30, 52);
        using var font = CreateFont(height);
        using var unitFont = CreateUnitFont(height);
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float total = PadX;
        foreach (var group in groups)
            total += MeasureGroupWidth(g, group, font, unitFont) + GroupGap;
        total += PadX - GroupGap;

        int w = Math.Max(180, (int)Math.Ceiling(total));
        if (w > _lockedWidth)
            _lockedWidth = w;
        return new Size(_lockedWidth > 0 ? _lockedWidth : w, height);
    }

    public void PaintGdi(Graphics g, MetricGroup[] groups, int width, int height)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var font = CreateFont(height);
        using var unitFont = CreateUnitFont(height);
        using var iconBrush = new SolidBrush(ThemeHelper.DimColor);
        using var valueBrush = new SolidBrush(ThemeHelper.TextColor);
        using var unitBrush = new SolidBrush(ThemeHelper.DimColor);

        float x = PadX;
        float rowH = height / 2f;

        foreach (var group in groups)
        {
            float gw = MeasureGroupWidth(g, group, font, unitFont);
            DrawLine(g, group.Top, x, 1, gw, rowH, font, unitFont, iconBrush, valueBrush, unitBrush);
            DrawLine(g, group.Bottom, x, rowH - 1, gw, rowH, font, unitFont, iconBrush, valueBrush, unitBrush);

            x += gw + GroupGap;
            if (x < width - PadX)
            {
                using var sep = new Pen(Color.FromArgb(40, ThemeHelper.TextColor), 1f);
                float midY = height * 0.22f;
                g.DrawLine(sep, x - GroupGap / 2f, midY, x - GroupGap / 2f, height - midY);
            }
        }
    }

    public void PaintLayered(IntPtr hwnd, MetricGroup[] groups, int width, int height, int screenX, int screenY, bool asChild)
    {
        EnsureBitmap(width, height);
        using (var g = Graphics.FromImage(_bitmap!))
        {
            g.Clear(ThemeHelper.TaskbarFillColor);
            PaintGdi(g, groups, width, height);
        }

        UpdateLayered(_bitmap!, hwnd, width, height, screenX, screenY, asChild);
    }

    private static void DrawLine(
        Graphics g,
        MetricLine line,
        float x,
        float y,
        float groupWidth,
        float rowHeight,
        Font font,
        Font unitFont,
        Brush iconBrush,
        Brush valueBrush,
        Brush unitBrush)
    {
        float iconW = g.MeasureString(line.Icon, font).Width;
        float unitW = string.IsNullOrEmpty(line.Unit) ? 0 : g.MeasureString(line.Unit, unitFont).Width;
        float valueArea = Math.Max(12, groupWidth - iconW - unitW - ColGap * 2);

        var iconRect = new RectangleF(x, y, iconW, rowHeight);
        var valueRect = new RectangleF(x + iconW + ColGap, y, valueArea, rowHeight);
        var unitRect = new RectangleF(x + iconW + ColGap + valueArea, y, unitW + 2, rowHeight);

        using var near = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var valueFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

        g.DrawString(line.Icon, font, iconBrush, iconRect, near);
        g.DrawString(line.Value, font, valueBrush, valueRect, valueFmt);
        if (!string.IsNullOrEmpty(line.Unit))
            g.DrawString(line.Unit, unitFont, unitBrush, unitRect, near);
    }

    private static float MeasureGroupWidth(Graphics g, MetricGroup group, Font font, Font unitFont)
    {
        float top = LineWidth(g, group.Top, font, unitFont);
        float bot = LineWidth(g, group.Bottom, font, unitFont);
        float w = Math.Max(top, bot);
        float min = group.Top.Icon switch
        {
            "CPU" or "GPU" => g.MeasureString("VRAM 00.0GB/00.0GB", font).Width,
            "↑" => g.MeasureString("↑ 000.0 Mbps", font).Width,
            "R" => g.MeasureString("R 000.0 MB/s", font).Width,
            _ => w
        };
        return Math.Max(w, min);
    }

    private static float LineWidth(Graphics g, MetricLine line, Font font, Font unitFont)
    {
        float icon = g.MeasureString(line.Icon, font).Width;
        float valueCol = Math.Max(g.MeasureString(line.Value, font).Width, g.MeasureString("000.0GB", font).Width);
        float unitCol = Math.Max(
            string.IsNullOrEmpty(line.Unit) ? 0 : g.MeasureString(line.Unit, unitFont).Width,
            g.MeasureString("/00.0GB", unitFont).Width);
        return icon + ColGap + valueCol + ColGap + unitCol;
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

    private static Font CreateFont(int height)
    {
        float size = Math.Clamp(height * 0.27f, 7.5f, 9.5f);
        return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point);
    }

    private static Font CreateUnitFont(int height)
    {
        float size = Math.Clamp(height * 0.23f, 6.5f, 8.5f);
        return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
    }

    private static void UpdateLayered(Bitmap bitmap, IntPtr hwnd, int width, int height, int screenX, int screenY, bool asChild)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap();
        IntPtr oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

        try
        {
            var size = new NativeMethods.SIZE { cx = width, cy = height };
            var pointSource = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            if (asChild)
            {
                // MSDN: for layered child windows pptDst must be NULL; position comes from SetWindowPos.
                NativeMethods.UpdateLayeredWindowChild(
                    hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref pointSource, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            else
            {
                var topPos = new NativeMethods.POINT { X = screenX, Y = screenY };
                NativeMethods.UpdateLayeredWindow(
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
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
