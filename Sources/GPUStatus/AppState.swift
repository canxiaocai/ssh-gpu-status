import Foundation
import SwiftUI
import ServiceManagement
import AppKit
import Network

@MainActor
final class AppState: ObservableObject {
    /// 用户管理的服务器列表（有序：决定面板里的展示顺序）。
    @Published var servers: [ServerConfig] = [] {
        didSet { persistServers(); pruneStatuses() }
    }

    // 展示指标开关
    @Published var showUtilization = true { didSet { defaults.set(showUtilization, forKey: Keys.showUtil) } }
    @Published var showMemory = true { didSet { defaults.set(showMemory, forKey: Keys.showMem) } }
    @Published var showTemperature = true { didSet { defaults.set(showTemperature, forKey: Keys.showTemp) } }
    @Published var menuBarShowsUtilization = true { didSet { defaults.set(menuBarShowsUtilization, forKey: Keys.menuBarUtil) } }

    /// 关闭面板时的慢速刷新间隔（秒）。
    @Published var refreshInterval: Double = 10 {
        didSet {
            guard refreshInterval != oldValue else { return }
            defaults.set(refreshInterval, forKey: Keys.refreshInterval)
            startPolling()
        }
    }

    /// 打开面板时的快速刷新间隔（秒）。
    @Published var fastInterval: Double = 1 {
        didSet {
            guard fastInterval != oldValue else { return }
            defaults.set(fastInterval, forKey: Keys.fastInterval)
            if isPanelOpen { startPolling() }
        }
    }

    /// 开机自启动（登录项）。
    @Published var launchAtLogin: Bool = (SMAppService.mainApp.status == .enabled)

    /// 每台服务器的最新状态，按 server id 索引。
    @Published private(set) var statusByServer: [UUID: HostStatus] = [:]

    private let defaults = UserDefaults.standard
    private var pollingTask: Task<Void, Never>?

    /// 每台服务器上一次的网络累计计数与采样时刻，用于求两次轮询间的收发速率。
    private var prevNet: [UUID: (counters: NetCounters, at: Date)] = [:]

    /// 睡眠唤醒监听 token；网络可达性监听。两者在「断后恢复」时主动立即刷新，避免盯一屏陈旧/全红数据。
    private var wakeObserver: NSObjectProtocol?
    private let pathMonitor = NWPathMonitor()
    private var wasNetworkSatisfied = true

    /// 用户从列表里删掉的 config 别名，合并时跳过它们（避免重新读取/重启又冒出来）。
    private var ignoredAliases: Set<String> = []

    private enum Keys {
        static let servers = "servers"
        static let ignoredAliases = "ignoredAliases"
        static let showUtil = "showUtilization"
        static let showMem = "showMemory"
        static let showTemp = "showTemperature"
        static let menuBarUtil = "menuBarShowsUtilization"
        static let refreshInterval = "refreshInterval"
        static let fastInterval = "fastInterval"
        static let legacyEnabledHosts = "enabledHosts"
        static let legacySelectedHost = "selectedHost"
    }

    init() {
        showUtilization = boolDefault(Keys.showUtil, true)
        showMemory = boolDefault(Keys.showMem, true)
        showTemperature = boolDefault(Keys.showTemp, true)
        menuBarShowsUtilization = boolDefault(Keys.menuBarUtil, true)

        let storedInterval = defaults.double(forKey: Keys.refreshInterval)
        refreshInterval = storedInterval > 0 ? storedInterval : 10
        let storedFast = defaults.double(forKey: Keys.fastInterval)
        fastInterval = storedFast > 0 ? storedFast : 1

        ignoredAliases = Set(defaults.stringArray(forKey: Keys.ignoredAliases) ?? [])
        servers = Self.loadServers(defaults: defaults, ignored: ignoredAliases)
        persistServers() // 写回迁移/合并结果

        startPolling()
        observeWakeAndNetwork()
    }

    deinit {
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        pathMonitor.cancel()
    }

    /// 睡眠唤醒、网络从断到通后各立即刷新一次（重启轮询循环 = 立刻拉一轮并重置节奏）。
    private func observeWakeAndNetwork() {
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.startPolling() }
        }

        pathMonitor.pathUpdateHandler = { [weak self] path in
            let satisfied = (path.status == .satisfied)
            Task { @MainActor in
                guard let self else { return }
                if satisfied && !self.wasNetworkSatisfied { self.startPolling() }
                self.wasNetworkSatisfied = satisfied
            }
        }
        pathMonitor.start(queue: DispatchQueue(label: "com.cxc.gpustatus.netmonitor"))
    }

    // MARK: - Derived

    /// 可见（要监控）的服务器，按用户顺序。
    var visibleServers: [ServerConfig] { servers.filter(\.isVisible) }

    var orderedStatuses: [HostStatus] {
        visibleServers.map { statusByServer[$0.id] ?? HostStatus(serverID: $0.id, name: $0.name) }
    }

    var metricVisibility: MetricVisibility {
        MetricVisibility(utilization: showUtilization, memory: showMemory, temperature: showTemperature)
    }

    /// 所有可见服务器上 GPU 的最高利用率，用于菜单栏摘要。
    var maxUtilization: Int? {
        let visibleIDs = Set(visibleServers.map(\.id))
        return statusByServer
            .filter { visibleIDs.contains($0.key) }
            .values.flatMap(\.gpus).compactMap(\.utilization).max()
    }

    // MARK: - Server management

    /// 切换某台服务器在面板里的显隐。
    func toggleVisible(_ id: UUID) {
        guard let idx = servers.firstIndex(where: { $0.id == id }) else { return }
        servers[idx].isVisible.toggle()
        if servers[idx].isVisible { Task { await refreshAll() } }
    }

    func moveServers(from source: IndexSet, to destination: Int) {
        servers.move(fromOffsets: source, toOffset: destination)
    }

    /// 从列表删除：自定义项直接删；config 项记入 ignored，避免合并时又冒出来。
    func deleteServer(_ id: UUID) {
        guard let server = servers.first(where: { $0.id == id }) else { return }
        if case .sshConfig(let alias) = server.source {
            ignoredAliases.insert(alias)
            defaults.set(Array(ignoredAliases), forKey: Keys.ignoredAliases)
        }
        servers.removeAll { $0.id == id }
    }

    func addCustomServer(name: String, user: String, host: String, port: Int, identityFile: String?) {
        let key = identityFile?.trimmingCharacters(in: .whitespaces)
        let conn = CustomConnection(user: user, host: host, port: port,
                                    identityFile: (key?.isEmpty == false) ? key : nil)
        let display = name.trimmingCharacters(in: .whitespaces).isEmpty ? host : name
        servers.append(ServerConfig(name: display, source: .custom(conn), isVisible: true))
        Task { await refreshAll() }
    }

    /// 重新读取 ~/.ssh/config：清空 ignored（恢复此前删掉的 config 项），再与现有列表合并。
    func reloadHosts() {
        ignoredAliases.removeAll()
        defaults.set(Array(ignoredAliases), forKey: Keys.ignoredAliases)
        servers = Self.mergeWithConfig(servers, ignored: ignoredAliases)
    }

    // MARK: - Polling cadence

    private var isPanelOpen = false
    private var effectiveInterval: Double { isPanelOpen ? fastInterval : refreshInterval }

    func panelDidOpen() {
        guard !isPanelOpen else { return }
        isPanelOpen = true
        startPolling() // 立即刷新一次并切到快速节奏
    }

    func panelDidClose() {
        guard isPanelOpen else { return }
        isPanelOpen = false
    }

    func startPolling() {
        pollingTask?.cancel()
        pollingTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.refreshAll()
                try? await Task.sleep(for: .seconds(self.effectiveInterval))
            }
        }
    }

    /// 并发拉取所有可见服务器的 GPU 状态，每台结果到达即更新。
    func refreshAll() async {
        let targets = visibleServers
        guard !targets.isEmpty else { return }
        for s in targets {
            statusByServer[s.id, default: HostStatus(serverID: s.id, name: s.name)].isLoading = true
        }

        await withTaskGroup(of: (UUID, Result<FetchResult, Error>).self) { group in
            for s in targets {
                group.addTask {
                    do { return (s.id, .success(try await GPUMonitor.fetch(server: s))) }
                    catch { return (s.id, .failure(error)) }
                }
            }
            for await (id, result) in group {
                guard let server = servers.first(where: { $0.id == id }), server.isVisible else { continue }
                var s = statusByServer[id] ?? HostStatus(serverID: id, name: server.name)
                s.isLoading = false
                switch result {
                case .success(let fetched):
                    s.gpus = fetched.gpus
                    s.errorMessage = nil
                    s.lastUpdated = Date()
                    updateNetRate(&s, id: id, counters: fetched.net)
                case .failure(let err):
                    if !(err is CancellationError) {
                        s.gpus = []
                        s.errorMessage = err.localizedDescription
                        // 抓取失败：清掉速率与基线，避免在错误旁残留陈旧速度，恢复后从新样本重新起算。
                        s.netRxBytesPerSec = nil
                        s.netTxBytesPerSec = nil
                        prevNet[id] = nil
                        Log.write("fetch \(server.name) failed: \(err.localizedDescription)")
                    }
                }
                statusByServer[id] = s
            }
        }
    }

    /// 用本次累计计数与上次样本求收发速率（B/s）；首次无样本、间隔过短或计数器回绕（重启）时
    /// 不刷新显示值（保留上一次速率，避免闪烁），并把本次作为新基线。
    private func updateNetRate(_ status: inout HostStatus, id: UUID, counters: NetCounters?) {
        guard let counters else { return }
        let now = Date()
        defer { prevNet[id] = (counters, now) }

        guard let prev = prevNet[id] else { return }
        let dt = now.timeIntervalSince(prev.at)
        let drx = counters.rxBytes - prev.counters.rxBytes
        let dtx = counters.txBytes - prev.counters.txBytes
        guard dt > 0.2, drx >= 0, dtx >= 0 else { return }
        status.netRxBytesPerSec = Double(drx) / dt
        status.netTxBytesPerSec = Double(dtx) / dt
    }

    // MARK: - Launch at login

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
        } catch {
            Log.write("launchAtLogin \(enabled ? "register" : "unregister") failed: \(error.localizedDescription)")
        }
        launchAtLogin = (SMAppService.mainApp.status == .enabled)
    }

    // MARK: - Persistence

    private func persistServers() {
        if let data = try? JSONEncoder().encode(servers) {
            defaults.set(data, forKey: Keys.servers)
        }
    }

    /// 加载服务器列表：优先读已存 JSON 并与 config 合并；否则从旧版 enabledHosts 迁移。
    private static func loadServers(defaults: UserDefaults, ignored: Set<String>) -> [ServerConfig] {
        if let data = defaults.data(forKey: Keys.servers),
           let decoded = try? JSONDecoder().decode([ServerConfig].self, from: data) {
            return mergeWithConfig(decoded, ignored: ignored)
        }
        // 迁移：旧版用 enabledHosts(Set) + config 顺序。
        let aliases = SSHConfig.parseAliases()
        let enabled: Set<String>
        if let arr = defaults.array(forKey: Keys.legacyEnabledHosts) as? [String] {
            enabled = Set(arr)
        } else if let legacy = defaults.string(forKey: Keys.legacySelectedHost) {
            enabled = [legacy]
        } else {
            enabled = Set(aliases) // 默认全部可见
        }
        return aliases.map {
            ServerConfig(name: $0, source: .sshConfig(alias: $0), isVisible: enabled.contains($0))
        }
    }

    /// 把当前 config 别名合并进已有列表：删掉消失的 config 项，追加新别名（跳过 ignored），保留自定义项与顺序。
    private static func mergeWithConfig(_ existing: [ServerConfig], ignored: Set<String>) -> [ServerConfig] {
        let aliases = SSHConfig.parseAliases()
        let aliasSet = Set(aliases)

        var result = existing.filter { server in
            if case .sshConfig(let alias) = server.source { return aliasSet.contains(alias) }
            return true // 自定义项始终保留
        }

        let known = Set(result.compactMap { server -> String? in
            if case .sshConfig(let alias) = server.source { return alias }
            return nil
        })
        for alias in aliases where !known.contains(alias) && !ignored.contains(alias) {
            result.append(ServerConfig(name: alias, source: .sshConfig(alias: alias), isVisible: true))
        }
        return result
    }

    // MARK: - Helpers

    private func boolDefault(_ key: String, _ fallback: Bool) -> Bool {
        defaults.object(forKey: key) == nil ? fallback : defaults.bool(forKey: key)
    }

    private func pruneStatuses() {
        let keep = Set(servers.map(\.id))
        statusByServer = statusByServer.filter { keep.contains($0.key) }
        prevNet = prevNet.filter { keep.contains($0.key) }
    }
}
