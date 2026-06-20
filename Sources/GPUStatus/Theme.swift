import SwiftUI

/// 配色：以 NVIDIA 绿为主，负载升高时依次过渡到黄、红。
/// 颜色语义参考 nvitop（light=green / moderate=yellow / heavy=red），
/// 阈值上调以让「绿色」覆盖正常工作区间、成为主色，仅在接近打满时才转黄/红。
enum Theme {
    /// NVIDIA 品牌绿 #76B900
    static let green = Color(red: 0x76 / 255, green: 0xB9 / 255, blue: 0x00 / 255)
    /// 警告黄
    static let yellow = Color(red: 0xF2 / 255, green: 0xB0 / 255, blue: 0x07 / 255)
    /// 高负载红
    static let red = Color(red: 0xE2 / 255, green: 0x3B / 255, blue: 0x2E / 255)
    /// 进度条底槽
    static let track = Color.secondary.opacity(0.18)

    /// GPU 利用率配色（%）
    static func utilization(_ percent: Int) -> Color { intensity(Double(percent), light: 60, heavy: 85) }
    /// 显存占用配色（%）
    static func memory(_ percent: Int) -> Color { intensity(Double(percent), light: 60, heavy: 85) }
    /// 温度配色（°C）
    static func temperature(_ celsius: Int) -> Color { intensity(Double(celsius), light: 65, heavy: 82) }

    // 在 light…heavy 区间内连续插值（绿→黄→红），避免 84%↔85% 之类的硬跳变闪烁。
    private typealias HSB = (h: Double, s: Double, b: Double)
    private static let greenHSB: HSB = (0.227, 1.00, 0.725)   // ≈ #76B900
    private static let yellowHSB: HSB = (0.120, 0.97, 0.949)  // ≈ #F2B007
    private static let redHSB: HSB = (0.012, 0.80, 0.886)     // ≈ #E23B2E

    /// value ≤ light → 绿；light…heavy → 绿黄红连续过渡；value ≥ heavy → 红。
    private static func intensity(_ value: Double, light: Double, heavy: Double) -> Color {
        let t = min(1, max(0, (value - light) / (heavy - light)))
        let hsb = t < 0.5
            ? lerp(greenHSB, yellowHSB, t / 0.5)
            : lerp(yellowHSB, redHSB, (t - 0.5) / 0.5)
        return Color(hue: hsb.h, saturation: hsb.s, brightness: hsb.b)
    }

    private static func lerp(_ a: HSB, _ b: HSB, _ t: Double) -> HSB {
        (a.h + (b.h - a.h) * t, a.s + (b.s - a.s) * t, a.b + (b.b - a.b) * t)
    }
}
