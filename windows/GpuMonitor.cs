using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GpuStatus;

public sealed class GpuException : Exception
{
    public GpuException(string message) : base(message) { }
}

/// 负责通过 SSH 在远程执行 nvidia-smi 并解析输出。对应 macOS 版 GPUMonitor.swift，
/// 远程命令与解析规则完全一致，区别只是本地用 Windows 的 ssh.exe。
public static class GpuMonitor
{
    private const string ProcMarker = "@@@PROC@@@";
    private const string NetMarker = "@@@NET@@@";
    private const string NetDevMarker = "@@@NETDEV@@@";

    /// 远程命令：先查每块 GPU（含 uuid 用于关联进程），再查计算进程并用 ps 补上占用用户与已运行时间，
    /// 最后采一次网络计数（默认路由网卡名 + /proc/net/dev 原文，速率在客户端按两次轮询差值算）。
    /// 进程 / 网络部分均 best-effort（|| true），失败也不影响 GPU 数据。
    private static readonly string RemoteCommand =
        "nvidia-smi --query-gpu=index,uuid,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits || exit $?\n" +
        "echo '" + ProcMarker + "'\n" +
        "{ nvidia-smi --query-compute-apps=gpu_uuid,pid,used_memory,process_name --format=csv,noheader,nounits | while IFS=',' read -r uuid pid mem pname; do pid=$(echo \"$pid\" | tr -d ' '); u=$(ps -o user= -p \"$pid\" 2>/dev/null | tr -d ' '); et=$(ps -o etimes= -p \"$pid\" 2>/dev/null | tr -d ' '); echo \"${uuid},${pid},${mem},${pname},${u},${et}\"; done; } 2>/dev/null || true\n" +
        "echo '" + NetMarker + "'\n" +
        "{ ip route show default 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i==\"dev\") print $(i+1)}'; echo '" + NetDevMarker + "'; cat /proc/net/dev 2>/dev/null; } 2>/dev/null || true";

    /// Windows 10/11 自带的 OpenSSH 客户端路径；找不到则退回 PATH 上的 "ssh"。
    private static readonly string SshPath = ResolveSsh();

    private static string ResolveSsh()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(sys) ? sys : "ssh";
    }

    public static async Task<FetchResult> FetchAsync(ServerConfig server, int connectTimeout = 10, CancellationToken ct = default)
    {
        var args = BuildSshArgs(server, connectTimeout);
        args.Add(RemoteCommand);

        var (exit, stdout, stderr) = await RunProcessAsync(SshPath, args, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            var msg = stderr.Trim();
            if (msg.Length == 0) msg = stdout.Trim();
            throw new GpuException($"SSH 连接失败 (code {exit})：{msg}");
        }
        return Parse(stdout);
    }

    /// 按服务器来源拼 ssh 参数。
    public static List<string> BuildSshArgs(ServerConfig server, int connectTimeout)
    {
        var args = new List<string> { "-o", "BatchMode=yes", "-o", $"ConnectTimeout={connectTimeout}" };
        if (server.Kind == ServerSourceKind.SshConfig)
        {
            args.Add(server.Alias ?? "");
        }
        else if (server.Custom is { } c)
        {
            args.Add("-p");
            args.Add(c.Port.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(c.IdentityFile))
            {
                args.Add("-i");
                args.Add(ExpandUser(c.IdentityFile!));
            }
            // 自定义服务器首次连接自动接受主机指纹，避免卡在 known_hosts 确认。
            args.Add("-o");
            args.Add("StrictHostKeyChecking=accept-new");
            args.Add($"{c.User}@{c.Host}");
        }
        return args;
    }

    /// 展开开头的 ~ 为用户主目录（类 macOS expandingTildeInPath）。
    private static string ExpandUser(string path)
    {
        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + path[1..].Replace('/', Path.DirectorySeparatorChar);
        }
        return path;
    }

    public static FetchResult Parse(string output)
    {
        var parts = output.Split(new[] { ProcMarker }, StringSplitOptions.None);
        var gpuPart = parts[0];
        // ProcMarker 之后是「进程段 + 网络段」，再按 NetMarker 拆开。
        var afterGpu = parts.Length > 1 ? parts[1] : "";
        var procAndNet = afterGpu.Split(new[] { NetMarker }, StringSplitOptions.None);
        var procPart = procAndNet[0];
        var netPart = procAndNet.Length > 1 ? procAndNet[1] : "";

        var gpus = new List<GpuInfo>();
        var indexByUuid = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rawLine in gpuPart.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var cols = line.Split(',');
            for (int i = 0; i < cols.Length; i++) cols[i] = cols[i].Trim();
            // index、uuid、显存是核心字段，缺失/无法解析就跳过该行（不连累整台）；利用率、温度宽容处理。
            if (cols.Length < 7 || !int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                Log.Write($"parse: 跳过无法识别的 GPU 行: {line}");
                continue;
            }
            var uuid = cols[1];
            if (uuid.Length == 0)
            {
                Log.Write($"parse: 跳过缺少 uuid 的 GPU 行: {line}");
                continue;
            }
            if (!int.TryParse(cols[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memUsed) ||
                !int.TryParse(cols[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memTotal))
            {
                Log.Write($"parse: 跳过显存异常的 GPU 行: {line}");
                continue;
            }

            indexByUuid[uuid] = index;
            gpus.Add(new GpuInfo
            {
                Index = index,
                Uuid = uuid,
                Name = cols[2],
                Utilization = ParseIntOrNull(cols[3]),   // [N/A] → null
                MemoryUsedMiB = memUsed,
                MemoryTotalMiB = memTotal,
                Temperature = ParseIntOrNull(cols[6]),    // [N/A] → null
            });
        }

        if (gpus.Count == 0)
            throw new GpuException("服务器未返回 GPU 数据（nvidia-smi 是否可用？）");

        // 把进程按 uuid 归到对应 GPU。
        var procsByIndex = new Dictionary<int, List<GpuProcess>>();
        foreach (var rawLine in procPart.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var cols = line.Split(',');  // 保留空字段
            if (cols.Length < 6) continue;
            for (int i = 0; i < cols.Length; i++) cols[i] = cols[i].Trim();
            if (!indexByUuid.TryGetValue(cols[0], out var index)) continue;
            if (!int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) continue;
            // 进程名可能含逗号。末两列由远端 ps 追加且不含逗号：倒数第一列 = etimes，倒数第二列 = user；
            // 固定取前 3 列 + 末 2 列，中间所有列用逗号 join 还原进程名。
            var elapsed = ParseIntOrNull(cols[^1]);
            var user = cols[^2];
            var pname = string.Join(",", cols[3..^2]);
            var proc = new GpuProcess
            {
                Pid = pid,
                User = user.Length == 0 ? "?" : user,
                MemoryUsedMiB = ParseIntOrNull(cols[2]) ?? 0,
                Name = pname,
                ElapsedSeconds = elapsed,
            };
            if (!procsByIndex.TryGetValue(index, out var list))
            {
                list = new List<GpuProcess>();
                procsByIndex[index] = list;
            }
            list.Add(proc);
        }
        foreach (var g in gpus)
        {
            if (procsByIndex.TryGetValue(g.Index, out var list))
                g.Processes = list.OrderByDescending(p => p.MemoryUsedMiB).ToList();
        }

        return new FetchResult { Gpus = gpus, Net = ParseNet(netPart) };
    }

    // MARK: - 网络计数解析

    /// 网络段 = 默认路由网卡名（每行一个）+ NetDevMarker + /proc/net/dev 原文。
    /// 优先只统计默认路由网卡，拿不到时退回「所有非虚拟网卡求和」。返回累计字节，速率由调用方按时间差计算。
    private static NetCounters? ParseNet(string netPart)
    {
        var sections = netPart.Split(new[] { NetDevMarker }, StringSplitOptions.None);
        if (sections.Length <= 1) return null;
        var wanted = new HashSet<string>(
            sections[0].Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        var devText = sections[1];

        long rx = 0, tx = 0;
        bool found = false;
        foreach (var rawLine in devText.Split('\n'))
        {
            var line = rawLine.Trim();
            // 表头两行没有冒号，自然被跳过；数据行形如 "eth0: <rx...> <tx...>"。
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            var fields = line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9) continue;

            var include = wanted.Count == 0 ? !IsVirtualInterface(name) : wanted.Contains(name);
            if (!include) continue;
            // /proc/net/dev 冒号后字段：[0]=rx bytes，[8]=tx bytes。
            if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
                !long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) continue;
            rx += r; tx += t; found = true;
        }
        return found ? new NetCounters(rx, tx) : null;
    }

    /// 回环口与常见虚拟网卡（容器/网桥/隧道等），仅在没有默认网卡可用时用于过滤。
    private static bool IsVirtualInterface(string name)
    {
        if (name == "lo") return true;
        string[] prefixes = { "docker", "veth", "br-", "virbr", "vnet", "tun", "tap",
                              "kube", "cni", "flannel", "cali", "ifb", "dummy", "bond" };
        return prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
    }

    private static int? ParseIntOrNull(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    // MARK: - Process helper

    /// 启动 ssh.exe，异步读取 stdout/stderr，取消时杀掉进程。不弹控制台窗口。
    private static async Task<(int exit, string stdout, string stderr)> RunProcessAsync(
        string fileName, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outTask = process.StandardOutput.ReadToEndAsync(ct);
        var errTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        var stdout = await outTask.ConfigureAwait(false);
        var stderr = await errTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
