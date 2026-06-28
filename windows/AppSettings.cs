using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuStatus;

/// 持久化设置，存到 %APPDATA%\GpuStatus\settings.json。
/// 对应 macOS 版用 UserDefaults 存的那些键。仓库与磁盘上都不含主机/账号信息——
/// 服务器列表由用户在运行时从 ~/.ssh/config 合并或手动添加。
public sealed class AppSettings
{
    public List<ServerConfig> Servers { get; set; } = new();
    public List<string> IgnoredAliases { get; set; } = new();
    public bool ShowUtilization { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowTemperature { get; set; } = true;
    public bool MenuBarShowsUtilization { get; set; } = true;
    public double RefreshInterval { get; set; } = 10;  // 关闭面板时的慢速刷新间隔（秒）
    public double FastInterval { get; set; } = 1;       // 打开面板时的快速刷新间隔（秒）

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuStatus");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath(), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}
