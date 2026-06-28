using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GpuStatus;

/// 悬停某块 GPU 时弹出的计算进程列表（对应 macOS 版 ProcessPopover）。
/// 以「不抢焦点」的方式显示，这样宿主面板不会因失焦而自动隐藏。
public sealed class ProcessPopupForm : Form
{
    private Theme.Palette _pal = Theme.PaletteFor(true);
    private GpuInfo? _gpu;
    private readonly Font _fHead, _fNorm, _fSmall, _fTiny, _fMono;

    private const int WidthBase = 330;
    private const int HeaderH = 36, RowH = 50, PadX = 14;

    private float DpiScale => DeviceDpi / 96f;
    private int S(double v) => (int)Math.Round(v * DpiScale);

    public ProcessPopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _fHead = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _fNorm = new Font("Segoe UI", 9f);
        _fSmall = new Font("Segoe UI", 8.25f);
        _fTiny = new Font("Segoe UI", 7.5f);
        _fMono = new Font("Consolas", 9.5f, FontStyle.Bold);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public void SetPalette(Theme.Palette pal) => _pal = pal;

    public void SetGpu(GpuInfo gpu)
    {
        _gpu = gpu;
        int h = S(HeaderH) + 1 + Math.Max(1, gpu.Processes.Count) * S(RowH) + S(6);
        ClientSize = new Size(S(WidthBase), h);
        Invalidate();
    }

    public void ShowNoActivate(Point location)
    {
        Location = location;
        if (!Visible)
        {
            Show(); // ShowWithoutActivation/WS_EX_NOACTIVATE 保证首次显示不抢焦点
        }
        else
        {
            // 切换悬停行时重新置顶：必须用 SWP_NOACTIVATE，否则会抢走宿主面板焦点，
            // 触发面板 OnDeactivate 而被自动隐藏（BringToFront 会激活窗口，故不能用）。
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
    }

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public void HidePopup()
    {
        if (Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var bg = new SolidBrush(_pal.Background))
            g.FillRectangle(bg, ClientRectangle);
        using (var border = new Pen(_pal.Divider))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        if (_gpu is not { } gpu) return;
        int left = S(PadX), right = Width - S(PadX);
        int y = 0;

        // 头部
        DrawText(g, "🧠", _fNorm, Theme.Green, new RectangleF(left, y, S(18), S(HeaderH)));
        DrawText(g, $"GPU {gpu.Index}", _fMono, _pal.Text, new RectangleF(left + S(20), y, S(70), S(HeaderH)));
        DrawText(g, gpu.ShortName, _fSmall, _pal.Secondary, new RectangleF(left + S(90), y, right - left - S(90) - S(70), S(HeaderH)));
        DrawText(g, $"{gpu.Processes.Count} 个进程", _fTiny, _pal.Secondary,
            new RectangleF(right - S(70), y, S(70), S(HeaderH)), StringAlignment.Far);
        y += S(HeaderH);
        using (var p = new Pen(_pal.Divider)) g.DrawLine(p, 0, y, Width, y);
        y += 1;

        if (gpu.Processes.Count == 0)
        {
            DrawText(g, "无计算进程", _fSmall, _pal.Secondary, new RectangleF(left, y, right - left, S(RowH)));
            return;
        }

        foreach (var p in gpu.Processes)
        {
            RenderProcRow(g, p, gpu, left, right, y);
            y += S(RowH);
        }
    }

    private void RenderProcRow(Graphics g, GpuProcess p, GpuInfo gpu, int left, int right, int y)
    {
        int mid = y + S(RowH) / 2;
        // 头像圈 + 用户首字母
        int av = S(30);
        var avRect = new Rectangle(left, mid - av / 2, av, av);
        using (var ab = new SolidBrush(Color.FromArgb(38, Theme.Green)))
            g.FillEllipse(ab, avRect);
        DrawText(g, Initials(p.User), _fTiny, Theme.Green, avRect, StringAlignment.Center);

        int tx = left + av + S(11);
        // 第一行：用户 + 进程短名
        var userW = g.MeasureString(p.User, _fNorm).Width;
        DrawText(g, p.User, _fNorm, _pal.Text, new RectangleF(tx, y + S(8), userW + S(2), S(16)));
        DrawText(g, p.ShortName, _fSmall, _pal.Secondary,
            new RectangleF(tx + userW + S(8), y + S(8), right - tx - userW - S(90), S(16)));

        // 第二行：显存占用条
        var barRect = new RectangleF(tx, y + S(26), S(160), S(4));
        DrawBar(g, barRect, (double)p.MemoryUsedMiB / Math.Max(1, gpu.MemoryTotalMiB), Theme.Green);

        // 运行时长
        if (p.RuntimeText is { } rt)
            DrawText(g, $"🕐 已运行 {rt}", _fTiny, _pal.Tertiary, new RectangleF(tx, y + S(32), S(180), S(14)));

        // 右侧：显存文本 + pid
        DrawText(g, MemText(p.MemoryUsedMiB), _fNorm, _pal.Text,
            new RectangleF(right - S(80), y + S(8), S(80), S(16)), StringAlignment.Far);
        DrawText(g, $"pid {p.Pid}", _fTiny, _pal.Tertiary,
            new RectangleF(right - S(80), y + S(26), S(80), S(14)), StringAlignment.Far);
    }

    private static string Initials(string user) =>
        user == "?" ? "?" : (user.Length <= 2 ? user : user[..2]).ToUpperInvariant();

    private static string MemText(int mib) =>
        mib >= 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GiB", mib / 1024.0)
            : $"{mib} MiB";

    private void DrawText(Graphics g, string s, Font f, Color c, RectangleF r,
        StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
    {
        using var br = new SolidBrush(c);
        using var sf = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = h,
            LineAlignment = v,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(s, f, br, r, sf);
    }

    private void DrawBar(Graphics g, RectangleF r, double fraction, Color tint)
    {
        float radius = r.Height / 2f;
        using (var track = new SolidBrush(_pal.Track))
        using (var tp = PanelForm.RoundedRect(r, radius))
            g.FillPath(track, tp);
        var clamped = Math.Max(0, Math.Min(1, fraction));
        if (clamped > 0)
        {
            float w = Math.Max(r.Height, (float)(clamped * r.Width));
            using var br = new SolidBrush(tint);
            using var fp = PanelForm.RoundedRect(new RectangleF(r.X, r.Y, w, r.Height), radius);
            g.FillPath(br, fp);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fHead.Dispose(); _fNorm.Dispose(); _fSmall.Dispose(); _fTiny.Dispose(); _fMono.Dispose();
        }
        base.Dispose(disposing);
    }
}
