import AppKit
import SwiftUI

/// 菜单栏应用（LSUIElement）按需弹出的设置窗口。
/// 打开时临时切到 .regular 以获得可聚焦的窗口，关闭后切回 .accessory（仅菜单栏）。
@MainActor
final class SettingsWindowManager {
    static let shared = SettingsWindowManager()
    private var window: NSWindow?

    func show(state: AppState) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)

        if let window {
            window.makeKeyAndOrderFront(nil)
            return
        }

        let hosting = NSHostingController(rootView: SettingsView().environmentObject(state))
        let win = NSWindow(contentViewController: hosting)
        win.title = "GPU 状态 · 设置"
        win.styleMask = [.titled, .closable]
        win.isReleasedWhenClosed = false
        win.center()
        window = win
        win.makeKeyAndOrderFront(nil)
    }
}
