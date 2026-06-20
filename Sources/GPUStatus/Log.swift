import Foundation

/// 极简文件日志，写到 ~/Library/Logs/GPUStatus.log。
/// 排查 GUI 下 ssh / 解析问题用：`tail -f ~/Library/Logs/GPUStatus.log`。
enum Log {
    static let fileURL: URL = {
        let dir = FileManager.default
            .homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("GPUStatus.log")
    }()

    private static let queue = DispatchQueue(label: "com.cxc.gpustatus.log")
    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func write(_ message: String) {
        let line = "[\(formatter.string(from: Date()))] \(message)\n"
        queue.async {
            guard let data = line.data(using: .utf8) else { return }
            if let handle = try? FileHandle(forWritingTo: fileURL) {
                defer { try? handle.close() }
                handle.seekToEndOfFile()
                handle.write(data)
            } else {
                try? data.write(to: fileURL)
            }
        }
    }
}
