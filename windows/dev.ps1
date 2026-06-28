# 一键让改动在本机生效：退出旧实例 → 重新编译 → 把单文件 exe 放到当前目录 → 重新启动。
# 改完代码后跑这一条，托盘里立刻就是最新版本，无需手动重装。用法：.\dev.ps1 [Debug|Release]
param([string]$Config = "Debug")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "==> 退出正在运行的旧实例"
Get-Process GpuStatus -ErrorAction SilentlyContinue | Stop-Process -Force
# 等进程真正退出，释放对 exe 的文件锁
for ($i = 0; $i -lt 10; $i++) {
    if (-not (Get-Process GpuStatus -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 200
}

Write-Host "==> 重新构建（框架依赖单文件，增量约 2s）"
dotnet publish -c $Config -r win-x64 --self-contained false -p:PublishSingleFile=true --nologo -v quiet
# dotnet 非零退出不会触发 $ErrorActionPreference，手动拦截，避免编译失败时启动旧 exe
if ($LASTEXITCODE -ne 0) { Write-Error "构建失败（exit $LASTEXITCODE），已中止——未替换/启动 exe。"; exit 1 }

$exe = Join-Path $PSScriptRoot "bin\$Config\net8.0-windows\win-x64\publish\GpuStatus.exe"
if (-not (Test-Path $exe)) { Write-Warning "未找到产物 exe，请检查上面的构建输出。"; exit 1 }

# 把最新 exe 放到当前目录，方便随时双击观察
$dest = Join-Path $PSScriptRoot "GpuStatus.exe"
Copy-Item $exe $dest -Force

Write-Host "==> 启动新版本"
Start-Process $dest

Write-Host ""
Write-Host "[OK] 已生效：托盘里的 GpuStatus 现在是最新版本（exe: $dest）"
