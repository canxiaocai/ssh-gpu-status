# Server GPU Status · Windows

<p align="center">
  <strong>Windows 系统托盘 GPU 监控工具</strong>
</p>

<p align="center">
  C# / .NET 8 / WinForms。通过 SSH 把多台远程服务器的 GPU 状态收进系统托盘：利用率、显存、温度、占用进程、网络吞吐。
  与仓库里的 macOS 版功能对齐，远程命令与解析逻辑完全一致。
</p>

<p align="center">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-blue?logo=windows">
  <img alt="dotnet" src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-blue">
</p>

## 功能

- **托盘图标常驻显示**所有可见服务器中的最高 GPU 利用率（随任务栏深/浅色自动用白/黑）。
- **左键托盘**弹出监控面板：每台服务器、每块 GPU 一行，利用率 / 显存 / 温度配色随负载绿→黄→红过渡，并显示每台网络上下行速率。
- **悬停某块 GPU** 弹出该卡上的计算进程：PID、占用用户、显存、已运行时长。
- **自动读取** `%USERPROFILE%\.ssh\config` 里可直连的 `Host` 别名；也可在设置里手动添加自定义服务器（user / host / port / 密钥）。
- **开机自启**（写入注册表 `HKCU\...\Run`）、面板打开/关闭自动切换快/慢刷新、睡眠唤醒与断网恢复后立即刷新。

## 运行前提

- **Windows 10 1809+ / Windows 11**（自带 OpenSSH 客户端 `C:\Windows\System32\OpenSSH\ssh.exe`）。
- 本机能**免密** SSH 登录目标服务器，且目标上有 `nvidia-smi`。先在 PowerShell 里验证一条命令能成功：

  ```powershell
  ssh 你的主机别名 nvidia-smi
  ```

  若私钥带 passphrase，确保已加载到 ssh-agent：

  ```powershell
  Get-Service ssh-agent | Set-Service -StartupType Automatic
  Start-Service ssh-agent
  ssh-add $env:USERPROFILE\.ssh\id_ed25519
  ```

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（`winget install Microsoft.DotNet.SDK.8`）。在本目录（`windows/`）：

```powershell
# 开发运行
dotnet run

# 发布为单文件 exe（自带运行时，免装 .NET）
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# 产物：bin\Release\net8.0-windows\win-x64\publish\GpuStatus.exe
```

或直接运行 `.\build.ps1`（默认发布单文件 Release）。

## 运行时数据位置

- 设置：`%APPDATA%\GpuStatus\settings.json`（服务器列表、刷新间隔、指标开关等）
- 日志：`%LOCALAPPDATA%\GpuStatus\Logs\GpuStatus.log`

仓库与上述本地存储中**都不含任何主机/账号信息**——服务器全部在运行时从你本机的 `~/.ssh/config` 合并或手动添加。

## 与 macOS 版的差异

| 关注点 | macOS | Windows |
| --- | --- | --- |
| 形态 | 菜单栏 `LSUIElement` | 系统托盘 `NotifyIcon` |
| 摘要展示 | 菜单栏「logo + 利用率%」合成图 | 托盘方形图标里画利用率数字（托盘唯一常驻文字通道） |
| 开机自启 | ServiceManagement 登录项 | 注册表 `HKCU\...\Run` |
| 持久化 | `UserDefaults` | `%APPDATA%\GpuStatus\settings.json` |
| 排序 | 拖动 | 设置里「上移/下移」按钮 |
| 远程命令 / 解析 | — 完全一致 — | — 完全一致 — |

## 许可证

[MIT](../LICENSE)
