# 发布为单文件 exe（自带 .NET 运行时，目标机免装）。用法：.\build.ps1 [Debug|Release]
param([string]$Config = "Release")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "==> dotnet publish ($Config, win-x64, single-file)"
dotnet publish -c $Config -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

$exe = Join-Path $PSScriptRoot "bin\$Config\net8.0-windows\win-x64\publish\GpuStatus.exe"
if (Test-Path $exe) {
    Write-Host ""
    Write-Host "[OK] 构建完成: $exe"
    Write-Host "     直接双击即可运行，图标会出现在系统托盘。"
} else {
    Write-Warning "未找到产物 exe，请检查上面的构建输出。"
}
