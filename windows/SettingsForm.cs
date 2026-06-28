using System.Drawing;

namespace GpuStatus;

/// 4 个标签页的设置窗口：通用 / 服务器 / 显示 / 关于。对应 macOS 版 SettingsView.swift。
public sealed class SettingsForm : Form
{
    private readonly AppState _state;
    private bool _refreshing;

    private CheckBox _launchCheck = null!;
    private ComboBox _fastCombo = null!, _slowCombo = null!;
    private CheckBox _utilCheck = null!, _memCheck = null!, _tempCheck = null!, _menubarCheck = null!;
    private ListView _serverList = null!;
    private TabControl _tabs = null!;

    /// 打开时定位到指定标签页（0=通用 1=服务器 2=显示 3=关于）。
    public void SelectTab(int index)
    {
        if (index >= 0 && index < _tabs.TabCount) _tabs.SelectedIndex = index;
    }

    private static readonly (string Label, double Value)[] FastOptions =
        { ("0.5 秒", 0.5), ("1 秒", 1), ("2 秒", 2), ("3 秒", 3) };
    private static readonly (string Label, double Value)[] SlowOptions =
        { ("5 秒", 5), ("10 秒", 10), ("30 秒", 30), ("60 秒", 60) };

    public SettingsForm(AppState state)
    {
        _state = state;

        Text = "GPU 状态 · 设置";
        // 可缩放 / 可最大化：任何 DPI 下都能调大，按钮永远点得到。
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        // Font 缩放比 Dpi 模式更可靠：以 96dpi 下 Segoe UI 9pt 的基准做等比缩放。
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        ClientSize = new Size(620, 600);
        MinimumSize = new Size(540, 560);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildServersTab());
        _tabs.TabPages.Add(BuildDisplayTab());
        _tabs.TabPages.Add(BuildAboutTab());
        Controls.Add(_tabs);

        _state.Changed += OnStateChanged;
        FormClosed += (_, _) => _state.Changed -= OnStateChanged;

        RefreshControls();
        RebuildServerList();
    }

    private void OnStateChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(OnStateChanged)); return; }
        RefreshControls();
        RebuildServerList();
    }

    private void RefreshControls()
    {
        _refreshing = true;
        _launchCheck.Checked = _state.LaunchAtLogin;
        _fastCombo.SelectedIndex = NearestIndex(FastOptions, _state.FastInterval);
        _slowCombo.SelectedIndex = NearestIndex(SlowOptions, _state.RefreshInterval);
        _utilCheck.Checked = _state.ShowUtilization;
        _memCheck.Checked = _state.ShowMemory;
        _tempCheck.Checked = _state.ShowTemperature;
        _menubarCheck.Checked = _state.MenuBarShowsUtilization;
        _refreshing = false;
    }

    // MARK: - 通用

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("通用") { Padding = new Padding(20), BackColor = SystemColors.Window, AutoScroll = true };
        int y = 16;

        page.Controls.Add(SectionHeader("启动", ref y));
        _launchCheck = new CheckBox { Text = "开机自动启动", AutoSize = true, Location = new Point(24, y) };
        _launchCheck.CheckedChanged += (_, _) => { if (!_refreshing) _state.SetLaunchAtLogin(_launchCheck.Checked); };
        page.Controls.Add(_launchCheck);
        y += 24;
        page.Controls.Add(Hint("登录 Windows 时自动启动 GPU 状态。", ref y));
        y += 16;

        page.Controls.Add(SectionHeader("刷新频率", ref y));
        page.Controls.Add(new Label { Text = "打开面板时（快）", AutoSize = true, Location = new Point(24, y + 4) });
        _fastCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(360, y), Width = 160 };
        foreach (var o in FastOptions) _fastCombo.Items.Add(o.Label);
        _fastCombo.SelectedIndexChanged += (_, _) => { if (!_refreshing && _fastCombo.SelectedIndex >= 0) _state.FastInterval = FastOptions[_fastCombo.SelectedIndex].Value; };
        page.Controls.Add(_fastCombo);
        y += 28;
        page.Controls.Add(Hint("面板打开时的刷新间隔，越短越实时。", ref y));
        y += 12;

        page.Controls.Add(new Label { Text = "关闭面板时（慢）", AutoSize = true, Location = new Point(24, y + 4) });
        _slowCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(360, y), Width = 160 };
        foreach (var o in SlowOptions) _slowCombo.Items.Add(o.Label);
        _slowCombo.SelectedIndexChanged += (_, _) => { if (!_refreshing && _slowCombo.SelectedIndex >= 0) _state.RefreshInterval = SlowOptions[_slowCombo.SelectedIndex].Value; };
        page.Controls.Add(_slowCombo);
        y += 28;
        page.Controls.Add(Hint("面板关闭后后台轮询的间隔，越长越省资源。", ref y));

        var quit = new Button { Text = "退出 GPU 状态", AutoSize = true, Location = new Point(24, 520) };
        quit.Click += (_, _) => Application.Exit();
        page.Controls.Add(quit);
        return page;
    }

    // MARK: - 服务器

    private TabPage BuildServersTab()
    {
        var page = new TabPage("服务器") { Padding = new Padding(12), BackColor = SystemColors.Window };

        var caption = new Label
        {
            Text = "勾选状态可在面板显示/隐藏；用下方按钮排序或删除。",
            AutoSize = false, Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 8, 6, 0),
            ForeColor = SystemColors.GrayText,
        };

        _serverList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _serverList.Columns.Add("状态", 70);
        _serverList.Columns.Add("名称", 170);
        _serverList.Columns.Add("连接", 280);
        _serverList.DoubleClick += (_, _) => ToggleSelected();

        // 自动高度 + 自动换行：按钮在任何 DPI / 窗口宽度下都完整显示，放不下就换行，永不裁切。
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(6),
            MinimumSize = new Size(0, 40),
        };
        bar.Controls.Add(MakeButton("显示 / 隐藏", ToggleSelected));
        bar.Controls.Add(MakeButton("上移", () => WithSelected(_state.MoveUp)));
        bar.Controls.Add(MakeButton("下移", () => WithSelected(_state.MoveDown)));
        bar.Controls.Add(MakeButton("删除", () => WithSelected(_state.DeleteServer)));
        bar.Controls.Add(MakeButton("添加服务器…", () => { using var f = new AddServerForm(_state); f.ShowDialog(this); }));
        bar.Controls.Add(MakeButton("重新读取 config", _state.ReloadHosts));

        page.Controls.Add(_serverList);
        page.Controls.Add(bar);
        page.Controls.Add(caption);
        return page;
    }

    private void RebuildServerList()
    {
        if (_serverList == null) return;
        var selectedId = SelectedServerId();
        _serverList.BeginUpdate();
        _serverList.Items.Clear();
        foreach (var s in _state.Servers)
        {
            var item = new ListViewItem(s.IsVisible ? "● 显示中" : "○ 已隐藏") { Tag = s.Id };
            item.ForeColor = s.IsVisible ? Color.FromArgb(0x4A, 0x8A, 0x00) : SystemColors.GrayText;
            item.SubItems.Add(s.Name);
            item.SubItems.Add(s.Detail);
            if (s.Id == selectedId) item.Selected = true;
            _serverList.Items.Add(item);
        }
        _serverList.EndUpdate();
    }

    private Guid? SelectedServerId() =>
        _serverList.SelectedItems.Count > 0 && _serverList.SelectedItems[0].Tag is Guid g ? g : null;

    private void ToggleSelected() => WithSelected(_state.ToggleVisible);

    private void WithSelected(Action<Guid> action)
    {
        if (SelectedServerId() is { } id) action(id);
    }

    // MARK: - 显示

    private TabPage BuildDisplayTab()
    {
        var page = new TabPage("显示") { Padding = new Padding(20), BackColor = SystemColors.Window, AutoScroll = true };
        int y = 16;

        page.Controls.Add(SectionHeader("展示指标", ref y));
        _utilCheck = AddToggle(page, "GPU 利用率", "显示每块 GPU 的计算利用率。", ref y, v => _state.ShowUtilization = v);
        _memCheck = AddToggle(page, "显存占用", "显示已用 / 总显存。", ref y, v => _state.ShowMemory = v);
        _tempCheck = AddToggle(page, "温度", "显示每块 GPU 的温度。", ref y, v => _state.ShowTemperature = v);
        y += 16;
        page.Controls.Add(SectionHeader("托盘", ref y));
        _menubarCheck = AddToggle(page, "显示最高 GPU 利用率", "托盘图标显示所有可见服务器中的最高利用率。", ref y, v => _state.MenuBarShowsUtilization = v);
        return page;
    }

    private CheckBox AddToggle(TabPage page, string title, string desc, ref int y, Action<bool> setter)
    {
        var cb = new CheckBox { Text = title, AutoSize = true, Location = new Point(24, y) };
        cb.CheckedChanged += (_, _) => { if (!_refreshing) setter(cb.Checked); };
        page.Controls.Add(cb);
        y += 24;
        page.Controls.Add(Hint(desc, ref y));
        y += 10;
        return cb;
    }

    // MARK: - 关于

    private TabPage BuildAboutTab()
    {
        var page = new TabPage("关于") { Padding = new Padding(20), BackColor = SystemColors.Window };

        var pic = new PictureBox
        {
            Image = Branding.GlyphBitmap(64, Theme.Green),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(72, 72),
            Location = new Point(244, 60),
        };
        var title = new Label { Text = "GPU 状态", Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true };
        var version = new Label { Text = $"版本 {AppState.Version}", ForeColor = SystemColors.GrayText, AutoSize = true };
        var desc = new Label
        {
            Text = "通过 SSH 监控远程服务器的 GPU 利用率、显存、温度与进程。",
            ForeColor = SystemColors.GrayText, AutoSize = false,
            Size = new Size(420, 40), TextAlign = ContentAlignment.TopCenter,
            Location = new Point(60, 200),
        };
        CenterX(title, 150);
        CenterX(version, 178);

        var quit = new Button { Text = "退出 GPU 状态", AutoSize = true };
        quit.Click += (_, _) => Application.Exit();
        page.Controls.Add(pic);
        page.Controls.Add(title);
        page.Controls.Add(version);
        page.Controls.Add(desc);
        page.Controls.Add(quit);
        page.Layout += (_, _) =>
        {
            CenterX(title, 150);
            CenterX(version, 178);
            quit.Location = new Point((page.ClientSize.Width - quit.Width) / 2, 520);
            pic.Location = new Point((page.ClientSize.Width - pic.Width) / 2, 60);
            desc.Location = new Point((page.ClientSize.Width - desc.Width) / 2, 200);
        };
        return page;

        void CenterX(Control c, int top) => c.Location = new Point((page.ClientSize.Width - c.Width) / 2, top);
    }

    // MARK: - 小控件工厂

    private static Label SectionHeader(string text, ref int y)
    {
        var l = new Label { Text = text, ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(12, y), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        y += 26;
        return l;
    }

    private static Label Hint(string text, ref int y)
    {
        var l = new Label { Text = text, ForeColor = SystemColors.GrayText, AutoSize = false, Size = new Size(500, 18), Location = new Point(44, y), Font = new Font("Segoe UI", 8.25f) };
        y += 20;
        return l;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(2, 4, 2, 4) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static int NearestIndex((string Label, double Value)[] options, double value)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < options.Length; i++)
        {
            var d = Math.Abs(options[i].Value - value);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        return best;
    }
}
