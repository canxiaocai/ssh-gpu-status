# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A GPU-status monitor for remote servers: it runs `nvidia-smi` over SSH and shows utilization,
memory, temperature, per-GPU compute processes, and per-host network throughput in a popover
panel, plus the highest utilization across all servers in the menu-bar / tray icon. There are
**two independent, feature-aligned implementations** in this repo, each using its platform's
native stack. The READMEs and most code comments are in Chinese.

## Repository layout

```
macos/      Swift 6 / SwiftUI + AppKit, SwiftPM — menu-bar app (LSUIElement). The original.
windows/    C# / .NET 8 / WinForms — system-tray app. Port of the macOS version.
docs/       Shared screenshots + HTML mockup (illustrative data).
README.md   Top-level overview linking both platforms.
```

The two share the **same design and remote command**: assemble one shell command that queries
GPUs / compute processes / network counters, then parse it client-side and compute network rates
from the delta between two polls. Only the local UI and system glue differ (menu bar ↔ tray,
login item ↔ registry autostart, `UserDefaults` ↔ JSON settings file). When changing behavior,
keep the two ports in sync — especially `GpuMonitor`'s remote command and the parsing rules.

No host/account data lives in the repo or in local storage on either platform — SSH config
aliases are read at runtime (`~/.ssh/config` on macOS, `%USERPROFILE%\.ssh\config` on Windows),
and the chosen server list (incl. custom connections) is persisted locally, not committed.

## macOS (`macos/`)

**macOS-only** — links AppKit, ServiceManagement, and SwiftUI menu-bar APIs (macOS 13+).
`swift build` and the build scripts will not run on Windows/Linux. Build, run, and manual
testing must happen on a macOS machine with Command Line Tools (full Xcode not required).

```bash
cd macos
./build.sh [release|debug]   # SwiftPM build → assembles "build/Server GPU Status.app"
                             #   (Info.plist with LSUIElement, copies icon resources, ad-hoc codesign)
./dev.sh   [release|debug]   # kill running instance → build.sh → relaunch (one-step hot reload)
swift build -c release       # raw compile only (no .app bundle)
open "build/Server GPU Status.app"
```

- No test suite and no linter. Runtime log: `~/Library/Logs/GPUStatus.log` (`Log.write(...)`).
- Bundle id `com.cxc.gpustatus`; version hardcoded in `macos/build.sh` (`VERSION`).
- Shells out to `/usr/bin/ssh` with `BatchMode=yes`; targets must be reachable passwordlessly.

### macOS architecture

Single executable target, `macos/Sources/GPUStatus/`. A timer in `AppState` triggers
`GPUMonitor.fetch` per visible server → `@Published` state updates → SwiftUI panel and menu-bar
icon re-render.

- **`GPUStatusApp.swift`** — `@main`. Empty `Settings` scene; real work in `AppDelegate`, which
  manages the `NSStatusItem` **manually** rather than via `MenuBarExtra` (which measures its label
  only once at launch, so the dynamic "logo + utilization%" title would never refresh). Subscribes
  to `state.objectWillChange` (deferred to the next runloop) to redraw the icon.
- **`AppState.swift`** — `@MainActor ObservableObject`, single source of truth. Polling cadence
  (fast when panel open, slow when closed; `startPolling()` restarts + forces immediate refresh;
  wake/network observers call it). Server merge with `~/.ssh/config`; deleting a config alias
  records it in `ignoredAliases`; `reloadHosts()` clears that. One-time migration from legacy
  `enabledHosts`/`selectedHost`. `refreshAll()` fans out with a `TaskGroup`.
- **`GPUMonitor.swift`** — composes the remote shell command and parses it. Sections split by
  `@@@PROC@@@`, `@@@NET@@@`, `@@@NETDEV@@@`. Process names can contain commas → fixed leading/
  trailing columns, re-join the middle. Bad GPU rows skipped; `[N/A]` → `nil`; process/network
  parts best-effort (`|| true`). Net rates computed client-side in `AppState.updateNetRate`.
- **`SSHConfig.swift`** — minimal `~/.ssh/config` parser (skips `*`/`?`/`!` patterns).
- **`Models.swift`** — value types and formatting helpers.
- **`Branding.swift`** — composites logo + utilization into a single template `NSImage` (status
  bar drops `Text` if given both image and text).
- **`Theme.swift`** — continuous green→yellow→red HSB interpolation; per-metric thresholds.
- **UI:** `MenuView.swift` (popover, fixed column `Layout`), `SettingsView.swift` (4 tabs),
  `SettingsWindow.swift` (temporarily switches activation policy to show a focusable window).
- **`Log.swift`** — minimal serialized file logger.

## Windows (`windows/`)

**Windows-only** (10 1809+/11) — WinForms + registry + `SystemEvents`. Needs the .NET 8 SDK
(`winget install Microsoft.DotNet.SDK.8`). Uses the built-in OpenSSH client
(`C:\Windows\System32\OpenSSH\ssh.exe`).

```powershell
cd windows
dotnet run                   # dev run
./dev.ps1   [Debug|Release]  # hot reload: kill instance → publish → drop GpuStatus.exe in windows/ → relaunch
./build.ps1 [Release|Debug]  # publish a self-contained single-file exe (win-x64)
```

**Hot-reload workflow (do this after every Windows code change):** run `./dev.ps1` from
`windows/`. It stops the running instance, does a framework-dependent single-file publish
(`-p:PublishSingleFile=true --self-contained false`, incremental ~2s), copies the resulting
**`windows/GpuStatus.exe`** into the project directory so the latest build is always one
double-click away, then relaunches it in the tray. Default config is `Debug` for fast iteration.
`windows/GpuStatus.exe` (+ `.pdb`) is git-ignored — it's a throwaway dev artifact, not committed.
This is the Windows analogue of macOS `dev.sh`; prefer it over `dotnet run` so the user can
immediately observe the change in the tray app.

- No test suite. Settings: `%APPDATA%\GpuStatus\settings.json`. Log:
  `%LOCALAPPDATA%\GpuStatus\Logs\GpuStatus.log` (`Log.Write(...)`).
- Namespace `GpuStatus`; version in `GpuStatus.csproj`.

### Windows architecture

One project, file-per-type, mirroring the macOS sources:

- **`Program.cs`** — `[STAThread]` entry, single-instance `Mutex`, `Application.Run(new TrayContext())`.
- **`TrayContext.cs`** (≈ `AppDelegate`) — owns the `NotifyIcon`, context menu, panel + settings
  windows; subscribes to `AppState.Changed` to redraw the tray icon and refresh the panel.
- **`AppState.cs`** — single source of truth. **Polling uses a UI-thread `System.Windows.Forms.Timer`**
  with a 250ms base tick (not async-from-constructor, which would capture the wrong
  `SynchronizationContext`); it fetches when `EffectiveInterval` elapsed, on first tick, or when the
  `volatile _wake` flag is set by the (any-thread) power/network handlers. `StartPolling()` cancels
  in-flight fetches via `_fetchCts` and forces an immediate fetch. Launch-at-login = `HKCU\...\Run`.
- **`GpuMonitor.cs`** — same remote command + parsing as macOS; runs `ssh.exe` via `ProcessStartInfo`
  with `ArgumentList` (the multi-line command is one argument), `CreateNoWindow`.
- **`SshConfig.cs`, `AppSettings.cs`, `Models.cs`, `Theme.cs`, `Log.cs`** — ports of their macOS
  counterparts. `Theme` adds a light/dark `Palette` for the owner-drawn panel.
- **`Branding.cs`** — renders the square tray icon (utilization number, or a chip/pulse glyph),
  theme-aware; builds a PNG-compressed `.ico` in memory so the `Icon` owns its data.
- **UI (owner-drawn):** `PanelForm.cs` (popover, anchored bottom-right, auto-hides on deactivate;
  `Render(g, draw)` does both measure and paint), `ProcessPopupForm.cs` (hover process list, shown
  `WS_EX_NOACTIVATE` so it doesn't steal focus from the panel). `SettingsForm.cs` (4-tab,
  standard controls; reorder via up/down buttons) and `AddServerForm.cs` use normal WinForms controls.

Threading model: all state mutations happen on the UI thread; `GpuMonitor` does I/O on background
threads (`ConfigureAwait(false)`) and per-result `await`s resume on the UI thread to mutate state
and raise `Changed`.
