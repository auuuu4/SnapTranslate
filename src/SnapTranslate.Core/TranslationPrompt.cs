using System.Text.RegularExpressions;

namespace SnapTranslate;

/// <summary>
/// 翻译提示词模板。支持 <c>{source}</c> 和 <c>{target}</c> 两个占位符，
/// 会分别替换成源语言和目标语言的显示名。留空时使用 <see cref="Default"/>。
/// </summary>
public static partial class TranslationPrompt
{
    public const string Default = """
        你是专业的翻译引擎。请把用户提供的文本翻译成{target}。
        原文语言：{source}。

        要求：
        1. 只输出译文，不要解释、不要加引号、不要复述原文。
        2. 保留原文的换行、序号和占位符（例如 %s、{0}）。
        3. 代码、命令、URL 和专有名词保持原样。
        4. 原文已经是{target}时原样返回。
        """;

    /// <summary>规范化换行并去掉首尾空白，便于比较和存储。</summary>
    public static string Normalize(string? template)
        => string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : template.Replace("\r\n", "\n").Trim();

    /// <summary>留空时回落到内置默认提示词。</summary>
    public static string Resolve(string? template)
        => Normalize(template) is { Length: > 0 } custom ? custom : Default;

    /// <summary>把占位符替换成语言显示名后的最终系统提示词。</summary>
    public static string Build(string? template, string? sourceLanguage, string? targetLanguage)
    {
        var text = Resolve(template);
        var source = LanguageName(sourceLanguage);
        var target = LanguageName(targetLanguage);
        return PlaceholderPattern().Replace(text, match =>
            match.Groups[1].Value.Equals("source", StringComparison.OrdinalIgnoreCase) ? source : target);
    }

    /// <summary>
    /// 保存用：空内容或与内置默认完全一致时返回空串，
    /// 这样以后改进默认提示词时，没自定义过的用户会自动跟着更新。
    /// </summary>
    public static string NormalizeForStorage(string? template)
    {
        var normalized = Normalize(template);
        return normalized.Length == 0 || normalized == Normalize(Default) ? string.Empty : normalized;
    }

    /// <summary>把语言代码翻成提示词里的自然语言名称。</summary>
    public static string LanguageName(string? code)
    {
        var value = string.IsNullOrWhiteSpace(code) ? "auto" : code.Trim();
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "自动识别";
        return value.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => "简体中文",
            "zh-tw" or "zh-hk" or "zh-hant" => "繁體中文",
            "en" or "en-us" or "en-gb" => "English",
            "ja" or "jp" => "日本語",
            "ko" or "kr" => "한국어",
            "fr" => "Français",
            "de" => "Deutsch",
            "es" => "Español",
            "ru" => "Русский",
            "pt" => "Português",
            "it" => "Italiano",
            "ar" => "العربية",
            "th" => "ไทย",
            "vi" => "Tiếng Việt",
            _ => value
        };
    }

    [GeneratedRegex(@"\{(source|target)\}", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();
}
