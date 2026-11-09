# 更新日志

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [未发布]

### 新增（macOS 客户端补齐到与 Windows 同级）

- 截图：全屏抓取 → 自绘浮层框选、移动、缩放
- 标注：矩形、椭圆、箭头、画笔、文字、马赛克，撤销/重做，5 种颜色
- 截图结果：复制 / 保存 PNG / 贴图（Topmost）/ OCR / 截图翻译
- 截图翻译窗口：原文 / 译文 / 对照三种模式，语言切换、重新翻译、失败重试
- 剪贴板历史：NSPasteboard 轮询 + SQLite 存储，搜索、类型筛选、收藏、备注、写回剪贴板
- 全局快捷键：Carbon RegisterEventHotKey（不需要辅助功能权限），设置页可编辑并显示注册结果
- 托盘常驻：显示主窗口 / 剪贴板历史 / 退出，关闭窗口不退出进程
- 设置页：通道改由 TranslationProviderCatalog 统一生成，支持自定义 API 多渠道、翻译提示词、七项快捷键

### 变更

- macOS OCR 抽成 `ITextRecognizer`，截图流程改为「抓屏 → 浮层 → 分发」
- macOS 打包新增 Swift 剪贴板辅助程序 SnapTranslatePasteboard

## [1.0.0] - 2026-10-19

首个公开发布版本。

### 新增

- 文本翻译：原文/译文对照、复制原文与译文、翻译历史、全局快捷键发起
- 截图翻译：框选截图 → OCR → 译文覆盖显示，支持对照/仅译文模式
- 截图标注：矩形、椭圆、箭头、画笔、文字、马赛克，撤销/重做，保存/贴图/复制
- 截图 OCR 与静默 OCR，多档预处理应对深色背景与小字号
- 剪贴板历史：文本/图片/文件、搜索、收藏、备注、双击粘贴回原窗口
- 翻译通道：有道 AI、有道 普通翻译、自定义 API（Chat Completions / Responses / Messages，多条渠道）、腾讯 Transmart、MyMemory、Google
- 「自动」通道：按可编辑的通道列表依次尝试
- 自定义翻译提示词，支持 `{source}` / `{target}` 占位符
- 文档翻译：TXT/Markdown/CSV/HTML/Office/PDF，保留结构格式，并发与进度可见
- 托盘常驻、开机自启、主窗口可选始终置顶
- macOS 客户端（Avalonia + 系统 Vision OCR）

### 修复

- 设置页改动不再需要手动点保存，700ms 防抖后自动落盘
- 翻译页目标语言下拉补齐九种语言，不再把设置页选择的语言冲成「自动」
- 截图翻译窗口新增常显关闭按钮
- 复制图片时同时写入 DIB 与 PNG，微信/QQ/浏览器粘贴不会再变成文件
