using System.IO;

namespace GpuStatus;

/// 极简文件日志，写到 %LOCALAPPDATA%\GpuStatus\Logs\GpuStatus.log。
/// 排查 GUI 下 ssh / 解析问题用：`Get-Content -Wait $env:LOCALAPPDATA\GpuStatus\Logs\GpuStatus.log`。
/// 对应 macOS 版 Log.swift（那边写 ~/Library/Logs/GPUStatus.log）。
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = BuildPath();

    private static string BuildPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GpuStatus", "Logs");
        try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
        return Path.Combine(dir, "GpuStatus.log");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate) File.AppendAllText(FilePath, line);
        }
        catch
        {
            // 日志失败绝不能影响主流程。
        }
    }
}
