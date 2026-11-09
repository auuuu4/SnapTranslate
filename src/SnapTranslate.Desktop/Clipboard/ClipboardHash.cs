using System.Security.Cryptography;
using System.Text;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>
/// 剪贴板内容哈希。Swift 辅助程序与 C# 侧使用同一规则：
/// SHA-256(kind + "\0" + payload)，图片的 payload 是 PNG 字节的 SHA-256 十六进制串。
/// </summary>
public static class ClipboardHash
{
    /// <summary>按“类型 + 内容载荷”计算去重哈希。</summary>
    public static string Compute(ClipboardCaptureKind kind, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\0{payload}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>计算文件内容的 SHA-256 十六进制串。</summary>
    public static string FileDigest(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
