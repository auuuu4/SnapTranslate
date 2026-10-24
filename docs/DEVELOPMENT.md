# 开发说明

## 环境

- .NET SDK **8.0.424**（`global.json` 锁定，`rollForward` 为 `latestPatch`）
- Windows 10/11 才能构建 WPF 客户端；macOS 客户端在 macOS 或 Windows 都能编译
- 首次构建需要联网还原 NuGet 包

## 常用命令

```powershell
# 构建 Windows 客户端
dotnet build SnapTranslate.csproj -c Release

# 构建单文件自包含 exe（产物在 artifacts\windows\<runtime>\）
.\scripts\publish-windows.ps1 -Runtime win-x64

# 可选：发布后替换本机正在运行的版本，并保留最近一份备份
.\scripts\deploy-local.ps1 -DeployDir "$env:LOCALAPPDATA\Programs\SnapTranslate"

# macOS 客户端
dotnet build src\SnapTranslate.Desktop\SnapTranslate.Desktop.csproj -c Release
.\scripts\package-macos.sh          # 需要 macOS 环境
```

`publish-windows.ps1` 使用 `PublishSingleFile` + `SelfContained`，产物约 190 MB，不需要目标机器装运行时。

`deploy-local.ps1` 会：编译 → 停掉运行中的实例 → 在部署目录建 `backup-时间戳` → 覆盖程序文件 → 重启，
并按 `-KeepBackups`（默认 1）清理旧备份。**它只覆盖 exe/dll/pdb/xml 这些程序文件，不动 `%APPDATA%` 里的数据。**

## 测试矩阵

| 套件 | 命令 | 覆盖内容 |
| --- | --- | --- |
| Core 单测 | `dotnet test tests\SnapTranslate.Core.Tests` | 设置迁移、翻译通道、提示词、文档分块、剪贴板仓库 |
| Desktop 测试 | `dotnet test tests\SnapTranslate.Desktop.Tests` | Avalonia 无头启动与主窗口渲染 |
| WPF 冒烟 | `--offline` | 贴图窗口、离线剪贴板、文档任务三条链路 |

冒烟测试还有这些模式（全部在 `tests/SnapTranslate.SmokeTest`）：

| 参数 | 作用 |
| --- | --- |
| `--offline` | 离线回归，默认跑这个 |
| `--ocr-eval` | 跑 OCR 样张并输出字符错误率 |
| `--clipboard-image-formats` | 校验复制图片写入剪贴板的格式 |
| `--render-settings` / `--render-input` | 渲染设置页 / 翻译页 PNG |
| `--render-translation` / `--render-capture-compact` | 渲染截图翻译窗口 / 紧凑截图工具条 |

渲染类模式会临时替换 `settings.json`（先备份、结束再还原），所以不要在应用运行中手动改配置。

## OCR 评测

```powershell
dotnet run --project tests\SnapTranslate.SmokeTest\SnapTranslate.SmokeTest.csproj -c Release -- --ocr-eval
```

样张放在 `tests/SnapTranslate.SmokeTest/ocr-samples`，输出每张图的识别结果、字符错误率（CER）和总体均值。
调整 `WindowsServices.OcrService.cs` 里的预处理后必须跑一次，CER 不应变差。

## 发布检查清单

1. 三个测试套件全绿
2. 界面相关改动用 `--render-*` 看一眼
3. `dotnet build` 零警告（项目开了 `TreatWarningsAsErrors`）
4. 更新 README 里变化的命令或参数
5. 打 tag，附上 `artifacts\windows\win-x64\SnapTranslate.exe` 和 zip
