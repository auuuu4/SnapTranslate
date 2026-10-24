using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SnapTranslate;

public enum TranslationProvider
{
    Auto,
    Transmart,
    MyMemory,
    Google,
    Ollama,
    Youdao,

    /// <summary>OpenAI / Anthropic 兼容的大模型接口，需要用户配置地址、密钥和模型。</summary>
    ChatApi,

    /// <summary>有道网页版普通翻译（非 AI 通道），走 dict-trans 的 webtranslate。</summary>
    YoudaoWeb
}

/// <summary>大模型接口的两种请求格式。</summary>
public enum ChatApiFormat
{
    /// <summary>OpenAI 兼容：POST /chat/completions，Authorization: Bearer。</summary>
    OpenAi,

    /// <summary>Anthropic：POST /messages，x-api-key + anthropic-version。</summary>
    Anthropic,

    /// <summary>OpenAI Responses API：POST /responses，Authorization: Bearer。</summary>
    Responses
}

public enum PinnedImageEdgeEffect
{
    Gradient,
    None
}

/// <summary>一条自定义大模型渠道：名称、地址、密钥、模型、接口格式都由用户自己维护。</summary>
public sealed class ChatApiProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public ChatApiFormat Format { get; set; } = ChatApiFormat.OpenAi;

    /// <summary>地址和模型都有值才算能直接用。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Name) ? "未命名渠道" : Name.Trim();

    public ChatApiProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        Format = Format
    };
}

public sealed class AppSettings
{
    public TranslationProvider Provider { get; set; } = TranslationProvider.Auto;
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModel { get; set; } = string.Empty;
    /// <summary>自定义大模型渠道列表，用户可以任意增删改。</summary>
    public List<ChatApiProfile> ChatApiProfiles { get; set; } = new();

    /// <summary>当前用于翻译的渠道 Id。</summary>
    public string ActiveChatApiProfileId { get; set; } = string.Empty;

    /// <summary>自定义翻译提示词（仅大模型接口使用）。留空表示使用内置默认值。</summary>
    public string TranslationPrompt { get; set; } = string.Empty;

    /// <summary>「自动」模式依次尝试的通道，顺序和内容都由用户自己配置。</summary>
    public List<TranslationProvider> AutoChain { get; set; } = DefaultAutoChain.ToList();
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string DocumentSourceLanguage { get; set; } = "auto";
    public string DocumentTargetLanguage { get; set; } = "zh-CN";
    public string DocumentOutputDirectory { get; set; } = string.Empty;
    public string DocumentOutputSuffix { get; set; } = "_translation";
    public int DocumentTranslationConcurrency { get; set; } = 2;
    public string InputHotkey { get; set; } = "Ctrl+Shift+A";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+S";
    public string ScreenshotTranslateHotkey { get; set; } = "Ctrl+Shift+D";
    public string SilentOcrHotkey { get; set; } = "Ctrl+Shift+C";
    public bool SilentOcrShowEditor { get; set; } = true;
    public string ScreenshotOcrHotkey { get; set; } = "Ctrl+Shift+O";
    public string ClipboardHotkey { get; set; } = "Win+V";
    public string PinnedImageHotkey { get; set; } = "Ctrl+Alt+P";
    public PinnedImageEdgeEffect PinnedImageEdgeEffect { get; set; } = PinnedImageEdgeEffect.Gradient;
    public bool ClipboardEnabled { get; set; } = true;
    public bool ClipboardImageOcr { get; set; } = true;
    public bool ClipboardAutoPaste { get; set; } = true;
    public int ClipboardMaxItems { get; set; } = 10000;
    public int ClipboardRetentionDays { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;

    /// <summary>主窗口是否始终置顶。</summary>
    public bool KeepWindowOnTop { get; set; }
    public bool NotifyScreenshotActions { get; set; }
    public bool NotifyOcrResults { get; set; } = true;
    public bool NotifyErrors { get; set; } = true;

    /// <summary>「自动」模式的默认尝试顺序：本地优先，其次大模型接口，最后免费网页接口。</summary>
    public static IReadOnlyList<TranslationProvider> DefaultAutoChain { get; } = new[]
    {
        TranslationProvider.ChatApi,
        TranslationProvider.Youdao,
        TranslationProvider.YoudaoWeb,
        TranslationProvider.Transmart,
        TranslationProvider.MyMemory,
        TranslationProvider.Google
    };

    /// <summary>
    /// 复制一份设置快照。翻译相关的字段很多，各处构造请求参数时用快照可以避免新增字段被漏掉。
    /// 列表字段要深拷贝，否则快照和原设置会共享同一个 List。
    /// </summary>
    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.AutoChain = NormalizeAutoChain(AutoChain);
        clone.ChatApiProfiles = ChatApiProfiles.Select(profile => profile.Clone()).ToList();
        return clone;
    }

    /// <summary>去掉「自动」自身、已并入自定义 API 的 Ollama，以及重复项；为空时回落到默认顺序。</summary>
    public static List<TranslationProvider> NormalizeAutoChain(IEnumerable<TranslationProvider>? chain)
    {
        var normalized = new List<TranslationProvider>();
        foreach (var provider in chain ?? Array.Empty<TranslationProvider>())
        {
            if (provider is TranslationProvider.Auto or TranslationProvider.Ollama) continue;
            if (normalized.Contains(provider)) continue;
            normalized.Add(provider);
        }

        return normalized.Count > 0 ? normalized : DefaultAutoChain.ToList();
    }

    /// <summary>当前选中的渠道；列表为空时为 null。</summary>
    public ChatApiProfile? ActiveChatApiProfile =>
        ChatApiProfiles.FirstOrDefault(profile => profile.Id == ActiveChatApiProfileId)
        ?? ChatApiProfiles.FirstOrDefault();

    /// <summary>是否已经配置好可直接使用的大模型接口。</summary>
    public bool IsChatApiConfigured => ActiveChatApiProfile?.IsConfigured == true;

    /// <summary>补齐 Id、去重并清理空白；空名称保持为空（显示时回落到「未命名渠道」）。</summary>
    public static List<ChatApiProfile> NormalizeChatApiProfiles(IEnumerable<ChatApiProfile>? profiles)
    {
        var normalized = new List<ChatApiProfile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles ?? Enumerable.Empty<ChatApiProfile>())
        {
            if (profile is null) continue;
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
                ids.Add(profile.Id);
            }

            profile.Name = profile.Name?.Trim() ?? string.Empty;
            profile.BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
                ? "https://api.openai.com/v1"
                : profile.BaseUrl.Trim();
            profile.ApiKey = profile.ApiKey?.Trim() ?? string.Empty;
            profile.Model = profile.Model?.Trim() ?? string.Empty;
            normalized.Add(profile);
        }

        return normalized;
    }
}

public sealed class HistoryEntry
{
    public DateTime CreatedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
}

public sealed class DocumentTaskSnapshot
{
    public Guid Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public DocumentTaskStatus Status { get; set; }
    public int Progress { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed record TranslationResult(string Text, string Provider, string DetectedLanguage);

public sealed record OcrTextLine(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLayoutResult(
    IReadOnlyList<OcrTextLine> Lines,
    string Text,
    string DetectedLanguage);

public enum ClipboardItemKind
{
    Text,
    Html,
    Rtf,
    Image,
    Files
}

public enum ClipboardFilter
{
    All,
    Text,
    Image,
    Files,
    Favorite
}

public sealed class ClipboardItem : INotifyPropertyChanged
{
    private string _note = string.Empty;
    private bool _isFavorite;

    public string Id { get; init; } = string.Empty;
    public ClipboardItemKind Kind { get; init; }
    public string Subtype { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public string Note
    {
        get => _note;
        set
        {
            if (string.Equals(_note, value, StringComparison.Ordinal)) return;
            _note = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoteDisplay));
            OnPropertyChanged(nameof(NoteActionText));
        }
    }
    public long Size { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int UseCount { get; init; } = 1;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteDisplay));
            OnPropertyChanged(nameof(FavoriteActionText));
        }
    }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string ContentHash { get; init; } = string.Empty;

    public bool IsImage => Kind == ClipboardItemKind.Image;
    public string? ImagePath => IsImage && File.Exists(Value) ? Value : null;
    public bool CanUsePlainText => Kind != ClipboardItemKind.Image || !string.IsNullOrWhiteSpace(SearchText);
    public string KindLabel => Kind switch
    {
        ClipboardItemKind.Image => "图片",
        ClipboardItemKind.Files => "文件",
        ClipboardItemKind.Html => "HTML",
        ClipboardItemKind.Rtf => "富文本",
        _ => Subtype switch
        {
            "url" => "链接",
            "email" => "邮箱",
            "path" => "路径",
            "color" => "颜色",
            _ => "文本"
        }
    };

    public string PreviewText
    {
        get
        {
            if (Kind == ClipboardItemKind.Files)
            {
                try
                {
                    var paths = System.Text.Json.JsonSerializer.Deserialize<string[]>(Value) ?? Array.Empty<string>();
                    return string.Join("  ·  ", paths.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)));
                }
                catch { return "文件"; }
            }
            if (Kind == ClipboardItemKind.Image)
                return string.IsNullOrWhiteSpace(SearchText) ? $"{Width} × {Height}" : SearchText;
            return string.IsNullOrWhiteSpace(SearchText) ? Value : SearchText;
        }
    }

    public string CreatedDisplay => CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
    public string FavoriteDisplay => IsFavorite ? "★" : "☆";
    public string FavoriteActionText => IsFavorite ? "取消收藏" : "收藏";
    public string NoteDisplay => string.IsNullOrWhiteSpace(Note) ? "添加备注…" : Note;
    public string NoteActionText => string.IsNullOrWhiteSpace(Note) ? "添加备注" : "编辑备注";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ClipboardCapture(
    ClipboardItemKind Kind,
    string Value,
    string SearchText,
    string Subtype,
    long Size,
    int Width,
    int Height,
    string ContentHash);

