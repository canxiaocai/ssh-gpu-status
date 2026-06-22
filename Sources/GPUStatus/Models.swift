import Foundation

/// 远程在某块 GPU 上运行的计算进程。
struct GPUProcess: Identifiable, Sendable, Hashable {
    let pid: Int
    let user: String        // 占用用户（远程 ps 解析，未知为 "?"）
    let memoryUsed: Int     // 占用显存 MiB
    let name: String        // 进程名（可能是完整路径）
    let elapsedSeconds: Int? // 已运行时间（远程 ps etimes，单位秒；老旧 ps 不支持时为 nil）

    var id: Int { pid }

    /// 进程名的最后一段（去掉路径），用于窄面板展示。
    var shortName: String { (name as NSString).lastPathComponent }

    /// 已运行时间的紧凑文本，如 "3天5小时"、"5小时12分"、"12分30秒"、"30秒"；无数据为 nil。
    var runtimeText: String? {
        guard let s = elapsedSeconds, s >= 0 else { return nil }
        let day = s / 86400
        let hour = (s % 86400) / 3600
        let minute = (s % 3600) / 60
        let second = s % 60
        if day > 0 { return "\(day)天\(hour)小时" }
        if hour > 0 { return "\(hour)小时\(minute)分" }
        if minute > 0 { return "\(minute)分\(second)秒" }
        return "\(second)秒"
    }
}

/// 单块 GPU 的状态快照，对应 nvidia-smi 的一行查询结果。
struct GPUInfo: Identifiable, Sendable, Hashable {
    let index: Int          // GPU 序号
    let uuid: String        // GPU UUID（用于把进程映射到具体 GPU）
    let name: String        // 型号，如 "NVIDIA A100-SXM4-40GB"
    let utilization: Int?   // 利用率 %（MIG 实例等场景 nvidia-smi 返回 [N/A] → nil）
    let memoryUsed: Int     // 已用显存 MiB
    let memoryTotal: Int    // 总显存 MiB
    let temperature: Int?   // 温度 °C（被动散热卡可能为 [N/A] → nil）
    var processes: [GPUProcess] = []

    var id: Int { index }

    /// 这块卡是否有人在用：优先看进程；进程信息抓取失败时退回看显存占用，
    /// 避免「其实有人占着显存却显示空闲」的误导。
    var isBusy: Bool {
        if !processes.isEmpty { return true }
        return memoryUsed > 200
    }

    /// 显存占用比例 0...1
    var memoryFraction: Double {
        memoryTotal > 0 ? Double(memoryUsed) / Double(memoryTotal) : 0
    }

    /// 显存占用百分比（取整）
    var memoryPercent: Int { Int((memoryFraction * 100).rounded()) }

    /// 显存「已用/总量」的紧凑 GiB 文本，如 "22.7/48.0G"
    var memoryGiBText: String {
        String(format: "%.1f/%.0fG", Double(memoryUsed) / 1024, Double(memoryTotal) / 1024)
    }

    /// 去掉 "NVIDIA " 前缀的简短型号，便于在窄面板里显示。
    var shortName: String {
        name.replacingOccurrences(of: "NVIDIA ", with: "")
    }
}

/// 一台可监控的服务器：要么来自 ~/.ssh/config 的别名，要么是用户自定义的连接。
struct ServerConfig: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String                 // 展示名（config 用别名；自定义用用户取的名）
    var source: Source
    var isVisible: Bool = true        // 是否在面板显示并轮询（隐藏 = 不显示不轮询）

    enum Source: Codable, Hashable {
        case sshConfig(alias: String)     // 直接用 `ssh <alias>`
        case custom(CustomConnection)     // 用 user/host/port/key 自行拼接
    }

    var isCustom: Bool {
        if case .custom = source { return true }
        return false
    }

    /// 副标题：展示连接方式。
    var detail: String {
        switch source {
        case .sshConfig(let alias): return "~/.ssh/config · \(alias)"
        case .custom(let c):
            let key = (c.identityFile?.isEmpty == false) ? " · 🔑" : ""
            return "\(c.user)@\(c.host):\(c.port)\(key)"
        }
    }
}

/// 自定义服务器的连接信息（仅密钥/身份文件认证）。
struct CustomConnection: Codable, Hashable {
    var user: String
    var host: String
    var port: Int = 22
    var identityFile: String? = nil   // 私钥路径，可空（用 ssh-agent / 默认密钥）
}

/// 网络收发的累计字节计数（取自远程 /proc/net/dev），用于在两次轮询间求速率。
struct NetCounters: Sendable, Hashable {
    let rxBytes: Int   // 累计下行字节
    let txBytes: Int   // 累计上行字节
}

/// 一次远程抓取的完整结果：GPU 列表 + 可选的网络计数。
struct FetchResult: Sendable {
    let gpus: [GPUInfo]
    let net: NetCounters?
}

/// 单台服务器的实时监控状态。
struct HostStatus: Identifiable, Sendable {
    let serverID: UUID
    let name: String
    var gpus: [GPUInfo] = []
    var errorMessage: String? = nil
    var isLoading: Bool = false
    var lastUpdated: Date? = nil
    var netRxBytesPerSec: Double? = nil   // 下行速率 B/s（首次轮询无上一次样本时为 nil）
    var netTxBytesPerSec: Double? = nil   // 上行速率 B/s

    var id: UUID { serverID }
}

/// 网络速率的紧凑文本格式化（1024 进制，B/s · KB/s · MB/s · GB/s）。
enum NetFormat {
    static func speed(_ bytesPerSec: Double) -> String {
        let v = max(0, bytesPerSec)
        let kb = 1024.0, mb = kb * 1024, gb = mb * 1024
        if v >= gb { return String(format: "%.1f GB/s", v / gb) }
        if v >= mb { return String(format: "%.1f MB/s", v / mb) }
        if v >= kb { return String(format: "%.0f KB/s", v / kb) }
        return String(format: "%.0f B/s", v)
    }
}

/// 用户在设置里选择展示哪些指标。
struct MetricVisibility: Sendable {
    var utilization: Bool
    var memory: Bool
    var temperature: Bool
}
