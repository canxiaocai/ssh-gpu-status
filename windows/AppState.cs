using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Win32;

namespace GpuStatus;

/// 单一数据源：持有服务器列表、轮询、持久化与派生值。对应 macOS 版 AppState.swift。
///
/// 线程模型：本类在 UI 线程上创建。轮询由 UI 线程的 WinForms 计时器驱动（Tick 必在 UI 线程），
/// await 续延也回到 UI 线程，因此所有状态变更都在 UI 线程发生、无需加锁。ssh 的实际 I/O 在
/// 后台线程跑（GpuMonitor 内部 ConfigureAwait(false)）。电源/网络事件回调可能在别的线程，只置
/// volatile 标志，由计时器在下一拍读取。
public sealed class AppState : IDisposable
{
    /// 任意状态变化（轮询到新数据、切换开关）后触发，UI 据此重绘。始终在 UI 线程触发。
    public event Action? Changed;

    private readonly AppSettings _settings;
    private readonly Dictionary<Guid, HostStatus> _statusByServer = new();
    private readonly Dictionary<Guid, (NetCounters counters, DateTime at)> _prevNet = new();

    // 轮询用 WinForms 计时器驱动：Tick 一定在 UI 线程上触发，await 续延也回到 UI 线程，
    // 因此无需捕获 SynchronizationContext、无需跨线程编组。基础节拍 250ms，按 EffectiveInterval 决定何时真正抓取。
    private readonly System.Windows.Forms.Timer _timer;
    private CancellationTokenSource _fetchCts = new();
    private const int BaseTickMs = 250;
    private double _sinceLastFetch;
    private bool _firstFetch = true;
    private volatile bool _wake;   // 睡眠唤醒 / 网络恢复（任意线程置位），下一拍立即抓取
    private bool _busy;
    private bool _isPanelOpen;
    private bool _wasNetworkSatisfied = true;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ServerGpuStatus";

    public AppState()
    {
        _settings = AppSettings.Load();
        _settings.Servers = MergeWithConfig(_settings.Servers, _settings.IgnoredAliases);
        Save(); // 写回合并结果

        _timer = new System.Windows.Forms.Timer { Interval = BaseTickMs };
        _timer.Tick += OnTick;

        try { _wasNetworkSatisfied = NetworkInterface.GetIsNetworkAvailable(); } catch { /* ignore */ }
        ObservePowerAndNetwork();
        StartPolling();
    }

    // MARK: - 设置（带持久化）

    public bool ShowUtilization
    {
        get => _settings.ShowUtilization;
        set { _settings.ShowUtilization = value; Save(); Notify(); }
    }
    public bool ShowMemory
    {
        get => _settings.ShowMemory;
        set { _settings.ShowMemory = value; Save(); Notify(); }
    }
    public bool ShowTemperature
    {
        get => _settings.ShowTemperature;
        set { _settings.ShowTemperature = value; Save(); Notify(); }
    }
    public bool MenuBarShowsUtilization
    {
        get => _settings.MenuBarShowsUtilization;
        set { _settings.MenuBarShowsUtilization = value; Save(); Notify(); }
    }
    public double RefreshInterval
    {
        get => _settings.RefreshInterval;
        set { if (value == _settings.RefreshInterval) return; _settings.RefreshInterval = value; Save(); StartPolling(); }
    }
    public double FastInterval
    {
        get => _settings.FastInterval;
        set { if (value == _settings.FastInterval) return; _settings.FastInterval = value; Save(); if (_isPanelOpen) StartPolling(); }
    }

    // MARK: - 派生值

    public IReadOnlyList<ServerConfig> Servers => _settings.Servers;

    /// 可见（要监控）的服务器，按用户顺序。
    public List<ServerConfig> VisibleServers => _settings.Servers.Where(s => s.IsVisible).ToList();

    public List<HostStatus> OrderedStatuses =>
        VisibleServers.Select(s => _statusByServer.TryGetValue(s.Id, out var st)
            ? st
            : new HostStatus { ServerId = s.Id, Name = s.Name }).ToList();

    public MetricVisibility MetricVisibility => new(ShowUtilization, ShowMemory, ShowTemperature);

    /// 所有可见服务器上 GPU 的最高利用率，用于托盘摘要。
    public int? MaxUtilization
    {
        get
        {
            var ids = VisibleServers.Select(s => s.Id).ToHashSet();
            var vals = _statusByServer.Where(kv => ids.Contains(kv.Key))
                .SelectMany(kv => kv.Value.Gpus)
                .Where(g => g.Utilization.HasValue)
                .Select(g => g.Utilization!.Value)
                .ToList();
            return vals.Count > 0 ? vals.Max() : null;
        }
    }

    // MARK: - 服务器管理

    public void ToggleVisible(Guid id)
    {
        var s = _settings.Servers.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        s.IsVisible = !s.IsVisible;
        Save();
        Notify();
        if (s.IsVisible) _ = RefreshAllAsync();
    }

    public void MoveUp(Guid id)
    {
        var i = _settings.Servers.FindIndex(x => x.Id == id);
        if (i > 0)
        {
            (_settings.Servers[i - 1], _settings.Servers[i]) = (_settings.Servers[i], _settings.Servers[i - 1]);
            Save(); Notify();
        }
    }

    public void MoveDown(Guid id)
    {
        var i = _settings.Servers.FindIndex(x => x.Id == id);
        if (i >= 0 && i < _settings.Servers.Count - 1)
        {
            (_settings.Servers[i + 1], _settings.Servers[i]) = (_settings.Servers[i], _settings.Servers[i + 1]);
            Save(); Notify();
        }
    }

    /// 从列表删除：自定义项直接删；config 项记入 ignored，避免合并时又冒出来。
    public void DeleteServer(Guid id)
    {
        var s = _settings.Servers.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        if (s.Kind == ServerSourceKind.SshConfig && s.Alias is { } alias && !_settings.IgnoredAliases.Contains(alias))
            _settings.IgnoredAliases.Add(alias);
        _settings.Servers.RemoveAll(x => x.Id == id);
        Prune();
        Save();
        Notify();
    }

    public void AddCustomServer(string name, string user, string host, int port, string? identityFile)
    {
        var key = identityFile?.Trim();
        var conn = new CustomConnection
        {
            User = user.Trim(),
            Host = host.Trim(),
            Port = port,
            IdentityFile = string.IsNullOrEmpty(key) ? null : key,
        };
        var display = string.IsNullOrWhiteSpace(name) ? host.Trim() : name.Trim();
        _settings.Servers.Add(new ServerConfig
        {
            Name = display,
            Kind = ServerSourceKind.Custom,
            Custom = conn,
            IsVisible = true,
        });
        Save();
        Notify();
        _ = RefreshAllAsync();
    }

    /// 重新读取 ~/.ssh/config：清空 ignored（恢复此前删掉的 config 项），再与现有列表合并。
    public void ReloadHosts()
    {
        _settings.IgnoredAliases.Clear();
        _settings.Servers = MergeWithConfig(_settings.Servers, _settings.IgnoredAliases);
        Save();
        Notify();
        _ = RefreshAllAsync();
    }

    // MARK: - 轮询节奏

    private double EffectiveInterval => _isPanelOpen ? _settings.FastInterval : _settings.RefreshInterval;

    public void PanelDidOpen()
    {
        if (_isPanelOpen) return;
        _isPanelOpen = true;
        StartPolling(); // 立即刷新一次并切到快速节奏
    }

    public void PanelDidClose()
    {
        if (!_isPanelOpen) return;
        _isPanelOpen = false;
    }

    /// 重新起算节奏并尽快刷新一次：取消在途请求、下一拍立即抓取。
    public void StartPolling()
    {
        _fetchCts.Cancel();
        _fetchCts = new CancellationTokenSource();
        _firstFetch = true;
        _sinceLastFetch = 0;
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _sinceLastFetch += BaseTickMs / 1000.0;
        bool due = _firstFetch || _wake || _sinceLastFetch >= EffectiveInterval;
        if (!due || _busy) return;

        _firstFetch = false;
        _wake = false;
        _sinceLastFetch = 0;

        _busy = true;
        var ct = _fetchCts.Token;
        try { await RefreshAllAsync(ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { /* 轮询被重启 */ }
        catch (Exception ex) { Log.Write($"refresh loop error: {ex.Message}"); }
        finally { _busy = false; }
    }

    /// 并发拉取所有可见服务器的 GPU 状态，每台结果到达即更新。
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var targets = VisibleServers;
        if (targets.Count == 0) return;

        foreach (var s in targets) Status(s).IsLoading = true;
        Notify();

        var tasks = targets.Select(s => FetchOneAsync(s, ct)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private HostStatus Status(ServerConfig s)
    {
        if (!_statusByServer.TryGetValue(s.Id, out var st))
        {
            st = new HostStatus { ServerId = s.Id, Name = s.Name };
            _statusByServer[s.Id] = st;
        }
        return st;
    }

    private async Task FetchOneAsync(ServerConfig server, CancellationToken ct)
    {
        FetchResult? result = null;
        Exception? error = null;
        try
        {
            result = await GpuMonitor.FetchAsync(server, 10, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // 轮询被重启/取消：不动状态
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // 回到 UI 线程后再改状态。
        var current = _settings.Servers.FirstOrDefault(x => x.Id == server.Id);
        if (current == null || !current.IsVisible) return;

        var st = Status(current);
        st.IsLoading = false;
        if (result != null)
        {
            st.Gpus = result.Gpus;
            st.ErrorMessage = null;
            st.LastUpdated = DateTime.Now;
            UpdateNetRate(st, server.Id, result.Net);
        }
        else if (error != null)
        {
            st.Gpus = new List<GpuInfo>();
            st.ErrorMessage = error.Message;
            // 抓取失败：清掉速率与基线，恢复后从新样本重新起算。
            st.NetRxBytesPerSec = null;
            st.NetTxBytesPerSec = null;
            _prevNet.Remove(server.Id);
            Log.Write($"fetch {server.Name} failed: {error.Message}");
        }
        Notify();
    }

    /// 用本次累计计数与上次样本求收发速率（B/s）；首次无样本、间隔过短或计数器回绕（重启）时
    /// 不刷新显示值，并把本次作为新基线。
    private void UpdateNetRate(HostStatus status, Guid id, NetCounters? counters)
    {
        if (counters is not { } c) return;
        var now = DateTime.Now;
        var hadPrev = _prevNet.TryGetValue(id, out var prev);
        _prevNet[id] = (c, now);
        if (!hadPrev) return;

        var dt = (now - prev.at).TotalSeconds;
        var drx = c.RxBytes - prev.counters.RxBytes;
        var dtx = c.TxBytes - prev.counters.TxBytes;
        if (dt > 0.2 && drx >= 0 && dtx >= 0)
        {
            status.NetRxBytesPerSec = drx / dt;
            status.NetTxBytesPerSec = dtx / dt;
        }
    }

    // MARK: - 开机自启（注册表 Run 键）

    public bool LaunchAtLogin
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return k?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                k!.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                k!.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"launchAtLogin set {enabled} failed: {ex.Message}");
        }
        Notify();
    }

    // MARK: - 睡眠唤醒 / 网络恢复

    private void ObservePowerAndNetwork()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    // 这两个事件可能在任意线程触发，只置 volatile 标志，由 UI 线程的计时器在下一拍读取并立即抓取。
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _wake = true;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable && !_wasNetworkSatisfied) _wake = true;
        _wasNetworkSatisfied = e.IsAvailable;
    }

    // MARK: - 合并 ~/.ssh/config

    /// 把当前 config 别名合并进已有列表：删掉消失的 config 项，追加新别名（跳过 ignored），保留自定义项与顺序。
    private static List<ServerConfig> MergeWithConfig(List<ServerConfig> existing, List<string> ignored)
    {
        var aliases = SshConfig.ParseAliases();
        var aliasSet = aliases.ToHashSet(StringComparer.Ordinal);
        var ignoredSet = ignored.ToHashSet(StringComparer.Ordinal);

        var result = existing
            .Where(s => s.Kind != ServerSourceKind.SshConfig || (s.Alias != null && aliasSet.Contains(s.Alias)))
            .ToList();

        var known = result
            .Where(s => s.Kind == ServerSourceKind.SshConfig && s.Alias != null)
            .Select(s => s.Alias!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var alias in aliases)
            if (!known.Contains(alias) && !ignoredSet.Contains(alias))
                result.Add(ServerConfig.FromAlias(alias));

        return result;
    }

    // MARK: - 杂项

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "—";

    private void Notify() => Changed?.Invoke();
    private void Save() => _settings.Save();

    private void Prune()
    {
        var keep = _settings.Servers.Select(s => s.Id).ToHashSet();
        foreach (var id in _statusByServer.Keys.Where(k => !keep.Contains(k)).ToList()) _statusByServer.Remove(id);
        foreach (var id in _prevNet.Keys.Where(k => !keep.Contains(k)).ToList()) _prevNet.Remove(id);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _fetchCts.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
