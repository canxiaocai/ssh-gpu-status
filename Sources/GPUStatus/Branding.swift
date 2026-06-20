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
}
