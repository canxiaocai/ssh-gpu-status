import Foundation

enum GPUError: LocalizedError {
    case sshFailed(code: Int32, message: String)
    case noOutput
    case parse(String)

    var errorDescription: String? {
        switch self {
        case .sshFailed(let code, let message):
            return "SSH 连接失败 (code \(code))：\(message)"
        case .noOutput:
            return "服务器未返回 GPU 数据（nvidia-smi 是否可用？）"
        case .parse(let line):
            return "无法解析 nvidia-smi 输出：\(line)"
        }
    }
}

/// 负责通过 SSH 在远程执行 nvidia-smi 并解析输出。
enum GPUMonitor {
    private static let procMarker = "@@@PROC@@@"
    private static let netMarker = "@@@NET@@@"
    private static let netDevMarker = "@@@NETDEV@@@"

    /// 远程命令：先查每块 GPU（含 uuid 用于关联进程），再查计算进程并用 `ps` 补上占用用户与已运行时间，
    /// 最后采一次网络计数（默认路由网卡名 + /proc/net/dev 原文，速率在客户端按两次轮询差值算）。
    /// 进程 / 网络部分均 best-effort：失败也不影响 GPU 数据（`|| true`）。
    /// user 与 etimes 分两次 `ps` 取，避免老旧 ps 不支持 etimes 时连累用户列。
    private static let remoteCommand = """
    nvidia-smi --query-gpu=index,uuid,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits || exit $?
    echo '\(procMarker)'
    { nvidia-smi --query-compute-apps=gpu_uuid,pid,used_memory,process_name --format=csv,noheader,nounits | while IFS=',' read -r uuid pid mem pname; do pid=$(echo "$pid" | tr -d ' '); u=$(ps -o user= -p "$pid" 2>/dev/null | tr -d ' '); et=$(ps -o etimes= -p "$pid" 2>/dev/null | tr -d ' '); echo "${uuid},${pid},${mem},${pname},${u},${et}"; done; } 2>/dev/null || true
    echo '\(netMarker)'
    { ip route show default 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="dev") print $(i+1)}'; echo '\(netDevMarker)'; cat /proc/net/dev 2>/dev/null; } 2>/dev/null || true
    """

    static func fetch(server: ServerConfig, connectTimeout: Int = 10) async throws -> FetchResult {
        let args = sshArgs(for: server, connectTimeout: connectTimeout) + [remoteCommand]
        let result = try await runProcess(launchPath: "/usr/bin/ssh", arguments: args)
        guard result.exitCode == 0 else {
            let stderr = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            let stdout = result.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
            throw GPUError.sshFailed(code: result.exitCode, message: stderr.isEmpty ? stdout : stderr)
        }
        return try parse(result.stdout)
    }

    /// 按服务器来源拼 ssh 参数。
    static func sshArgs(for server: ServerConfig, connectTimeout: Int) -> [String] {
        var args = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=\(connectTimeout)"]
        switch server.source {
        case .sshConfig(let alias):
            args.append(alias)
        case .custom(let c):
            args += ["-p", "\(c.port)"]
            if let key = c.identityFile, !key.isEmpty {
                args += ["-i", (key as NSString).expandingTildeInPath]
            }
            // 自定义服务器首次连接自动接受主机指纹，避免卡在 known_hosts 确认。
            args += ["-o", "StrictHostKeyChecking=accept-new"]
            args.append("\(c.user)@\(c.host)")
        }
        return args
    }

    static func parse(_ output: String) throws -> FetchResult {
        let parts = output.components(separatedBy: procMarker)
        let gpuPart = parts[0]
        // procMarker 之后是「进程段 + 网络段」，再按 netMarker 拆开。
        let afterGPU = parts.count > 1 ? parts[1] : ""
        let procAndNet = afterGPU.components(separatedBy: netMarker)
        let procPart = procAndNet[0]
        let netPart = procAndNet.count > 1 ? procAndNet[1] : ""

        var gpus: [GPUInfo] = []
        var indexByUUID: [String: Int] = [:]
        for rawLine in gpuPart.split(separator: "\n") {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }

            let cols = line.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            // index、uuid、显存是核心字段，缺失/无法解析就跳过该行（不再 throw 连累整台）；
            // 利用率、温度宽容处理（MIG 实例、被动散热卡常返回 [N/A] → nil）。
            guard cols.count >= 7, let index = Int(cols[0]) else {
                Log.write("parse: 跳过无法识别的 GPU 行: \(line)")
                continue
            }
            let uuid = cols[1]
            guard !uuid.isEmpty else {
                Log.write("parse: 跳过缺少 uuid 的 GPU 行: \(line)")
                continue
            }
            guard let memUsed = Int(cols[4]), let memTotal = Int(cols[5]) else {
                Log.write("parse: 跳过显存异常的 GPU 行: \(line)")
                continue
            }

            indexByUUID[uuid] = index
            gpus.append(GPUInfo(
                index: index,
                uuid: uuid,
                name: cols[2],
                utilization: Int(cols[3]),   // [N/A] → nil
                memoryUsed: memUsed,
                memoryTotal: memTotal,
                temperature: Int(cols[6])    // [N/A] → nil
            ))
        }

        if gpus.isEmpty { throw GPUError.noOutput }

        // 把进程按 uuid 归到对应 GPU。
        var procsByIndex: [Int: [GPUProcess]] = [:]
        for rawLine in procPart.split(separator: "\n") {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            let cols = line.split(separator: ",", omittingEmptySubsequences: false)
                .map { $0.trimmingCharacters(in: .whitespaces) }
            guard cols.count >= 6, let index = indexByUUID[cols[0]], let pid = Int(cols[1]) else { continue }
            // 进程名可能含逗号（带参数的路径 / python 脚本名 / java 主类）。末两列由远端 ps 追加且不含逗号：
            // 倒数第一列 = etimes（已运行秒数），倒数第二列 = user；固定取前 3 列 + 末 2 列，
            // 中间所有列用逗号 join 还原进程名，避免「用户 / 显存 / 时间列被冲乱」。
            let elapsed = Int(cols[cols.count - 1])      // etimes 秒；空（不支持/读取失败）→ nil
            let user = cols[cols.count - 2]
            let pname = cols[3..<(cols.count - 2)].joined(separator: ",")
            let proc = GPUProcess(
                pid: pid,
                user: user.isEmpty ? "?" : user,
                memoryUsed: Int(cols[2]) ?? 0,
                name: pname,
                elapsedSeconds: elapsed
            )
            procsByIndex[index, default: []].append(proc)
        }
        for i in gpus.indices {
            gpus[i].processes = (procsByIndex[gpus[i].index] ?? []).sorted { $0.memoryUsed > $1.memoryUsed }
        }

        return FetchResult(gpus: gpus, net: parseNet(netPart))
    }

    // MARK: - 网络计数解析

    /// 网络段 = 默认路由网卡名（每行一个）+ netDevMarker + /proc/net/dev 原文。
    /// 优先只统计默认路由网卡（即真正的上行口，避开 docker/veth 等虚拟口与回环口的重复计数）；
    /// 拿不到默认网卡时退回「所有非虚拟网卡求和」。返回累计字节，速率由调用方按时间差计算。
    private static func parseNet(_ netPart: String) -> NetCounters? {
        let sections = netPart.components(separatedBy: netDevMarker)
        guard sections.count > 1 else { return nil }
        let wanted = Set(sections[0]
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty })
        let devText = sections[1]

        var rx = 0, tx = 0
        var found = false
        for rawLine in devText.split(separator: "\n") {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            // 表头两行没有冒号，自然被跳过；数据行形如 "eth0: <rx...> <tx...>"。
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = String(line[..<colon]).trimmingCharacters(in: .whitespaces)
            let fields = line[line.index(after: colon)...]
                .split(whereSeparator: { $0 == " " || $0 == "\t" })
            guard fields.count >= 9 else { continue }

            let include = wanted.isEmpty ? !isVirtualInterface(name) : wanted.contains(name)
            guard include else { continue }
            // /proc/net/dev 冒号后字段：[0]=rx bytes，[8]=tx bytes。
            guard let r = Int(fields[0]), let t = Int(fields[8]) else { continue }
            rx += r; tx += t; found = true
        }
        return found ? NetCounters(rxBytes: rx, txBytes: tx) : nil
    }

    /// 回环口与常见虚拟网卡（容器/网桥/隧道等），仅在没有默认网卡可用时用于过滤。
    private static func isVirtualInterface(_ name: String) -> Bool {
        if name == "lo" { return true }
        let prefixes = ["docker", "veth", "br-", "virbr", "vnet", "tun", "tap",
                        "kube", "cni", "flannel", "cali", "ifb", "dummy", "bond"]
        return prefixes.contains { name.hasPrefix($0) }
    }

    // MARK: - Process helper

    private struct ProcessResult: Sendable {
        let exitCode: Int32
        let stdout: String
        let stderr: String
    }

    /// 在后台队列里跑外部进程，避免阻塞主线程。
    private static func runProcess(launchPath: String, arguments: [String]) async throws -> ProcessResult {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: launchPath)
                process.arguments = arguments

                let outPipe = Pipe()
                let errPipe = Pipe()
                process.standardOutput = outPipe
                process.standardError = errPipe

                do {
                    try process.run()
                } catch {
                    continuation.resume(throwing: error)
                    return
                }

                let outData = outPipe.fileHandleForReading.readDataToEndOfFile()
                let errData = errPipe.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()

                continuation.resume(returning: ProcessResult(
                    exitCode: process.terminationStatus,
                    stdout: String(data: outData, encoding: .utf8) ?? "",
                    stderr: String(data: errData, encoding: .utf8) ?? ""
                ))
            }
        }
    }
}
