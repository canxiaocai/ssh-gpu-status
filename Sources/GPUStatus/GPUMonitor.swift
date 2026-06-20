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

    /// 远程命令：先查每块 GPU（含 uuid 用于关联进程），再查计算进程并用 `ps` 补上占用用户。
    /// 进程部分 best-effort：失败也不影响 GPU 数据（`|| true`）。
    private static let remoteCommand = """
    nvidia-smi --query-gpu=index,uuid,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits || exit $?
    echo '\(procMarker)'
    { nvidia-smi --query-compute-apps=gpu_uuid,pid,used_memory,process_name --format=csv,noheader,nounits | while IFS=',' read -r uuid pid mem pname; do pid=$(echo "$pid" | tr -d ' '); u=$(ps -o user= -p "$pid" 2>/dev/null | tr -d ' '); echo "${uuid},${pid},${mem},${pname},${u}"; done; } 2>/dev/null || true
    """

    static func fetch(server: ServerConfig, connectTimeout: Int = 10) async throws -> [GPUInfo] {
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

    static func parse(_ output: String) throws -> [GPUInfo] {
        let parts = output.components(separatedBy: procMarker)
        let gpuPart = parts[0]
        let procPart = parts.count > 1 ? parts[1] : ""

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
            guard cols.count >= 5, let index = indexByUUID[cols[0]], let pid = Int(cols[1]) else { continue }
            // 进程名可能含逗号（带参数的路径 / python 脚本名 / java 主类）。user 是远端 ps 追加的最后一列：
            // 固定取前 3 列 + 最后 1 列，中间所有列用逗号 join 还原进程名，避免「用户/显存列被冲乱」。
            let user = cols[cols.count - 1]
            let pname = cols[3..<(cols.count - 1)].joined(separator: ",")
            let proc = GPUProcess(
                pid: pid,
                user: user.isEmpty ? "?" : user,
                memoryUsed: Int(cols[2]) ?? 0,
                name: pname
            )
            procsByIndex[index, default: []].append(proc)
        }
        for i in gpus.indices {
            gpus[i].processes = (procsByIndex[gpus[i].index] ?? []).sorted { $0.memoryUsed > $1.memoryUsed }
        }

        return gpus
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
