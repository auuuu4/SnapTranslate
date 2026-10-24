# 架构说明

## 分层

```
┌─────────────────────────────┐   ┌──────────────────────────────┐
│ SnapTranslate (WPF, Windows)│   │ SnapTranslate.Desktop (Avalonia) │
│ 截图 / 标注 / 托盘 / 快捷键  │   │ 跨平台界面                     │
└──────────────┬──────────────┘   └───────────────┬──────────────┘
               │                                  │
               └────────────┬─────────────────────┘
                            ▼
                 ┌──────────────────────┐
                 │ SnapTranslate.Core   │
                 │ 设置 / 翻译 / 提示词  │
                 │ 文档翻译 / 数据路径   │
                 └──────────────────────┘
```

`Core` 不引用任何 UI 框架，只依赖 `System.Text.Json` 与 `HttpClient`，所以两个客户端和单元测试共用同一套行为。

## 核心模块

| 文件 | 职责 |
| --- | --- |
| `Models.cs` | `AppSettings`、`TranslationProvider`、`ChatApiProfile`，以及 `Clone()` 快照 |
| `SettingsService.cs` | 读写 `settings.json`、旧配置迁移、字段归一化（原子写入：先写临时文件再替换） |
| `TranslationService.cs` | 所有翻译通道的实现、超时与重试、流式增量回调 |
| `TranslationProviderCatalog.cs` | 通道显示名的**唯一来源** |
| `TranslationPrompt.cs` | 提示词默认值、`{source}`/`{target}` 占位符替换与归一化 |
| `DocumentTranslation.cs` | 文档解析、分块、并发调度与结果落盘 |
| `AppDataPaths.cs` | 各平台数据目录（`%APPDATA%` / `~/Library/Application Support`） |

## 翻译调用链

```
UI 触发
  → TranslationService.TranslateAsync(text, settings, progress, ct)
     ├─ Provider != Auto：直接走对应通道
     └─ Provider == Auto：按 settings.AutoChain 依次尝试，失败记录后换下一个
        → 各通道返回 TranslationResult(译文, 通道显示名, 源语言)
  → 写历史（history.json）+ 更新 UI
```

通道实现要点：

- 鉴权类通道（有道 AI / 有道普通）先换 token，缓存在实例字段里带过期时间，失败时清空重取
- 消息类接口（Chat Completions / Responses / Messages）共用 `ChatApiProfile`，只是请求体和流式解析不同
- 所有出站请求都有 4 MB 响应上限与取消支持，`IProgress<string>` 用于流式预览

## UI 状态管理

设置页编辑用的是一份**候选对象**：用户改动先落在控件上，保存时 `ReadSettingsControls()` 全量构造 `AppSettings`，
校验通过后才替换同步对象并写入磁盘；设置页的改动会在 700ms 防抖后自动保存。

截图流程：`ScreenshotService.CaptureVirtualScreen()` 抓整屏 → `CaptureOverlayWindow` 负责选区与标注 →
`ComposeResult()` 生成位图 → 按动作分发（复制 / 保存 / 贴图 / OCR / 翻译）。

## 数据文件

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 所有设置，含 API Key（明文） |
| `history.json` | 翻译历史 |
| `document-tasks.json` | 文档翻译任务状态 |
| `Data/clipboard.db` | 剪贴板历史（SQLite） |
| `logs/` | 按天滚动的日志 |
