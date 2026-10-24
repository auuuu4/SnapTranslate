# 参与贡献

感谢你愿意改进 SnapTranslate。这里的约定不复杂，但最好先花两分钟读一遍。

## 环境准备

- Windows 10 19041+ / macOS 12+
- [.NET SDK 8.0.424](https://dotnet.microsoft.com/download)（`global.json` 已锁定，其他 8.x 版本可能可以但未验证）
- 构建 Windows 客户端需要 Windows，macOS 客户端可以在 macOS 或 Windows 上编译

```powershell
dotnet restore
dotnet build SnapTranslate.csproj -c Release
```

## 提交前必须跑通的测试

```powershell
dotnet test tests\SnapTranslate.Core.Tests\SnapTranslate.Core.Tests.csproj -c Release
dotnet test tests\SnapTranslate.Desktop.Tests\SnapTranslate.Desktop.Tests.csproj -c Release
dotnet run --project tests\SnapTranslate.SmokeTest\SnapTranslate.SmokeTest.csproj -c Release -- --offline
```

涉及界面改动的，再用 `--render-settings` / `--render-input` / `--render-translation` / `--render-capture-compact` 渲染出来看一眼，确认没有错位或截断。

> 冒烟测试会读写 `%APPDATA%\SnapTranslate\settings.json`，它会在运行前备份、结束后还原。**不要**在测试运行时手动改这个文件。

## 代码约定

- **通道名称只有一个来源**：`TranslationProviderCatalog`。界面上任何显示通道名的地方都必须用它，不要就地写字符串。
- **设置对象一律走快照**：改设置时先 `AppSettings.Clone()`，确认无误再整体替换同步对象，避免半成品状态被别的线程读到。
- **不要在 UI 线程做重活**：OCR、网络、文件编码都在后台线程，回到 UI 时用 `Dispatcher`。
- **新增配置项要能迁移**：`SettingsService.LoadSettings` 里给旧字段留迁移路径，新字段给合理默认值。
- **引入新依赖前先开 issue**：项目刻意保持零第三方运行时依赖（除 WPF/Avalonia 本身），加包需要说明理由。
- 编译开启 `TreatWarningsAsErrors`，提交前不应有警告。

## 提交信息

采用 Conventional Commits 前缀，正文用中文说明「为什么」：

```
fix(clipboard): 复制图片时同时写 PNG 格式，避免接收方当成文件

- 原来只写 CF_DIB，微信/QQ 挑不到认识的格式会当成未知数据
- 改为 24 位 DIB + 注册格式 PNG + image/png
```

可用的作用域：`settings` / `translation` / `capture` / `ocr` / `clipboard` / `ui` / `docs` / `build`。

## 新增一个翻译通道

1. 在 `src/SnapTranslate.Core/Models.cs` 的 `TranslationProvider` 末尾追加枚举值（**不要插在中间**，序列化按整数存储）。
2. 在 `TranslationProviderCatalog` 里补显示名，并决定是否加入 `SelectableProviders`。
3. 在 `TranslationService` 的 `switch` 里加分支，返回 `TranslationResult(text, 通道名, 识别到的源语言)`。
4. 需要鉴权的通道请自行缓存凭据并设置过期时间，参考 `GetYoudaoAuthAsync`。
5. 在 `tests/SnapTranslate.Core.Tests` 里用 `StubHttpHandler` 写至少三个用例：正常返回、凭据复用、失败信息透传。
6. 如果是内置免费通道，把它加进 `AppSettings.DefaultAutoChain`。

## 报告问题

请用 issue 模板，并附上：

- 版本号（设置页底部或 exe 属性里的 `1.0.0+<commit>`）
- 复现步骤，越具体越好
- `%APPDATA%\SnapTranslate\logs` 下对应时间的日志片段

**分享前请删掉日志和截图里的 API Key。**

## 行为准则

参与本项目即表示同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
