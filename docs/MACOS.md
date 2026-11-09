# macOS 客户端

Windows 客户端是 WPF，macOS 客户端是 Avalonia；两者共用 `src/SnapTranslate.Core`（设置、翻译通道、提示词、文档翻译）。
这篇文档只讲 macOS 侧的平台相关部分。

## 功能对照

| 能力 | Windows (WPF) | macOS (Avalonia) |
| --- | --- | --- |
| 输入翻译 / 历史 | ✅ | ✅ |
| 文档翻译（TXT/Markdown/CSV/HTML/Office/PDF） | ✅ | ✅ |
| 截图 + 标注 + 贴图 | ✅ | ✅ 自绘浮层，见下文 |
| 截图 OCR / 截图翻译 | ✅ Windows.Media.Ocr | ✅ 系统 Vision（Swift 辅助程序） |
| 剪贴板历史 | ✅ | ✅ 轮询 NSPasteboard + SQLite |
| 全局快捷键 | ✅ 低级键盘钩子 | ✅ Carbon RegisterEventHotKey |
| 托盘常驻 | ✅ | ✅ Avalonia TrayIcon |
| 主窗口始终置顶 | ✅ | ⏳ 设置项已存在，Avalonia macOS 侧暂未生效 |

## 平台层

`src/SnapTranslate.Desktop/Platform/` 把「和系统打交道」的部分抽成契约，UI 只依赖接口：

| 文件 | 作用 |
| --- | --- |
| `Contracts.cs` | `IScreenCaptureService`、`ITextRecognizer`、`IClipboardMonitor`、`IGlobalHotkeyService` 以及 `PlatformServices` 定位器 |
| `PlatformBootstrap.cs` | 启动时按平台装配真实实现；非 macOS 保持空实现，方便在 Windows 上跑无头测试 |
| `MacScreenCaptureService.cs` | 调 `/usr/sbin/screencapture -x` 抓整屏 |
| `MacVisionTextRecognizer.cs` | 调应用包里的 `SnapTranslateVision`（Swift + Vision 框架） |
| `HotkeyGesture.cs` | 把 `Ctrl+Alt+F` 解析成 Carbon 的修饰键与虚拟键码（跨平台可测） |
| `CarbonHotkeyService.cs` | `RegisterEventHotKey` + `InstallEventHandler`，**不需要辅助功能权限** |

## 截图流程

```
StartCaptureAsync
  → PlatformServices.ScreenCapture.CaptureAsync()      抓整屏 PNG（隐藏主窗口后）
  → CaptureOverlayWindow.ShowAsync(owner, screen)     全屏浮层：框选 + 标注 + 工具条
  → CaptureOutcome { Image, Region, Action }          用户选的动作
  → 分发：复制 / 保存 / 贴图 / OCR / 翻译
```

- 标注（矩形、椭圆、箭头、画笔、文字、马赛克）先在浮层里画，最终由 `CaptureComposer` 一次性合成。
- 复制走 `IClipboardMonitor.CopyToClipboardAsync`，即 Swift 辅助程序写 NSPasteboard（文本/图片/文件都支持）。
- OCR 先把选区写成临时 PNG，再交给 `MacVisionTextRecognizer`，返回多行纯文本。
- 截图翻译把图片 + OCR 文本交给 `ScreenshotTranslationWindow`，翻译完成回调主窗写历史。

## 剪贴板历史

- 存储：`~/Library/Application Support/SnapTranslate/Data/clipboard.db`（SQLite，`Microsoft.Data.Sqlite`）。
- 监控：`MacPasteboardMonitor` 每 700ms 调用一次 `SnapTranslatePasteboard`，用 `changeCount` 判断是否有新内容。
- 图片会落成 PNG 存到 `Data/Images/<sha256>.png`，相同内容按 hash 去重。

## 系统权限

| 权限 | 什么时候要 | 在哪里开 |
| --- | --- | --- |
| 屏幕录制 | 第一次截图时 | 系统设置 → 隐私与安全性 → 屏幕录制 |
| 辅助功能 | **不需要**（Carbon 快捷键不受此限制） | — |

如果截图返回 null，先检查屏幕录制权限，再确认没有别的进程占着 `screencapture`。

## 快捷键映射

设置里的 `Ctrl` 在 macOS 上映射成 **Command**（`Cmd`），`Alt` 映射成 `Option`，`Shift` 不变。
解析与注册在 `HotkeyGesture` + `CarbonHotkeyService`，注册失败会把原因显示在设置页。

## 在 Windows 上开发与测试

平台相关代码全部在 `OperatingSystem.IsMacOS()` 后面，Windows 上可以完整编译并跑 UI 测试：

```powershell
dotnet build src\SnapTranslate.Desktop\SnapTranslate.Desktop.csproj -c Release
dotnet test tests\SnapTranslate.Desktop.Tests\SnapTranslate.Desktop.Tests.csproj -c Release
```

无头测试用 `Avalonia.Headless`，覆盖：窗口布局、截图几何与合成、标注撤销栈、剪贴板仓库（SQLite）、
快捷键解析、翻译结果窗口布局。**真正的屏幕抓取、Vision OCR、NSPasteboard 只能在 Mac 上验证。**

## 打包

```bash
bash scripts/build-macos-helper.sh          # 编译 Vision + Pasteboard 两个 Swift 辅助程序
dotnet publish src/SnapTranslate.Desktop/SnapTranslate.Desktop.csproj -c Release -r osx-arm64 --self-contained true
bash scripts/package-macos.sh osx-arm64 artifacts/publish/osx-arm64
```

应用包结构：`Snapshot.app/Contents/MacOS/SnapTranslate` + `Contents/Resources/SnapTranslateVision` + `SnapTranslatePasteboard`。

## 已知限制

- 没有 Apple 开发者签名，首次打开需要右键「打开」或 `xattr -dr com.apple.quarantine`。
- 主窗口「始终置顶」在 macOS 上还没接（Avalonia 的 `Topmost` 在 macOS 行为有限）。
- 多显示器：截图目前只抓主屏，浮层也只覆盖主屏。
- 剪贴板监控是轮询实现，700ms 内连续两次复制可能只记录最后一条。
