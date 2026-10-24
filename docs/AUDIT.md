# SnapTranslate 代码审计

审计日期：2026-08-24

## 已修复

| 级别 | 问题 | 处理 |
| --- | --- | --- |
| High | `.NET 7` 已停止支持 | 全部项目升级到 `.NET 8` |
| High | `SQLitePCLRaw.lib.e_sqlite3 2.1.4` 命中 `GHSA-2m69-gcr7-jv3q` | `Microsoft.Data.Sqlite` 升级到 `8.0.30`，复查无已知漏洞 |
| High | WPF/Win32 与业务逻辑耦合，macOS 无法编译 | 抽出 `SnapTranslate.Core`，新增 Avalonia macOS 客户端 |
| Medium | 配置直接覆盖，异常退出可能损坏 JSON，读取失败被静默吞掉 | 原子临时文件替换，保留损坏文件并暴露警告 |
| Medium | 剪贴板数据库默认写入程序目录 | 改到用户应用数据目录，并迁移旧数据库和图片路径 |
| Medium | 文档失败或取消时可能留下半成品 | 所有格式先写同目录临时文件，成功后原子移动 |
| Medium | 重复段落产生重复外部请求，长文本分块改变空白 | 相同文本去重，分块保留原空白和连接边界 |
| Medium | 外部数据源路径写死为某个绝对路径 | 改为文件选择器 |
| Low | 新安装默认写入开机启动项 | 默认关闭，由用户显式启用 |

## 仍需关注

- `Transmart`、Google 网页接口和有道网页接口不是带 SLA 的正式服务端 API，可能随上游变更失效，也应在分发前复核各自服务条款。有道常量是公开网页客户端签名材料，不是用户凭据，但仍属于脆弱集成。
- Windows 全局快捷键使用低级键盘钩子。当前回调不记录或持久化按键，但签名发布前仍应进行隐私说明和安全软件兼容测试。
- macOS 的全局快捷键、托盘、截图标注、贴图和剪贴板历史尚未迁移；当前 macOS 版本覆盖翻译、系统 Vision 截图 OCR、文档任务、历史和设置。
- PDF 翻译输出为分页纯文本，不保留原 PDF 版式。UI 和文档中应继续明确输出扩展名为 `.txt`。
- 原生 OCR、屏幕权限、代码签名和 notarization 只能在真实 macOS 环境完成端到端验证；CI 已覆盖编译、应用包结构和签名一致性。

## 验证门槛

- 核心单元测试必须在 Windows/macOS/Linux 通过。
- Windows WPF 与 Avalonia 客户端必须以 Release、0 警告构建。
- Windows 依赖漏洞扫描不得报告 High/Critical。
- macOS arm64/x64 必须生成包含主程序和通用 Vision helper 的有效 `.app` 压缩包。
