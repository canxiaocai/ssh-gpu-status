namespace GpuStatus;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // 调试用：直接打开设置窗口（便于核对布局），不进托盘、不做单实例限制。
        if (args.Length > 0 && args[0] == "--settings")
        {
            using var s = new AppState();
            var form = new SettingsForm(s);
            if (args.Length > 1 && int.TryParse(args[1], out var tab)) form.SelectTab(tab);
            Application.Run(form);
            return;
        }

        // 单实例：已在运行则直接退出，避免托盘里出现两个图标。
        using var mutex = new Mutex(initiallyOwned: true, "ServerGpuStatus_SingleInstance", out var isNew);
        if (!isNew) return;

        Application.Run(new TrayContext());

        GC.KeepAlive(mutex);
    }
}
