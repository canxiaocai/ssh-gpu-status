import SwiftUI

@main
struct GPUStatusApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        MenuBarExtra {
            MenuView(state: state)
        } label: {
            Label {
                Text(labelText)
            } icon: {
                Image(nsImage: Branding.logo)
            }
        }
        .menuBarExtraStyle(.window)
    }

    /// 菜单栏标题：开启摘要且有数据时显示最高利用率，否则只显示图标。
    private var labelText: String {
        if state.menuBarShowsUtilization, let util = state.maxUtilization {
            return "\(util)%"
        }
        return ""
    }
}
