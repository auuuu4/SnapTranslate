namespace SnapTranslate;

/// <summary>
/// 翻译通道与接口格式的显示名唯一来源。
/// 设置页下拉、结果标签、历史记录、测试窗口都从这里取字符串，
/// 避免同一个通道在不同地方叫不同的名字（例如「OpenAI 兼容」和「OpenAI Responses」）。
/// </summary>
public static class TranslationProviderCatalog
{
    /// <summary>设置页里可选的通道，顺序即下拉顺序。</summary>
    public static readonly IReadOnlyList<TranslationProvider> SelectableProviders =
    [
        TranslationProvider.ChatApi,
        TranslationProvider.Youdao,
        TranslationProvider.YoudaoWeb,
        TranslationProvider.Transmart,
        TranslationProvider.MyMemory,
        TranslationProvider.Google,
        TranslationProvider.Auto
    ];

    /// <summary>大模型 API 支持的接口格式，顺序即下拉顺序。</summary>
    public static readonly IReadOnlyList<ChatApiFormat> SelectableFormats =
    [
        ChatApiFormat.OpenAi,
        ChatApiFormat.Responses,
        ChatApiFormat.Anthropic
    ];

    /// <summary>通道名。下拉、结果、历史都用这一个字符串。</summary>
    public static string DisplayName(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Youdao => "有道 AI（内置免费）",
        TranslationProvider.YoudaoWeb => "有道 普通翻译（内置免费）",
        TranslationProvider.ChatApi => "自定义 API",
        // Windows 端已把 Ollama 并进「自定义 API」，这个名字只给 macOS 客户端和旧配置迁移用
        TranslationProvider.Ollama => "Ollama",
        TranslationProvider.Transmart => "腾讯 Transmart（内置免费）",
        TranslationProvider.MyMemory => "MyMemory（内置免费）",
        TranslationProvider.Google => "Google（内置免费）",
        TranslationProvider.Auto => "自动",
        _ => provider.ToString()
    };

    /// <summary>接口格式名。三种格式只是请求形态不同，不带厂商标注。</summary>
    public static string FormatDisplayName(ChatApiFormat format) => format switch
    {
        ChatApiFormat.OpenAi => "Chat Completions",
        ChatApiFormat.Responses => "Responses",
        ChatApiFormat.Anthropic => "Messages",
        _ => format.ToString()
    };

    /// <summary>该格式对应的请求路径，用于提示文案。</summary>
    public static string FormatPath(ChatApiFormat format) => format switch
    {
        ChatApiFormat.OpenAi => "/chat/completions",
        ChatApiFormat.Responses => "/responses",
        ChatApiFormat.Anthropic => "/messages",
        _ => string.Empty
    };

    /// <summary>结果与历史里的标签：通道名，必要时带上具体模型。</summary>
    public static string ResultLabel(TranslationProvider provider, string? model = null)
    {
        var name = DisplayName(provider);
        return string.IsNullOrWhiteSpace(model) ? name : name + " · " + model.Trim();
    }

    /// <summary>自定义渠道在结果与历史里的标签：「渠道名 · 模型」。</summary>
    public static string ProfileLabel(ChatApiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Join(profile.DisplayLabel, profile.Model);
    }

    /// <summary>测试窗口标题栏用的完整描述，例如「DeepSeek · Responses · deepseek-chat」。</summary>
    public static string Describe(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Provider switch
        {
            TranslationProvider.ChatApi => settings.ActiveChatApiProfile is { } profile
                ? Join(profile.DisplayLabel, FormatDisplayName(profile.Format), profile.Model)
                : Join(DisplayName(TranslationProvider.ChatApi), "未配置渠道"),
            TranslationProvider.Ollama => Join(DisplayName(TranslationProvider.Ollama), settings.OllamaModel),
            _ => DisplayName(settings.Provider)
        };
    }

    private static string Join(params string?[] parts)
        => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
}
