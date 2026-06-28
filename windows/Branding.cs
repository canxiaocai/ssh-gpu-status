using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace GpuStatus;

/// 托盘图标渲染。macOS 把「logo + 利用率%」合成一张菜单栏图（可变宽度、模板图自动着色）；
/// Windows 托盘图标是固定方形、不能自动着色，所以这里：
///   有利用率数据时把数字画进方形图标（托盘上唯一常驻可见的文字通道），
///   否则画一个芯片/脉冲字形。颜色随任务栏深/浅色切换（深色→白，浅色→近黑）。
public static class Branding
{
    /// 任务栏是否深色（决定图标用白还是近黑）。
    public static bool IsTaskbarDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int v) return v == 0;
        }
        catch { /* ignore */ }
        return true; // 默认按深色任务栏处理
    }

    public static Icon CreateTrayIcon(int? utilization, bool dark, int size = 32)
    {
        var fg = dark ? Color.White : Color.FromArgb(0x20, 0x20, 0x20);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            if (utilization is { } u) DrawNumber(g, u, size, fg);
            else DrawGlyph(g, size, fg);
        }
        return IconFromBitmap(bmp);
    }

    /// 在 About 等处用品牌绿画一个 logo 字形。
    public static Bitmap GlyphBitmap(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        DrawGlyph(g, size, color);
        return bmp;
    }

    private static void DrawNumber(Graphics g, int value, int size, Color fg)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        using var brush = new SolidBrush(fg);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        // 自适应字号：缩到能塞进方形为止（处理 "100" 这种 3 位数）。
        for (float fontSize = size * 0.82f; fontSize > 6f; fontSize -= 1f)
        {
            using var f = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            if (sz.Width <= size - 1 && sz.Height <= size + 2)
            {
                g.DrawString(text, f, brush, new RectangleF(0, 0, size, size), sf);
                return;
            }
        }
        using var fallback = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(text, fallback, brush, new RectangleF(0, 0, size, size), sf);
    }

    private static void DrawGlyph(Graphics g, int size, Color fg)
    {
        using var pen = new Pen(fg, Math.Max(1.5f, size / 16f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        var pad = size * 0.16f;
        var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
        using (var path = RoundedRect(rect, size * 0.14f))
            g.DrawPath(pen, path);

        // 中间一条心跳/脉冲线。
        var midY = size / 2f;
        var pts = new[]
        {
            new PointF(rect.Left + rect.Width * 0.08f, midY),
            new PointF(rect.Left + rect.Width * 0.32f, midY),
            new PointF(rect.Left + rect.Width * 0.44f, midY - rect.Height * 0.24f),
            new PointF(rect.Left + rect.Width * 0.56f, midY + rect.Height * 0.24f),
            new PointF(rect.Left + rect.Width * 0.68f, midY),
            new PointF(rect.Left + rect.Width * 0.92f, midY),
        };
        g.DrawLines(pen, pts);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// 把位图封装成一个「自有数据」的 Icon（PNG 压缩的 ICO，Vista+ 支持），
    /// 避免 GetHicon 的句柄生命周期问题，可正常 Dispose。
    private static Icon IconFromBitmap(Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        var pngBytes = png.ToArray();

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);  // reserved
            w.Write((short)1);  // type = icon
            w.Write((short)1);  // image count
            w.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
            w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
            w.Write((byte)0);   // palette colors
            w.Write((byte)0);   // reserved
            w.Write((short)1);  // color planes
            w.Write((short)32); // bits per pixel
            w.Write(pngBytes.Length);
            w.Write(6 + 16);    // offset to image data (6-byte header + 16-byte entry)
            w.Write(pngBytes);
        }
        ms.Position = 0;
        return new Icon(ms, bmp.Width, bmp.Height);
    }
}
