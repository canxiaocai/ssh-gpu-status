using System.Drawing;

namespace GpuStatus;

/// 配色：以 NVIDIA 绿为主，负载升高时依次过渡到黄、红。
/// 在 light…heavy 区间内连续插值（绿→黄→红），避免阈值边界的硬跳变闪烁。对应 macOS 版 Theme.swift。
public static class Theme
{
    public static readonly Color Green = Color.FromArgb(0x76, 0xB9, 0x00);  // NVIDIA 品牌绿 #76B900
    public static readonly Color Yellow = Color.FromArgb(0xF2, 0xB0, 0x07); // 警告黄
    public static readonly Color Red = Color.FromArgb(0xE2, 0x3B, 0x2E);    // 高负载红

    /// GPU 利用率配色（%）。
    public static Color Utilization(int percent) => Intensity(percent, 60, 85);
    /// 显存占用配色（%）。
    public static Color Memory(int percent) => Intensity(percent, 60, 85);
    /// 温度配色（°C）。
    public static Color Temperature(int celsius) => Intensity(celsius, 65, 82);

    private readonly record struct Hsb(double H, double S, double B);
    private static readonly Hsb GreenHsb = new(0.227, 1.00, 0.725);   // ≈ #76B900
    private static readonly Hsb YellowHsb = new(0.120, 0.97, 0.949);  // ≈ #F2B007
    private static readonly Hsb RedHsb = new(0.012, 0.80, 0.886);     // ≈ #E23B2E

    /// value ≤ light → 绿；light…heavy → 绿黄红连续过渡；value ≥ heavy → 红。
    private static Color Intensity(double value, double light, double heavy)
    {
        var t = Math.Min(1, Math.Max(0, (value - light) / (heavy - light)));
        var hsb = t < 0.5
            ? Lerp(GreenHsb, YellowHsb, t / 0.5)
            : Lerp(YellowHsb, RedHsb, (t - 0.5) / 0.5);
        return FromHsb(hsb.H, hsb.S, hsb.B);
    }

    private static Hsb Lerp(Hsb a, Hsb b, double t) =>
        new(a.H + (b.H - a.H) * t, a.S + (b.S - a.S) * t, a.B + (b.B - a.B) * t);

    /// h,s,b 均为 0…1，与 SwiftUI Color(hue:saturation:brightness:) 一致。
    public static Color FromHsb(double h, double s, double b)
    {
        h = (h % 1 + 1) % 1;
        double r, g, bl;
        if (s <= 0)
        {
            r = g = bl = b;
        }
        else
        {
            var hh = h * 6;
            var i = (int)Math.Floor(hh) % 6;
            var f = hh - Math.Floor(hh);
            var p = b * (1 - s);
            var q = b * (1 - s * f);
            var tt = b * (1 - s * (1 - f));
            switch (i)
            {
                case 0: r = b; g = tt; bl = p; break;
                case 1: r = q; g = b; bl = p; break;
                case 2: r = p; g = b; bl = tt; break;
                case 3: r = p; g = q; bl = b; break;
                case 4: r = tt; g = p; bl = b; break;
                default: r = b; g = p; bl = q; break;
            }
        }
        return Color.FromArgb(Clamp(r), Clamp(g), Clamp(bl));
    }

    private static int Clamp(double v) => Math.Min(255, Math.Max(0, (int)Math.Round(v * 255)));

    // MARK: - 面板配色（随系统深/浅色）

    public sealed record Palette(
        Color Background, Color Text, Color Secondary, Color Tertiary,
        Color Track, Color Divider, Color HoverBg);

    public static Palette PaletteFor(bool dark) => dark
        ? new Palette(
            Background: Color.FromArgb(0x26, 0x26, 0x28),
            Text: Color.FromArgb(0xF2, 0xF2, 0xF2),
            Secondary: Color.FromArgb(0x9A, 0x9A, 0x9E),
            Tertiary: Color.FromArgb(0x6E, 0x6E, 0x73),
            Track: Color.FromArgb(0x3A, 0x3A, 0x3D),
            Divider: Color.FromArgb(0x3A, 0x3A, 0x3D),
            HoverBg: Color.FromArgb(0x33, 0x76, 0xB9, 0x00))   // 绿色 10% 透明
        : new Palette(
            Background: Color.White,
            Text: Color.FromArgb(0x1A, 0x1A, 0x1A),
            Secondary: Color.FromArgb(0x6E, 0x6E, 0x73),
            Tertiary: Color.FromArgb(0xA0, 0xA0, 0xA5),
            Track: Color.FromArgb(0xE2, 0xE2, 0xE4),
            Divider: Color.FromArgb(0xE2, 0xE2, 0xE4),
            HoverBg: Color.FromArgb(0x22, 0x76, 0xB9, 0x00));
}
