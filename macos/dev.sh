#!/bin/bash
# 一键让改动在本机生效：退出旧实例 → 重新编译打包 → 重新启动。
# 改完代码后跑这一条，菜单栏里立刻就是最新版本，无需手动重装。
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="Server GPU Status"
EXECUTABLE="GPUStatus"

echo "==> 退出正在运行的旧实例"
pkill -x "$EXECUTABLE" 2>/dev/null || true
# 等待进程真正退出，避免还占用菜单栏
for _ in 1 2 3 4 5; do
    pgrep -x "$EXECUTABLE" >/dev/null 2>&1 || break
    sleep 0.3
done

echo "==> 重新构建"
./build.sh "${1:-release}"

echo "==> 启动新版本"
open "build/$APP_NAME.app"

echo ""
echo "✅ 已生效：菜单栏中的 $APP_NAME 现在是最新版本"
