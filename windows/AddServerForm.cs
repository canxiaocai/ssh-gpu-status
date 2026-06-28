using System.Drawing;
using System.Globalization;
using System.IO;

namespace GpuStatus;

/// 添加自定义服务器（仅密钥/身份文件认证）。对应 macOS 版 AddServerView。
public sealed class AddServerForm : Form
{
    private readonly AppState _state;
    private readonly TextBox _name, _user, _host, _port, _identity;
    private readonly Button _ok;

    public AddServerForm(AppState state)
    {
        _state = state;

        Text = "添加服务器";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(440, 300);

        int labelX = 20, fieldX = 120, fieldW = 300, y = 20, rowH = 34;

        _name = AddRow("名称（可选）", "如 lab-a100", labelX, fieldX, fieldW, ref y, rowH);
        _user = AddRow("用户名", "如 root", labelX, fieldX, fieldW, ref y, rowH);
        _host = AddRow("主机", "IP 或域名", labelX, fieldX, fieldW, ref y, rowH);
        _port = AddRow("端口", "22", labelX, fieldX, 80, ref y, rowH);
        _port.Text = "22";

        // 身份文件 + 选择按钮
        Controls.Add(new Label { Text = "身份文件（可选）", AutoSize = true, Location = new Point(labelX, y + 4) });
        _identity = new TextBox { Location = new Point(fieldX, y), Width = fieldW - 80, PlaceholderText = "~/.ssh/id_ed25519" };
        var browse = new Button { Text = "选择…", Location = new Point(fieldX + fieldW - 74, y - 1), Width = 74 };
        browse.Click += (_, _) => BrowseIdentity();
        Controls.Add(_identity);
        Controls.Add(browse);
        y += rowH;

        Controls.Add(new Label
        {
            Text = "仅支持密钥/身份文件认证。留空则用 ssh-agent 或默认密钥。",
            ForeColor = SystemColors.GrayText, AutoSize = false,
            Size = new Size(fieldW + 60, 32), Location = new Point(labelX, y + 4),
            Font = new Font("Segoe UI", 8.25f),
        });

        _ok = new Button { Text = "添加", DialogResult = DialogResult.None, Location = new Point(250, 256), Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(340, 256), Width = 80 };
        _ok.Click += (_, _) => Save();
        Controls.Add(_ok);
        Controls.Add(cancel);
        AcceptButton = _ok;
        CancelButton = cancel;

        foreach (var t in new[] { _user, _host, _port }) t.TextChanged += (_, _) => UpdateOkState();
        UpdateOkState();
    }

    private TextBox AddRow(string label, string placeholder, int labelX, int fieldX, int fieldW, ref int y, int rowH)
    {
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(labelX, y + 4) });
        var tb = new TextBox { Location = new Point(fieldX, y), Width = fieldW, PlaceholderText = placeholder };
        Controls.Add(tb);
        y += rowH;
        return tb;
    }

    private void UpdateOkState()
    {
        _ok.Enabled = _user.Text.Trim().Length > 0
            && _host.Text.Trim().Length > 0
            && int.TryParse(_port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private void Save()
    {
        if (!_ok.Enabled) return;
        var port = int.TryParse(_port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 22;
        _state.AddCustomServer(_name.Text, _user.Text, _host.Text, port,
            string.IsNullOrWhiteSpace(_identity.Text) ? null : _identity.Text.Trim());
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseIdentity()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择身份文件（私钥）",
            CheckFileExists = true,
        };
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshDir)) dlg.InitialDirectory = sshDir;
        if (dlg.ShowDialog(this) == DialogResult.OK) _identity.Text = dlg.FileName;
    }
}
