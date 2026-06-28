using System.Drawing;

namespace GpuStatus;

/// 自己管理 NSStatusItem 的 Windows 对应物：用 NotifyIcon 常驻托盘，左键开关面板、右键弹菜单。
/// 订阅 AppState.Changed，在数据/开关变化时重画托盘图标并刷新已打开的面板。对应 macOS 版 AppDelegate。
public sealed class TrayContext : ApplicationContext
{
    private readonly AppState _state;
    private readonly NotifyIcon _tray;
    private PanelForm? _panel;
    private SettingsForm? _settings;
    private Icon? _currentIcon;

    private int _lastUtilKey = int.MinValue;
    private bool _lastDark;
    private bool _hasIcon;
    private DateTime _panelHiddenAt = DateTime.MinValue;

    public TrayContext()
    {
        _state = new AppState();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => TogglePanel());
        menu.Items.Add("立即刷新", null, async (_, _) => await _state.RefreshAllAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 GPU 状态", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "Server GPU Status",
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += OnTrayClick;

        _state.Changed += OnStateChanged;
        RefreshTray();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) TogglePanel();
    }

    private void OnStateChanged()
    {
        RefreshTray();
        if (_panel is { Visible: true }) _panel.UpdateContent();
    }

    // MARK: - 托盘图标

    private void RefreshTray()
    {
        var util = _state.MenuBarShowsUtilization ? _state.MaxUtilization : null;
        var dark = Branding.IsTaskbarDark();
        _tray.Text = BuildTooltip(util);

        var utilKey = util ?? -1;
        if (_hasIcon && utilKey == _lastUtilKey && dark == _lastDark) return;
        _lastUtilKey = utilKey;
        _lastDark = dark;
        _hasIcon = true;

        var newIcon = Branding.CreateTrayIcon(util, dark);
        _tray.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
    }

    private string BuildTooltip(int? util)
    {
        var visible = _state.VisibleServers.Count;
        var head = util is { } u ? $"最高 GPU 利用率 {u}%" : "Server GPU Status";
        var text = visible > 0 ? $"{head} · {visible} 台服务器" : head;
        return text.Length > 127 ? text[..127] : text;
    }

    // MARK: - 面板

    private void TogglePanel()
    {
        if (_panel == null || _panel.IsDisposed)
        {
            _panel = new PanelForm(_state, ShowSettings, ExitApp);
            _panel.VisibleChanged += (_, _) =>
            {
                if (_panel is { Visible: false }) _panelHiddenAt = DateTime.Now;
            };
        }

        if (_panel.Visible)
        {
            _panel.Hide();
        }
        else
        {
            // 点击托盘会先让面板 Deactivate 而隐藏；若刚隐藏（<250ms）则视为「关闭」，不要又弹回来。
            if ((DateTime.Now - _panelHiddenAt).TotalMilliseconds > 250)
                _panel.ShowNearTray();
        }
    }

    private void ShowSettings()
    {
        if (_settings == null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_state);
            _settings.FormClosed += (_, _) => _settings = null;
        }
        _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized)
            _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _state.Dispose();
        _panel?.Dispose();
        _settings?.Dispose();
        _tray.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentIcon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
