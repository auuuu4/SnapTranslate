using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>一条剪贴板历史记录。</summary>
public sealed class ClipboardItem
{
    /// <summary>记录唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>内容类型。</summary>
    public ClipboardCaptureKind Kind { get; set; }

    /// <summary>文本内容；文件类型时是换行分隔的文件路径。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>图片类型的 PNG 文件路径，或文件类型的第一个文件路径。</summary>
    public string? FilePath { get; set; }

    /// <summary>内容哈希，用于去重。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>记录时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>是否收藏。</summary>
    public bool Favorite { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>列表里显示的类型名称。</summary>
    public string KindDisplay => Kind switch
    {
        ClipboardCaptureKind.Text => "文本",
        ClipboardCaptureKind.Image => "图片",
        ClipboardCaptureKind.Files => "文件",
        _ => "其他"
    };

    /// <summary>列表里显示的内容预览。</summary>
    public string Preview => Kind switch
    {
        ClipboardCaptureKind.Text => BuildTextPreview(),
        ClipboardCaptureKind.Image => BuildFileNamePreview("图片"),
        ClipboardCaptureKind.Files => BuildFilesPreview(),
        _ => string.Empty
    };

    /// <summary>列表里显示的时间：今天只显示时分，其余显示月日时分。</summary>
    public string CreatedAtDisplay
    {
        get
        {
            var local = CreatedAt.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
        }
    }

    /// <summary>收藏标记。</summary>
    public string FavoriteDisplay => Favorite ? "★" : "☆";

    /// <summary>转成平台层的剪贴板内容，用于写回系统剪贴板。</summary>
    public Platform.ClipboardCapture ToCapture() => new(Kind, Text, FilePath, CreatedAt, Hash);

    /// <summary>从平台层捕获内容创建历史记录。</summary>
    public static ClipboardItem FromCapture(Platform.ClipboardCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return new ClipboardItem
        {
            Kind = capture.Kind,
            Text = capture.Text,
            FilePath = capture.FilePath,
            Hash = capture.Hash,
            CreatedAt = capture.CapturedAt == default ? DateTimeOffset.Now : capture.CapturedAt
        };
    }

    private string BuildTextPreview()
    {
        var normalized = string.Join(" ", Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return "空文本";
        return normalized.Length <= 160 ? normalized : normalized[..160] + "…";
    }

    private string BuildFileNamePreview(string fallback)
    {
        if (string.IsNullOrWhiteSpace(FilePath)) return fallback;
        var name = Path.GetFileName(FilePath);
        return string.IsNullOrEmpty(name) ? fallback : $"{fallback} · {name}";
    }

    private string BuildFilesPreview()
    {
        var paths = Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0) return BuildFileNamePreview("文件");
        var names = paths.Take(2).Select(Path.GetFileName).Where(name => !string.IsNullOrEmpty(name)).ToArray();
        var summary = string.Join("、", names);
        return paths.Length > 2 ? $"{summary} 等 {paths.Length} 个文件" : summary;
    }
}
