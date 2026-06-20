import Foundation

/// 极简的 ~/.ssh/config 解析器：只提取可直接连接的 Host 别名，
/// 跳过通配符模式（*、?）和否定模式（!），它们不能直接作为连接目标。
enum SSHConfig {
    static func defaultPath() -> String {
        NSString(string: "~/.ssh/config").expandingTildeInPath
    }

    /// 返回可直连的 Host 别名（按 config 出现顺序，去重）。
    static func parseAliases(at path: String = SSHConfig.defaultPath()) -> [String] {
        guard let content = try? String(contentsOfFile: path, encoding: .utf8) else {
            return []
        }

        var aliases: [String] = []
        var seen = Set<String>()

        for rawLine in content.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }

            let tokens = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
            guard tokens.count >= 2, tokens[0].lowercased() == "host" else { continue }

            for token in tokens.dropFirst() {
                let alias = String(token)
                if alias.contains("*") || alias.contains("?") || alias.hasPrefix("!") { continue }
                if seen.insert(alias).inserted {
                    aliases.append(alias)
                }
            }
        }

        return aliases
    }
}
