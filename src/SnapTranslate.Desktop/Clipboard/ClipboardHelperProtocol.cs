using System.Globalization;
using System.Text;
using System.Text.Json;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>辅助程序读取剪贴板的结果：changeCount 加解析出的内容（空剪贴板时 Capture 为 null）。</summary>
public sealed record ClipboardHelperReadResult(int ChangeCount, Platform.ClipboardCapture? Capture);

/// <summary>与 Swift 辅助程序 SnapTranslatePasteboard 交换的 JSON 协议。</summary>
public static class ClipboardHelperProtocol
{
    /// <summary>只读取 changeCount 的参数。</summary>
    public const string ChangeCountArgument = "--change-count";

    /// <summary>指定图片落盘目录的参数。</summary>
    public const string ImagesDirectoryArgument = "--images-dir";

    /// <summary>把 JSON 内容写回剪贴板的参数。</summary>
    public const string CopyArgument = "--copy";

    /// <summary>解析读取模式的 JSON 输出。</summary>
    public static ClipboardHelperReadResult ParseReadOutput(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("剪贴板辅助程序返回的 JSON 不是对象。");

        var changeCount = GetInt32(root, "changeCount") ?? 0;
        if (!TryParseKind(GetString(root, "kind"), out var kind))
            return new ClipboardHelperReadResult(changeCount, null);

        var text = GetString(root, "text") ?? string.Empty;
        var path = GetString(root, "path");
        if (string.IsNullOrEmpty(text) && root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            var paths = files.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
            text = string.Join("\n", paths);
            path ??= paths.FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(path)) path = null;

        var hash = GetString(root, "hash");
        var effectiveHash = string.IsNullOrWhiteSpace(hash) ? ComputeHash(kind, text, path) : hash;
        return new ClipboardHelperReadResult(
            changeCount,
            new Platform.ClipboardCapture(kind, text, path, DateTimeOffset.Now, effectiveHash));
    }

    /// <summary>解析 --change-count 的纯数字输出，非法输出返回 -1。</summary>
    public static int ParseChangeCountOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return -1;
        return int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : -1;
    }

    /// <summary>把一条历史拼成 --copy 需要的 JSON。</summary>
    public static string BuildCopyPayload(Platform.ClipboardCapture item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", ToJsonKind(item.Kind));
            writer.WriteString("text", item.Text);
            if (!string.IsNullOrWhiteSpace(item.FilePath)) writer.WriteString("path", item.FilePath);
            if (item.Kind == ClipboardCaptureKind.Files)
            {
                writer.WriteStartArray("files");
                foreach (var file in SplitFilePaths(item)) writer.WriteStringValue(file);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string[] SplitFilePaths(Platform.ClipboardCapture item)
    {
        var paths = item.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0 && !string.IsNullOrWhiteSpace(item.FilePath)) return [item.FilePath];
        return paths;
    }

    private static string ComputeHash(ClipboardCaptureKind kind, string text, string? path)
    {
        if (kind == ClipboardCaptureKind.Image && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return ClipboardHash.Compute(kind, ClipboardHash.FileDigest(path));
        return ClipboardHash.Compute(kind, text);
    }

    private static string ToJsonKind(ClipboardCaptureKind kind) => kind switch
    {
        ClipboardCaptureKind.Text => "text",
        ClipboardCaptureKind.Image => "image",
        ClipboardCaptureKind.Files => "files",
        _ => "none"
    };

    private static bool TryParseKind(string? value, out ClipboardCaptureKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "text":
                kind = ClipboardCaptureKind.Text;
                return true;
            case "image":
                kind = ClipboardCaptureKind.Image;
                return true;
            case "files":
                kind = ClipboardCaptureKind.Files;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? GetInt32(JsonElement root, string property)
        => root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
}
