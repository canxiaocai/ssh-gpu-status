using System.Globalization;
using System.Text.Json.Serialization;

namespace GpuStatus;

/// 远程在某块 GPU 上运行的计算进程。
public sealed class GpuProcess
{
    public int Pid { get; init; }
    public string User { get; init; } = "?";   // 占用用户（远程 ps 解析，未知为 "?"）
    public int MemoryUsedMiB { get; init; }     // 占用显存 MiB
    public string Name { get; init; } = "";     // 进程名（可能是完整路径）
    public int? ElapsedSeconds { get; init; }   // 已运行时间（远程 ps etimes，秒；老旧 ps 不支持时为 null）

    /// 进程名的最后一段（去掉路径），用于窄面板展示。
    public string ShortName => LastPathComponent(Name);

    /// 已运行时间的紧凑文本，如 "3天5小时"、"5小时12分"、"12分30秒"、"30秒"；无数据为 null。
    public string? RuntimeText
    {
        get
        {
            if (ElapsedSeconds is not { } s || s < 0) return null;
            int day = s / 86400, hour = (s % 86400) / 3600, minute = (s % 3600) / 60, second = s % 60;
            if (day > 0) return $"{day}天{hour}小时";
            if (hour > 0) return $"{hour}小时{minute}分";
            if (minute > 0) return $"{minute}分{second}秒";
            return $"{second}秒";
        }
    }

    /// 类 macOS NSString.lastPathComponent：按 '/' 取最后一段。
    private static string LastPathComponent(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }
}

/// 单块 GPU 的状态快照，对应 nvidia-smi 的一行查询结果。
public sealed class GpuInfo
{
    public int Index { get; init; }
    public string Uuid { get; init; } = "";
    public string Name { get; init; } = "";
    public int? Utilization { get; init; }      // 利用率 %（MIG 实例等场景返回 [N/A] → null）
    public int MemoryUsedMiB { get; init; }
    public int MemoryTotalMiB { get; init; }
    public int? Temperature { get; init; }       // 温度 °C（被动散热卡可能为 [N/A] → null）
    public List<GpuProcess> Processes { get; set; } = new();

    /// 这块卡是否有人在用：优先看进程；进程信息抓取失败时退回看显存占用。
    public bool IsBusy => Processes.Count > 0 || MemoryUsedMiB > 200;

    public double MemoryFraction => MemoryTotalMiB > 0 ? (double)MemoryUsedMiB / MemoryTotalMiB : 0;
    public int MemoryPercent => (int)Math.Round(MemoryFraction * 100);

    /// 显存「已用/总量」的紧凑 GiB 文本，如 "22.7/48.0G"。
    public string MemoryGiBText =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0}G", MemoryUsedMiB / 1024.0, MemoryTotalMiB / 1024.0);

    /// 去掉 "NVIDIA " 前缀的简短型号。
    public string ShortName => Name.Replace("NVIDIA ", "");
}

/// 自定义服务器的连接信息（仅密钥/身份文件认证）。
public sealed class CustomConnection
{
    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string? IdentityFile { get; set; }   // 私钥路径，可空（用 ssh-agent / 默认密钥）
}

public enum ServerSourceKind { SshConfig, Custom }

/// 一台可监控的服务器：要么来自 ~/.ssh/config 的别名，要么是用户自定义的连接。
public sealed class ServerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";       // 展示名（config 用别名；自定义用用户取的名）
    public ServerSourceKind Kind { get; set; }
    public string? Alias { get; set; }            // Kind == SshConfig 时有效
    public CustomConnection? Custom { get; set; } // Kind == Custom 时有效
    public bool IsVisible { get; set; } = true;   // 是否在面板显示并轮询

    [JsonIgnore] public bool IsCustom => Kind == ServerSourceKind.Custom;

    /// 副标题：展示连接方式。
    [JsonIgnore]
    public string Detail => Kind switch
    {
        ServerSourceKind.SshConfig => $"~/.ssh/config · {Alias}",
        ServerSourceKind.Custom when Custom is { } c =>
            $"{c.User}@{c.Host}:{c.Port}{(string.IsNullOrEmpty(c.IdentityFile) ? "" : " · 🔑")}",
        _ => ""
    };

    public static ServerConfig FromAlias(string alias) =>
        new() { Name = alias, Kind = ServerSourceKind.SshConfig, Alias = alias, IsVisible = true };
}

/// 网络收发的累计字节计数（取自远程 /proc/net/dev），用于在两次轮询间求速率。
public readonly record struct NetCounters(long RxBytes, long TxBytes);

/// 一次远程抓取的完整结果：GPU 列表 + 可选的网络计数。
public sealed class FetchResult
{
    public List<GpuInfo> Gpus { get; init; } = new();
    public NetCounters? Net { get; init; }
}

/// 单台服务器的实时监控状态。
public sealed class HostStatus
{
    public Guid ServerId { get; init; }
    public string Name { get; set; } = "";
    public List<GpuInfo> Gpus { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public bool IsLoading { get; set; }
    public DateTime? LastUpdated { get; set; }
    public double? NetRxBytesPerSec { get; set; }  // 下行速率 B/s（首次无上次样本时 null）
    public double? NetTxBytesPerSec { get; set; }  // 上行速率 B/s
}

/// 用户在设置里选择展示哪些指标。
public readonly record struct MetricVisibility(bool Utilization, bool Memory, bool Temperature);

/// 网络速率的紧凑文本格式化（1024 进制，B/s · KB/s · MB/s · GB/s）。
public static class NetFormat
{
    public static string Speed(double bytesPerSec)
    {
        var v = Math.Max(0, bytesPerSec);
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        var ci = CultureInfo.InvariantCulture;
        if (v >= gb) return string.Format(ci, "{0:0.0} GB/s", v / gb);
        if (v >= mb) return string.Format(ci, "{0:0.0} MB/s", v / mb);
        if (v >= kb) return string.Format(ci, "{0:0} KB/s", v / kb);
        return string.Format(ci, "{0:0} B/s", v);
    }
}
