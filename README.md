# Server GPU Status

<p align="center">
  <img src="macos/logos/AppIcon.iconset/icon_256x256.png" width="120" alt="Server GPU Status icon">
</p>

<p align="center">
  <strong>把多台远程服务器的 GPU 状态收进系统托盘 / 菜单栏</strong>
</p>

<p align="center">
  通过 SSH 跑 <code>nvidia-smi</code>，一个面板纵览所有服务器的利用率、显存、温度、占用进程与网络吞吐，
  托盘图标常驻显示全局最高利用率。
</p>

<p align="center">
  <img alt="macOS" src="https://img.shields.io/badge/macOS%2013%2B-Swift%206-black?logo=apple">
  <img alt="Windows" src="https://img.shields.io/badge/Windows%2010%2B-.NET%208-blue?logo=windows">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-blue">
</p>

## 两个平台实现

本仓库包含两套独立实现，功能对齐，各自使用所在平台最原生的技术：

| 平台 | 目录 | 技术栈 | 形态 |
| --- | --- | --- | --- |
| **macOS** 13+ | [`macos/`](macos) | Swift 6 · SwiftUI + AppKit · SwiftPM | 菜单栏 App（`LSUIElement`） |
| **Windows** 10+ | [`windows/`](windows) | C# · .NET 8 · WinForms | 系统托盘 App（无任务栏窗口） |

两端共享同一套设计与远程命令：在远端组装一条 shell 命令查询 GPU / 计算进程 / 网络计数，
客户端解析并按两次轮询差值算网速。差异只在本地的 UI 与系统集成层（菜单栏 ↔ 系统托盘、
登录项 ↔ 注册表自启、`UserDefaults` ↔ JSON 配置文件）。

## 截图（macOS 面板示意）

| 深色 | 浅色 |
| --- | --- |
| ![深色面板](docs/images/panel-dark.png) | ![浅色面板](docs/images/panel-light.png) |

> 以上为演示示例，主机名 / 用户名 / 占用数据均为虚构。Windows 版界面以 WinForms 自绘还原同样的信息布局。

## 功能亮点（两端一致）

- **多服务器并发监控**：同时拉取所有服务器，每块 GPU 一行显示利用率、显存、温度，配色随负载绿→黄→红过渡。
- **托盘 / 菜单栏摘要**：图标上常驻显示所有可见服务器中的最高 GPU 利用率。
- **进程与占用用户**：悬停某块 GPU 弹出该卡上的计算进程，含 PID、占用用户、显存与已运行时长。
- **每台网络吞吐**：展示每台服务器默认网卡的实时上下行速率。
- **自动读取 SSH 配置**：扫描 `~/.ssh/config`（Windows 为 `%USERPROFILE%\.ssh\config`）里可直连的 `Host` 别名，也支持手动添加自定义服务器。仓库与本地存储中**不含任何主机或账号信息**。
- **省心的刷新策略**：面板打开时快速刷新、关闭后降速；睡眠唤醒 / 断网恢复后自动立即刷新。

## 通用前提

- 目标服务器上有 `nvidia-smi`。
- 本机能**免密** SSH 登录目标服务器（密钥已加入 ssh-agent）。验证一条命令能成功，App 就能工作：

  ```bash
  ssh 你的主机别名 nvidia-smi
  ```

## 各平台构建与安装

- **macOS** → 见 [`macos/README.md`](macos/README.md)
- **Windows** → 见 [`windows/README.md`](windows/README.md)

## 许可证

[MIT](LICENSE)
