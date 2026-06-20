import SwiftUI
import AppKit

struct SettingsView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        TabView {
            GeneralSettings()
                .tabItem { Label("通用", systemImage: "gearshape") }
            ServersSettings()
                .tabItem { Label("服务器", systemImage: "server.rack") }
            DisplaySettings()
                .tabItem { Label("显示", systemImage: "slider.horizontal.3") }
            AboutSettings()
                .tabItem { Label("关于", systemImage: "info.circle") }
        }
        .frame(width: 520, height: 580)
        .onDisappear {
            // 设置窗口关闭后回到仅菜单栏状态（隐藏 Dock 图标）。
            NSApp.setActivationPolicy(.accessory)
        }
    }
}

// MARK: - 通用的设置控件样式

/// 一屏可滚动内容，统一边距。
private struct SettingsScroll<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) { content }
                .padding(.horizontal, 28)
                .padding(.vertical, 22)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// 灰色小节标题。
private struct SectionHeader: View {
    let title: String
    init(_ title: String) { self.title = title }
    var body: some View {
        Text(title)
            .font(.system(size: 13))
            .foregroundStyle(.secondary)
            .padding(.bottom, 6)
    }
}

/// 标题 + 描述 + 右侧控件 的一行。
private struct SettingRow<Control: View>: View {
    let title: String
    let description: String?
    @ViewBuilder var control: Control

    init(_ title: String, description: String? = nil, @ViewBuilder control: () -> Control) {
        self.title = title
        self.description = description
        self.control = control()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 12) {
                Text(title).font(.system(size: 14))
                Spacer(minLength: 16)
                control
            }
            if let description {
                Text(description)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// 勾选框 + 标题，下方灰色描述。
private struct ToggleRow: View {
    let title: String
    let description: String?
    @Binding var isOn: Bool

    init(_ title: String, description: String? = nil, isOn: Binding<Bool>) {
        self.title = title
        self.description = description
        self._isOn = isOn
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Toggle(isOn: $isOn) { Text(title).font(.system(size: 14)) }
                .toggleStyle(.checkbox)
            if let description {
                Text(description)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct SectionDivider: View {
    var body: some View { Divider().padding(.vertical, 14) }
}

// MARK: - 通用

struct GeneralSettings: View {
    @EnvironmentObject var state: AppState

    private let fastOptions: [(String, Double)] = [("0.5 秒", 0.5), ("1 秒", 1), ("2 秒", 2), ("3 秒", 3)]
    private let slowOptions: [(String, Double)] = [("5 秒", 5), ("10 秒", 10), ("30 秒", 30), ("60 秒", 60)]

    var body: some View {
        VStack(spacing: 0) {
            SettingsScroll {
                SectionHeader("启动")
                ToggleRow("开机自动启动",
                          description: "登录 Mac 时自动启动 GPU 状态。",
                          isOn: Binding(get: { state.launchAtLogin },
                                        set: { state.setLaunchAtLogin($0) }))

                SectionDivider()

                SectionHeader("刷新频率")
                SettingRow("打开面板时（快）", description: "面板打开时的刷新间隔，越短越实时。") {
                    Picker("", selection: $state.fastInterval) {
                        ForEach(fastOptions, id: \.1) { Text($0.0).tag($0.1) }
                    }
                    .labelsHidden().frame(width: 110)
                }
                SettingRow("关闭面板时（慢）", description: "面板关闭后后台轮询的间隔，越长越省资源。") {
                    Picker("", selection: $state.refreshInterval) {
                        ForEach(slowOptions, id: \.1) { Text($0.0).tag($0.1) }
                    }
                    .labelsHidden().frame(width: 110)
                }
            }

            Divider()
            HStack {
                Spacer()
                Button("退出 GPU 状态") { NSApplication.shared.terminate(nil) }
                    .buttonStyle(.borderedProminent)
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 14)
        }
    }
}

// MARK: - 显示

struct DisplaySettings: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        SettingsScroll {
            SectionHeader("展示指标")
            ToggleRow("GPU 利用率", description: "显示每块 GPU 的计算利用率。", isOn: $state.showUtilization)
            ToggleRow("显存占用", description: "显示已用 / 总显存。", isOn: $state.showMemory)
            ToggleRow("温度", description: "显示每块 GPU 的温度。", isOn: $state.showTemperature)

            SectionDivider()

            SectionHeader("菜单栏")
            ToggleRow("显示最高 GPU 利用率",
                      description: "菜单栏图标旁显示所有可见服务器中的最高利用率。",
                      isOn: $state.menuBarShowsUtilization)
        }
    }
}

// MARK: - 服务器

struct ServersSettings: View {
    @EnvironmentObject var state: AppState
    @State private var showingAdd = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                SectionHeader("监控的服务器")
                Text("眼睛在面板显示/隐藏 · 拖动排序 · 🗑 从列表移除。")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 28)
            .padding(.top, 22)
            .padding(.bottom, 8)

            if state.servers.isEmpty {
                Spacer()
                Text("还没有服务器。从 ~/.ssh/config 读取，或手动添加。")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                Spacer()
            } else {
                List {
                    ForEach(state.servers) { server in
                        ServerRow(server: server)
                            .listRowSeparator(.hidden)
                    }
                    .onMove { state.moveServers(from: $0, to: $1) }
                }
                .listStyle(.inset)
                .scrollContentBackground(.hidden)
            }

            Divider()
            HStack(spacing: 8) {
                Button { showingAdd = true } label: { Label("添加服务器", systemImage: "plus") }
                Button { state.reloadHosts() } label: { Label("重新读取 config", systemImage: "arrow.clockwise") }
                Spacer()
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
        }
        .sheet(isPresented: $showingAdd) { AddServerView() }
    }
}

struct ServerRow: View {
    @EnvironmentObject var state: AppState
    let server: ServerConfig

    var body: some View {
        HStack(spacing: 12) {
            Button { state.toggleVisible(server.id) } label: {
                Image(systemName: server.isVisible ? "eye.fill" : "eye.slash")
                    .foregroundStyle(server.isVisible ? Theme.green : .secondary)
            }
            .buttonStyle(.borderless)
            .frame(width: 22)
            .help(server.isVisible ? "在面板中隐藏" : "在面板中显示")

            Image(systemName: server.isCustom ? "desktopcomputer" : "server.rack")
                .foregroundStyle(.secondary)
                .frame(width: 22)

            VStack(alignment: .leading, spacing: 1) {
                Text(server.name).fontWeight(.medium)
                Text(server.detail)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }

            Spacer(minLength: 8)

            Button(role: .destructive) {
                state.deleteServer(server.id)
            } label: {
                Image(systemName: "trash").foregroundStyle(.secondary)
            }
            .buttonStyle(.borderless)
            .frame(width: 22)
            .help(server.isCustom ? "删除自定义服务器" : "从列表移除（“重新读取 config”可恢复）")

            Image(systemName: "line.3.horizontal")
                .foregroundStyle(.tertiary)
                .frame(width: 20)
        }
        .padding(.vertical, 5)
        .opacity(server.isVisible ? 1 : 0.5)
    }
}

// MARK: - 添加自定义服务器

struct AddServerView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss

    @State private var name = ""
    @State private var user = ""
    @State private var host = ""
    @State private var port = "22"
    @State private var identityFile = ""

    private var canSave: Bool {
        !user.trimmingCharacters(in: .whitespaces).isEmpty &&
        !host.trimmingCharacters(in: .whitespaces).isEmpty &&
        Int(port) != nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("添加服务器").font(.headline).padding([.top, .horizontal], 16)

            Form {
                TextField("名称（可选）", text: $name, prompt: Text("如 lab-a100"))
                TextField("用户名", text: $user, prompt: Text("如 root"))
                TextField("主机", text: $host, prompt: Text("IP 或域名"))
                TextField("端口", text: $port, prompt: Text("22"))
                HStack {
                    TextField("身份文件（可选）", text: $identityFile, prompt: Text("~/.ssh/id_ed25519"))
                    Button("选择…") {
                        if let path = Self.chooseIdentityFile() { identityFile = path }
                    }
                }
            }
            .formStyle(.grouped)

            Text("仅支持密钥/身份文件认证。留空则用 ssh-agent 或默认密钥。")
                .font(.caption).foregroundStyle(.secondary)
                .padding(.horizontal, 16)

            Divider().padding(.top, 8)
            HStack {
                Spacer()
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("添加") {
                    state.addCustomServer(
                        name: name, user: user, host: host,
                        port: Int(port) ?? 22,
                        identityFile: identityFile
                    )
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!canSave)
            }
            .padding(12)
        }
        .frame(width: 420)
    }

    private static func chooseIdentityFile() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.directoryURL = URL(fileURLWithPath: NSString(string: "~/.ssh").expandingTildeInPath)
        return panel.runModal() == .OK ? panel.url?.path : nil
    }
}

// MARK: - 关于

struct AboutSettings: View {
    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—"
    }

    var body: some View {
        VStack(spacing: 14) {
            Spacer()
            Image(nsImage: Branding.logo)
                .resizable().aspectRatio(contentMode: .fit)
                .frame(width: 64, height: 64)
                .foregroundStyle(Theme.green)
            Text("GPU 状态").font(.title2).bold()
            Text("版本 \(version)").font(.callout).foregroundStyle(.secondary)
            Text("通过 SSH 监控远程服务器的 GPU 利用率、显存、温度与进程。")
                .font(.callout).foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 340)
            Spacer()
            Button("退出 GPU 状态") { NSApplication.shared.terminate(nil) }
                .buttonStyle(.borderedProminent)
            Spacer().frame(height: 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(28)
    }
}
