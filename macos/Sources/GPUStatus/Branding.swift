import AppKit
import SwiftUI

/// 应用品牌图标（菜单栏 + 面板标题共用）。
/// 资源是模板图（黑 + alpha），设 `isTemplate = true` 后 macOS 会在菜单栏自动按明暗着色。
/// 资源缺失（如 `swift run` 未拷贝 Resources）时回退到系统 memorychip 图标。
enum Branding {
    static let logo: NSImage = {
        if let custom = NSImage(named: "gpu-pulse-template") {
            custom.isTemplate = true
            return custom
        }
        Log.write("warning: 未找到 gpu-pulse-template 图标资源，回退到 memorychip")
        let fallback = NSImage(systemSymbolName: "memorychip", accessibilityDescription: "GPU")
            ?? NSImage()
        fallback.isTemplate = true
        return fallback
    }()

    /// 菜单栏图标的目标高度（点）。菜单栏内容区约 18pt，固定它让带文字/不带文字两种状态大小一致。
    private static let menuBarHeight: CGFloat = 18

    /// 把 logo 和（可选的）利用率文字画进同一张模板图。
    ///
    /// `MenuBarExtra` 的 label 若同时给 `Image` 和 `Text`，AppKit 状态栏只会渲染图标、丢掉文字
    /// （表现为「只显示 logo」）。所以这里把两者合成单张图交给菜单栏——菜单栏总能完整渲染一张图。
    /// 整图设为模板图（`isTemplate`），由 macOS 按明暗自动着色，文字也随之适配。
    /// `utilization` 为 nil（关闭摘要或暂无数据）时只返回 logo 本身。
    static func menuBarImage(utilization: Int?) -> NSImage {
        guard let utilization else { return logo }

        let logo = logo
        let aspect = logo.size.height > 0 ? logo.size.width / logo.size.height : 1
        let logoWidth = menuBarHeight * aspect

        let text = "\(utilization)%" as NSString
        // 等宽数字：数值变化时整体宽度不抖动。前景用黑色，配合模板图由系统统一着色。
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .regular),
            .foregroundColor: NSColor.black,
        ]
        let textSize = text.size(withAttributes: attrs)
        let gap: CGFloat = 3
        let width = ceil(logoWidth + gap + textSize.width)

        let image = NSImage(size: NSSize(width: width, height: menuBarHeight), flipped: false) { _ in
            logo.draw(in: NSRect(x: 0, y: 0, width: logoWidth, height: menuBarHeight))
            text.draw(at: NSPoint(x: logoWidth + gap, y: (menuBarHeight - textSize.height) / 2),
                      withAttributes: attrs)
            return true
        }
        image.isTemplate = true
        return image
    }
}
