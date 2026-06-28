# Logo 资源

## App 图标（Finder / Dock / 关于）

| 文件 | 说明 |
|---|---|
| `AppIcon.iconset/` | 所有尺寸的源 PNG（16→1024px，含 @2x） |
| `AppIcon.icns` | 由 `iconutil` 从 iconset 生成，`build.sh` 打进 bundle |

替换图标：把 iconset 里的 PNG 换成新设计，然后重新生成 icns：

```bash
iconutil --convert icns logos/AppIcon.iconset --output logos/AppIcon.icns
./dev.sh
```

---

## 菜单栏图标（Status Item）

GPU 芯片 + 脉冲波形，用作菜单栏（status item）图标。

命名遵循 Apple 松散资源（loose bundle resource）约定，`NSImage(named:)` 会按屏幕缩放自动挑选合适分辨率：

| 文件 | 尺寸 | 用途 |
|---|---|---|
| `gpu-pulse-template.png`     | 18×18 (@1x) | **模板图**（黑色 + alpha），macOS 自动按明暗/高亮着色 |
| `gpu-pulse-template@2x.png`  | 36×36 (@2x) | 同上，Retina |
| `gpu-pulse-template@3x.png`  | 54×54 (@3x) | 同上，三倍屏 |
| `gpu-pulse-white@2x.png`     | 36×36       | 纯白填充变体，**当前未使用** |

约定：

- 模板图必须是黑色像素 + 透明背景；代码里设 `isTemplate = true`，系统据此在菜单栏自动反色。
- `build.sh` 会把 `gpu-pulse-template*.png` 拷进 `GPU Status.app/Contents/Resources/`。
