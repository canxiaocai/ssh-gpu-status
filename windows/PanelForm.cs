using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GpuStatus;

/// 托盘弹出的监控面板，自绘渲染（对应 macOS 版 MenuView.swift）。
/// 锚定在屏幕右下角任务栏附近，失焦自动隐藏；悬停某块 GPU 弹出进程列表。
public sealed class PanelForm : Form
{
    private readonly AppState _state;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private Theme.Palette _pal = Theme.PaletteFor(true);

    private readonly Font _fTitle, _fHost, _fNorm, _fSmall, _fTiny, _fMono, _fMonoNorm;

    // 自绘时记录的命中区域，供鼠标交互使用。
    private readonly List<(RectangleF rect, GpuInfo gpu, HostStatus host)> _gpuRects = new();
    private RectangleF _refreshRect, _settingsRect, _quitRect, _emptySettingsRect;
    private string? _hoverKey;

    private ProcessPopupForm? _popup;
    private int _contentWidth = 320;

    // 基准像素（96dpi），实际乘以 Scale。
    private const int PAD = 14, ROWHPAD = 6, COLGAP = 18, INNER = 7;
    private const int GpuW = 60, BarW = 64, PctW = 34, GibW = 58, TempW = 48;
    private const int MemColW = BarW + INNER + PctW + INNER + GibW; // 170
    private const int UtilColW = BarW + INNER + PctW;               // 105
    private const int HeaderH = 22, HostHdrH = 20, TblHdrH = 15, GpuRowH = 24, FooterH = 22;

    private float DpiScale => DeviceDpi / 96f;
    private int S(double v) => (int)Math.Round(v * DpiScale);

    public PanelForm(AppState state, Action openSettings, Action quit)
    {
        _state = state;
        _openSettings = openSettings;
        _quit = quit;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _fTitle = new Font("Segoe UI", 11f, FontStyle.Bold);
        _fHost = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _fNorm = new Font("Segoe UI", 9f);
        _fSmall = new Font("Segoe UI", 8.25f);
        _fTiny = new Font("Segoe UI", 7.5f);
        _fMono = new Font("Consolas", 9.5f, FontStyle.Bold);
        _fMonoNorm = new Font("Consolas", 9f);
    }

    // MARK: - 显示 / 隐藏

    public void ShowNearTray()
    {
        _pal = Theme.PaletteFor(Branding.IsTaskbarDark());
        UpdateContent();
        var wa = Screen.GetWorkingArea(Cursor.Position);
        int x = wa.Right - Width - S(8);
        int y = wa.Bottom - Height - S(8);
        Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
        Show();
        Activate();
        ForceForeground(); // 托盘点击不会自动把面板设为前台窗口，需强制夺取前台，否则鼠标一动就失活自隐
        _state.PanelDidOpen();
    }

    /// 从托盘弹出的窗口不会自动成为真正的前台窗口（Windows 的 SetForegroundWindow 限制），
    /// 导致鼠标一离开托盘图标、外壳重新夺回前台就触发 OnDeactivate 把面板隐藏。
    /// 这里用 AttachThreadInput 把自己挂到当前前台线程上，绕过限制真正取得前台。
    private void ForceForeground()
    {
        IntPtr hWnd = Handle;
        IntPtr fg = GetForegroundWindow();
        if (fg == hWnd) return;
        uint myThread = GetCurrentThreadId();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        if (fgThread != 0 && fgThread != myThread)
        {
            AttachThreadInput(myThread, fgThread, true);
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
            AttachThreadInput(myThread, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
        }
    }

    /// 重新测量内容、调整尺寸，并保持锚定在右下角。
    public void UpdateContent()
    {
        _contentWidth = ComputeWidth();
        int h;
        using (var g = CreateGraphics())
            h = Render(g, draw: false);
        ClientSize = new Size(_contentWidth, h);
        if (Visible)
        {
            var wa = Screen.GetWorkingArea(this);
            Location = new Point(wa.Right - Width - S(8), wa.Bottom - Height - S(8));
        }
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // 延后判定：弹出进程子窗口的瞬间会触发失活，但此刻前台焦点尚未稳定，
        // 同步读 GetForegroundWindow 会误判而隐藏。改为下一轮消息循环再决定。
        BeginInvoke(new Action(MaybeHide));
    }

    /// 真正决定是否隐藏：焦点仍在面板/进程弹窗上，或鼠标仍停在二者之上（悬停 GPU 的情形），都不隐藏。
    private void MaybeHide()
    {
        if (IsDisposed || !Visible) return;
        var fg = GetForegroundWindow();
        if (fg == Handle) return;
        if (_popup is { IsDisposed: false, IsHandleCreated: true } p && fg == p.Handle) return;
        if (CursorOverUs()) return;
        Hide();
    }

    private bool CursorOverUs()
    {
        var c = Cursor.Position;
        if (Bounds.Contains(c)) return true;
        return _popup is { IsDisposed: false, Visible: true } p && p.Bounds.Contains(c);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            HidePopup();
            _hoverKey = null;
            _state.PanelDidClose();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    // 不抢任务栏焦点闪烁。
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW（不进 Alt-Tab）
            return cp;
        }
    }

    private int ComputeWidth()
    {
        var m = _state.MetricVisibility;
        double w = GpuW;
        int cols = 1;
        if (m.Memory) { w += MemColW; cols++; }
        if (m.Utilization) { w += UtilColW; cols++; }
        if (m.Temperature) { w += TempW; cols++; }
        w += (cols - 1) * COLGAP + 2 * ROWHPAD;
        w = Math.Max(w + 2 * PAD, 320);
        return S(w);
    }

    // MARK: - 渲染

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var bg = new SolidBrush(_pal.Background))
            g.FillRectangle(bg, ClientRectangle);
        // 细边框
        using (var border = new Pen(_pal.Divider))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        Render(g, draw: true);
    }

    /// 自顶向下布局；draw=false 仅推进 y 用于测高，draw=true 实际绘制并记录命中区。返回总高度。
    private int Render(Graphics g, bool draw)
    {
        if (draw) _gpuRects.Clear();
        var m = _state.MetricVisibility;
        int left = S(PAD);
        int right = _contentWidth - S(PAD);
        int y = S(PAD);

        // ---- 面板头：logo + 标题 + 刷新 ----
        if (draw)
        {
            using var glyph = Branding.GlyphBitmap(S(16), Theme.Green);
            g.DrawImage(glyph, left, y, S(16), S(16));
            DrawText(g, "GPU 状态", _fTitle, _pal.Text,
                new RectangleF(left + S(22), y, right - left - S(60), S(HeaderH)));
            _refreshRect = new RectangleF(right - S(22), y, S(22), S(HeaderH));
            DrawText(g, "⟳", _fTitle, _state.VisibleServers.Count == 0 ? _pal.Tertiary : _pal.Secondary,
                _refreshRect, StringAlignment.Center);
        }
        y += S(HeaderH) + S(8);
        if (draw) DrawDivider(g, left, right, y);
        y += S(1) + S(8);

        // ---- 内容 ----
        var statuses = _state.OrderedStatuses;
        if (statuses.Count == 0)
        {
            string msg = _state.Servers.Count == 0 ? "未在 ~/.ssh/config 中找到主机" : "未显示任何服务器";
            if (draw) DrawText(g, msg, _fNorm, _pal.Secondary, new RectangleF(left, y, right - left, S(18)));
            y += S(18) + S(6);
            _emptySettingsRect = new RectangleF(left, y, S(120), S(18));
            if (draw) DrawText(g, "打开设置…", _fNorm, Theme.Green, _emptySettingsRect);
            y += S(18) + S(6);
        }
        else
        {
            for (int hi = 0; hi < statuses.Count; hi++)
            {
                if (hi > 0) y += S(14);
                y = RenderHost(g, draw, statuses[hi], m, left, right, y);
            }
        }

        // ---- 页脚：设置 / 退出 ----
        y += S(8);
        if (draw) DrawDivider(g, left, right, y);
        y += S(1) + S(8);
        _settingsRect = new RectangleF(left, y, S(80), S(FooterH));
        _quitRect = new RectangleF(right - S(60), y, S(60), S(FooterH));
        if (draw)
        {
            DrawText(g, "⚙ 设置", _fNorm, _pal.Text, _settingsRect);
            DrawText(g, "退出", _fNorm, _pal.Secondary, _quitRect, StringAlignment.Far);
        }
        y += S(FooterH) + S(PAD);

        return y;
    }

    private int RenderHost(Graphics g, bool draw, HostStatus st, MetricVisibility m, int left, int right, int y)
    {
        // 主机头：图标 + 名称 + 型号 + 网速 + 状态
        if (draw)
        {
            DrawText(g, "🖥", _fSmall, Theme.Green, new RectangleF(left, y, S(16), S(HostHdrH)));
            float nameX = left + S(18);
            string name = st.Name;
            var nameW = g.MeasureString(name, _fHost).Width;
            DrawText(g, name, _fHost, _pal.Text, new RectangleF(nameX, y, right - nameX, S(HostHdrH)));

            string? model = UniformModel(st);
            if (model != null && st.Gpus.Count > 0)
                DrawText(g, $"· {st.Gpus.Count}× {model}", _fSmall, _pal.Secondary,
                    new RectangleF(nameX + nameW + S(6), y, right - nameX - nameW - S(6), S(HostHdrH)));

            // 右侧：状态指示 + 网速
            float rx = right;
            string status = StatusText(st);
            if (status.Length > 0)
            {
                var w = g.MeasureString(status, _fTiny).Width;
                DrawText(g, status, _fTiny, StatusColor(st), new RectangleF(rx - w, y, w, S(HostHdrH)), StringAlignment.Far);
                rx -= w + S(8);
            }
            if (st.NetRxBytesPerSec != null || st.NetTxBytesPerSec != null)
            {
                string net = $"↓ {NetFormat.Speed(st.NetRxBytesPerSec ?? 0)}   ↑ {NetFormat.Speed(st.NetTxBytesPerSec ?? 0)}";
                var w = g.MeasureString(net, _fTiny).Width;
                DrawText(g, net, _fTiny, _pal.Secondary, new RectangleF(rx - w, y, w, S(HostHdrH)), StringAlignment.Far);
            }
        }
        y += S(HostHdrH) + S(6);

        // 内容：错误 / 加载 / 无数据 / 表格
        if (st.ErrorMessage is { } err)
        {
            if (draw)
            {
                DrawText(g, err, _fSmall, Theme.Red, new RectangleF(left, y, right - left, S(18)));
                DrawText(g, $"确保能免密 `ssh {st.Name}`，且远程有 nvidia-smi", _fTiny, _pal.Secondary,
                    new RectangleF(left, y + S(18), right - left, S(16)));
            }
            y += S(38);
        }
        else if (st.Gpus.Count == 0)
        {
            string txt = st.IsLoading ? "读取中…" : "无 GPU 数据";
            if (draw) DrawText(g, txt, _fSmall, _pal.Secondary, new RectangleF(left, y, right - left, S(18)));
            y += S(18);
        }
        else
        {
            // 表头
            if (draw)
            {
                int cx = left + S(ROWHPAD);
                DrawText(g, "GPU", _fTiny, _pal.Tertiary, new RectangleF(cx, y, S(GpuW), S(TblHdrH)));
                cx += S(GpuW) + S(COLGAP);
                if (m.Memory) { DrawText(g, "显存", _fTiny, _pal.Tertiary, new RectangleF(cx, y, S(MemColW), S(TblHdrH))); cx += S(MemColW) + S(COLGAP); }
                if (m.Utilization) { DrawText(g, "利用率", _fTiny, _pal.Tertiary, new RectangleF(cx, y, S(UtilColW), S(TblHdrH))); cx += S(UtilColW) + S(COLGAP); }
                if (m.Temperature) { DrawText(g, "温度", _fTiny, _pal.Tertiary, new RectangleF(cx, y, S(TempW), S(TblHdrH))); }
            }
            y += S(TblHdrH);
            if (draw) DrawDivider(g, left + S(ROWHPAD), right - S(ROWHPAD), y);
            y += S(1) + S(3);

            foreach (var gpu in st.Gpus)
            {
                var rowRect = new RectangleF(left, y, right - left, S(GpuRowH));
                if (draw)
                {
                    string key = HoverKey(st, gpu);
                    if (_hoverKey == key)
                    {
                        using var hb = new SolidBrush(_pal.HoverBg);
                        using var hp = RoundedRect(new RectangleF(left, y + S(1), right - left, S(GpuRowH) - S(2)), S(6));
                        g.FillPath(hb, hp);
                    }
                    RenderGpuRow(g, gpu, m, left, y);
                    _gpuRects.Add((rowRect, gpu, st));
                }
                y += S(GpuRowH);
            }
        }
        return y;
    }

    private void RenderGpuRow(Graphics g, GpuInfo gpu, MetricVisibility m, int left, int y)
    {
        int cx = left + S(ROWHPAD);
        int cyMid = y + S(GpuRowH) / 2;

        // GPU 标签：忙碌点 + "GPU N"
        var dot = gpu.IsBusy ? Theme.Green : _pal.Tertiary;
        using (var db = new SolidBrush(dot))
            g.FillEllipse(db, cx, cyMid - S(3), S(6), S(6));
        DrawText(g, $"GPU {gpu.Index}", _fMono, _pal.Text,
            new RectangleF(cx + S(10), y, S(GpuW) - S(10), S(GpuRowH)));
        cx += S(GpuW) + S(COLGAP);

        if (m.Memory)
        {
            var color = Theme.Memory(gpu.MemoryPercent);
            var barRect = new RectangleF(cx, cyMid - S(3), S(BarW), S(6));
            DrawBar(g, barRect, gpu.MemoryFraction, color);
            DrawText(g, $"{gpu.MemoryPercent}%", _fNorm, color,
                new RectangleF(cx + S(BarW) + S(INNER), y, S(PctW), S(GpuRowH)), StringAlignment.Far);
            DrawText(g, gpu.MemoryGiBText, _fTiny, _pal.Secondary,
                new RectangleF(cx + S(BarW) + S(INNER) + S(PctW) + S(INNER), y, S(GibW), S(GpuRowH)));
            cx += S(MemColW) + S(COLGAP);
        }

        if (m.Utilization)
        {
            var util = gpu.Utilization;
            var color = util.HasValue ? Theme.Utilization(util.Value) : _pal.Secondary;
            var barRect = new RectangleF(cx, cyMid - S(3), S(BarW), S(6));
            DrawBar(g, barRect, util.HasValue ? util.Value / 100.0 : 0, color);
            DrawText(g, util.HasValue ? $"{util.Value}%" : "—", _fNorm, color,
                new RectangleF(cx + S(BarW) + S(INNER), y, S(PctW), S(GpuRowH)), StringAlignment.Far);
            cx += S(UtilColW) + S(COLGAP);
        }

        if (m.Temperature)
        {
            var temp = gpu.Temperature;
            var color = temp.HasValue ? Theme.Temperature(temp.Value) : _pal.Secondary;
            DrawText(g, temp.HasValue ? $"{temp.Value}°C" : "—", _fNorm, color,
                new RectangleF(cx, y, S(TempW), S(GpuRowH)));
        }
    }

    // MARK: - 交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? hit = null;
        GpuInfo? gpu = null;
        RectangleF rect = default;
        foreach (var (r, gp, host) in _gpuRects)
        {
            if (r.Contains(e.Location))
            {
                hit = HoverKey(host, gp);
                gpu = gp;
                rect = r;
                break;
            }
        }
        if (hit != _hoverKey)
        {
            _hoverKey = hit;
            Invalidate();
            if (gpu != null && gpu.Processes.Count > 0)
                ShowPopup(gpu, rect);
            else
                HidePopup();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // 移到子弹窗上不算离开行；只有移出整个面板才清。
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
        {
            if (_hoverKey != null) { _hoverKey = null; Invalidate(); }
            HidePopup();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_refreshRect.Contains(e.Location)) { _ = _state.RefreshAllAsync(); return; }
        if (_emptySettingsRect.Contains(e.Location) && _state.OrderedStatuses.Count == 0) { Hide(); _openSettings(); return; }
        if (_settingsRect.Contains(e.Location)) { Hide(); _openSettings(); return; }
        if (_quitRect.Contains(e.Location)) { _quit(); return; }
    }

    private void ShowPopup(GpuInfo gpu, RectangleF rowRect)
    {
        _popup ??= new ProcessPopupForm();
        _popup.SetPalette(_pal);
        _popup.SetGpu(gpu);
        // 弹窗放在面板左侧，与该行垂直对齐。
        var rowScreen = PointToScreen(new Point((int)rowRect.X, (int)rowRect.Y));
        int px = Left - _popup.Width - S(6);
        int py = rowScreen.Y - S(8);
        var wa = Screen.GetWorkingArea(this);
        if (px < wa.Left) px = Right + S(6);
        py = Math.Max(wa.Top, Math.Min(py, wa.Bottom - _popup.Height));
        _popup.ShowNoActivate(new Point(px, py));
    }

    private void HidePopup() => _popup?.HidePopup();

    // MARK: - 绘图辅助

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

    private void DrawDivider(Graphics g, int x1, int x2, int y)
    {
        using var p = new Pen(_pal.Divider);
        g.DrawLine(p, x1, y, x2, y);
    }

    private void DrawBar(Graphics g, RectangleF r, double fraction, Color tint)
    {
        float radius = r.Height / 2f;
        using (var track = new SolidBrush(_pal.Track))
        using (var tp = RoundedRect(r, radius))
            g.FillPath(track, tp);
        var clamped = Math.Max(0, Math.Min(1, fraction));
        if (clamped > 0)
        {
            float w = Math.Max(r.Height, (float)(clamped * r.Width));
            using var br = new SolidBrush(tint);
            using var fp = RoundedRect(new RectangleF(r.X, r.Y, w, r.Height), radius);
            g.FillPath(br, fp);
        }
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        float d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string HoverKey(HostStatus st, GpuInfo gpu) => $"{st.ServerId}#{gpu.Index}";

    private static string? UniformModel(HostStatus st)
    {
        if (st.Gpus.Count == 0) return null;
        var first = st.Gpus[0].ShortName;
        return st.Gpus.All(g => g.ShortName == first) ? first : null;
    }

    private static string StatusText(HostStatus st)
    {
        if (st.IsLoading && st.Gpus.Count == 0) return "…";
        if (st.ErrorMessage != null) return "⚠";
        if (st.LastUpdated is { } t) return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return "";
    }

    private Color StatusColor(HostStatus st) =>
        st.ErrorMessage != null ? Theme.Red : _pal.Secondary;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup?.Dispose();
            _fTitle.Dispose(); _fHost.Dispose(); _fNorm.Dispose();
            _fSmall.Dispose(); _fTiny.Dispose(); _fMono.Dispose(); _fMonoNorm.Dispose();
        }
        base.Dispose(disposing);
    }
}
