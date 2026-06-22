import SwiftUI
import AppKit
import Combine

@main
struct GPUStatusApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        // 菜单栏项与弹层都由 AppDelegate 直接管理（见下方说明），这里不需要常规窗口。
        // 用一个空的 Settings 场景占位，它不会自动弹窗。
        Settings { EmptyView() }
    }
}

/// 自己管理 `NSStatusItem`，而不用 SwiftUI 的 `MenuBarExtra`。
///
/// 原因：`MenuBarExtra` 的 label 只在首次（启动时还没数据、util 为 nil）按「只有 logo」测量并渲染一次，
/// 之后即便 label 视图重绘、合成图换成「logo + 利用率」，状态栏项也不会更新——表现为永远只显示 logo。
/// 直接持有 `NSStatusItem` 并自行设置 `button.image`，是动态菜单栏标题的标准做法，刷新可靠可控。
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSPopoverDelegate {
    private let state = AppState()
    private var statusItem: NSStatusItem?
    private let popover = NSPopover()
    private var cancellable: AnyCancellable?

    /// 上一次画到菜单栏的利用率值；用双层 optional 区分「从未渲染」与「渲染过 nil」，避免重复重画。
    private var lastUtilization: Int??

    func applicationDidFinishLaunching(_ notification: Notification) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(togglePopover)
        statusItem = item
        refreshButtonImage(force: true)

        popover.behavior = .transient
        popover.delegate = self
        popover.contentViewController = NSHostingController(rootView: MenuView(state: state))

        // state 任意变化（轮询到新数据、切换开关）都可能改变要显示的最高利用率，借此刷新菜单栏标题。
        // objectWillChange 在赋值前触发，故 receive(on: main) 推迟到下一轮 runloop 再读已更新的值。
        cancellable = state.objectWillChange
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in
                Task { @MainActor in self?.refreshButtonImage(force: false) }
            }
    }

    /// 按当前开关与数据重画菜单栏图标；值没变则跳过（force 用于首次强制渲染）。
    private func refreshButtonImage(force: Bool) {
        let util = state.menuBarShowsUtilization ? state.maxUtilization : nil
        if !force, lastUtilization == .some(util) { return }
        lastUtilization = .some(util)
        statusItem?.button?.image = Branding.menuBarImage(utilization: util)
    }

    @objc private func togglePopover() {
        guard let button = statusItem?.button else { return }
        if popover.isShown {
            popover.performClose(nil)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    func popoverDidClose(_ notification: Notification) {
        state.panelDidClose()
    }
}
