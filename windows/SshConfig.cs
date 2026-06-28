using System.IO;

namespace GpuStatus;

/// 极简的 ~/.ssh/config（Windows: %USERPROFILE%\.ssh\config）解析器：
/// 只提取可直接连接的 Host 别名，跳过通配符模式（*、?）和否定模式（!）。对应 macOS 版 SSHConfig.swift。
public static class SshConfig
{
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// 返回可直连的 Host 别名（按 config 出现顺序，去重）。
    public static List<string> ParseAliases(string? path = null)
    {
        path ??= DefaultPath();
        var aliases = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string content;
        try { content = File.ReadAllText(path); }
        catch { return aliases; }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // 任意空白分隔
            if (tokens.Length < 2 || !tokens[0].Equals("host", StringComparison.OrdinalIgnoreCase)) continue;

            for (int i = 1; i < tokens.Length; i++)
            {
                var alias = tokens[i];
                if (alias.Contains('*') || alias.Contains('?') || alias.StartsWith('!')) continue;
                if (seen.Add(alias)) aliases.Add(alias);
            }
        }

        return aliases;
    }
}
