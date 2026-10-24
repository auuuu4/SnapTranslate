# 安全策略

## 报告漏洞

**请不要开公开 issue。** 用 GitHub 的 Security Advisories（仓库 Security 标签页 → Report a vulnerability）私下提交，
或直接邮件联系维护者。我们会在 7 天内确认，并在修复发布后公开致谢。

## 已知的设计取舍

- **API Key 明文保存**在 `%APPDATA%\SnapTranslate\settings.json`（macOS 在 `~/Library/Application Support/SnapTranslate`）。
  目前不做系统钥匙串加密，因为多平台实现成本高；请自行评估共享机器上的风险。
- **翻译走第三方网页接口**，非官方 API，请求会带上你配置的地址/密钥，请只填写你信任的服务。
- **剪贴板历史**会完整记录剪贴板内容，包括密码管理器可能放进去的文本。介意的话在设置里关掉「记录剪贴板」。
- **全局键盘钩子**只用于分发已配置的快捷键，不记录按键内容。
- 程序**未做代码签名**，请只从本仓库的 Releases 或自己构建的产物运行。
- 仓库里有两处**有道网页版的前端常量**（`YoudaoPreSecret`、`YoudaoWebKeyGetterSecret`），
  它们随网页 JS 公开分发，用于复刻公开接口的签名，不是任何人的账号凭据。

## 支持范围

只维护默认分支的最新版本，旧版本不再单独打补丁。
