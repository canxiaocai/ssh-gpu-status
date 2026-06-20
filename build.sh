#!/bin/bash
# 用 SPM 编译并组装成菜单栏 .app（带 LSUIElement，无 Dock 图标）。
# 仅需 Command Line Tools，不需要完整版 Xcode。
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="Server GPU Status"
EXECUTABLE="GPUStatus"
BUNDLE_ID="com.cxc.gpustatus"
VERSION="0.1.0"
BUILD_CONFIG="${1:-release}"

echo "==> swift build -c $BUILD_CONFIG"
swift build -c "$BUILD_CONFIG"
BIN_DIR="$(swift build -c "$BUILD_CONFIG" --show-bin-path)"

APP_DIR="build/$APP_NAME.app"
echo "==> 组装 $APP_DIR"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

cp "$BIN_DIR/$EXECUTABLE" "$APP_DIR/Contents/MacOS/$EXECUTABLE"

# 菜单栏图标资源（模板图 @1x/@2x/@3x），供 NSImage(named:) 加载。
cp logos/gpu-pulse-template*.png "$APP_DIR/Contents/Resources/"

# App 图标（Finder / Dock / 关于 等场景）
cp logos/AppIcon.icns "$APP_DIR/Contents/Resources/AppIcon.icns"

cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>     <string>$APP_NAME</string>
    <key>CFBundleExecutable</key>      <string>$EXECUTABLE</string>
    <key>CFBundleIdentifier</key>      <string>$BUNDLE_ID</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleShortVersionString</key> <string>$VERSION</string>
    <key>CFBundleVersion</key>         <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>  <string>13.0</string>
    <key>LSUIElement</key>             <true/>
    <key>NSHighResolutionCapable</key> <true/>
    <key>CFBundleIconFile</key>        <string>AppIcon</string>
</dict>
</plist>
EOF

echo "==> ad-hoc 签名"
codesign --force --deep --sign - "$APP_DIR"

echo ""
echo "✅ 构建完成：$APP_DIR"
echo "   运行：open \"$APP_DIR\""
