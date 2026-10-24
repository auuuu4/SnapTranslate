using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapTranslate;

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        CancellationToken cancellationToken = default);

    async Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var result = await TranslateAsync(text, settings, cancellationToken);
        progress?.Report(result.Text);
        return result;
    }
}

public sealed class TranslationService : ITranslationService
{
    private const long MaxResponseBytes = 4 * 1024 * 1024;
    // 下面两个 secret 是有道网页版打包在前端 JS 里的固定常量（任何人打开 fanyi.youdao.com 都能抓到），
    // 不是用户凭据，也不属于本项目的账号。仅用于复刻公开网页接口的签名，接口失效时按新抓包更新即可。
    private const string YoudaoApi = "https://luna-ai.youdao.com";
    private const string YoudaoPreKeyId = "ai-translate-llm-pre";
    private const string YoudaoPreSecret = "EZAmCfVOH2CrBGMtPrtIPUzyv3bheLdk";
    private const string YoudaoChatKeyId = "ai-translate-llm";
    private const string YoudaoWebApi = "https://dict-trans.youdao.com";
    private const string YoudaoWebKeyGetter = "translate-webmain-key-getter";
    private const string YoudaoWebKeyId = "translate-webfanyi-webmain";
    private const string YoudaoWebKeyGetterSecret = "kSy5gtKA4yRUxAVPJPrdYKZ0jBKyd3t1";
    private const string YoudaoProduct = "webfanyi";
    private const string YoudaoUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/150.0.7871.101 Safari/537.36";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly HttpClient SharedChatHttpClient = CreateHttpClient(TimeSpan.FromMinutes(3));
    private readonly HttpClient _http;
    private readonly HttpClient _chatHttp;
    private readonly SemaphoreSlim _youdaoAuthLock = new(1, 1);
    private readonly SemaphoreSlim _youdaoRequestSlots = new(2, 2);
    private YoudaoAuth? _youdaoAuth;
    private readonly SemaphoreSlim _youdaoWebAuthLock = new(1, 1);
    private YoudaoWebAuth? _youdaoWebAuth;

    public TranslationService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttpClient;
        // 大模型翻译长文本时 30 秒不够用，单独给一个更宽松的超时客户端。
        _chatHttp = httpClient ?? SharedChatHttpClient;
    }

    public Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        CancellationToken cancellationToken = default)
        => TranslateAsync(text, settings, null, cancellationToken);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("没有可翻译的文本。", nameof(text));

        var target = ResolveTarget(text, settings.TargetLanguage);
        var source = NormalizeLanguageCode(settings.SourceLanguage, true);
        if (settings.Provider != TranslationProvider.Auto)
            return await TranslateWithProviderAsync(settings.Provider, text, source, target, settings, progress, cancellationToken);

        // 自动模式按用户配置的顺序依次尝试，未配置好的通道（没填模型 / 没填接口）直接跳过。
        var providers = AppSettings.NormalizeAutoChain(settings.AutoChain)
            .Where(provider => IsProviderReady(provider, settings))
            .ToList();
        if (providers.Count == 0)
            throw new InvalidOperationException("自动模式没有可用的通道，请检查「自动顺序」里是否包含已配置好的通道。");

        var failures = new List<Exception>();
        foreach (var provider in providers)
        {
            try
            {
                return await TranslateWithProviderAsync(provider, text, source, target, settings, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException($"{provider}: {ex.Message}", ex));
            }
        }

        throw new AggregateException("所有翻译服务均不可用。", failures);
    }

    public async Task<string> TestAsync(AppSettings settings)
    {
        var result = await TranslateAsync("Hello", settings);
        return $"{result.Provider}: {result.Text}";
    }

    private Task<TranslationResult> TranslateWithProviderAsync(
        TranslationProvider provider,
        string text,
        string source,
        string target,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken) => provider switch
        {
            TranslationProvider.Ollama => TranslateWithBufferedProgressAsync(
                TranslateWithOllamaAsync(text, source, target, settings, cancellationToken), progress),
            TranslationProvider.Transmart => TranslateWithBufferedProgressAsync(
                TranslateWithTransmartAsync(text, source, target, cancellationToken), progress),
            TranslationProvider.MyMemory => TranslateWithBufferedProgressAsync(
                TranslateWithMyMemoryAsync(text, source, target, cancellationToken), progress),
            TranslationProvider.Google => TranslateWithBufferedProgressAsync(
                TranslateWithGoogleAsync(text, source, target, cancellationToken), progress),
            TranslationProvider.Youdao => TranslateWithYoudaoAsync(text, source, target, progress, cancellationToken),
            TranslationProvider.YoudaoWeb => TranslateWithYoudaoWebAsync(text, source, target, progress, cancellationToken),
            TranslationProvider.ChatApi => TranslateWithBufferedProgressAsync(
                TranslateWithChatApiAsync(text, source, target, settings, cancellationToken), progress),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的翻译服务。")
        };

    private static bool IsProviderReady(TranslationProvider provider, AppSettings settings) => provider switch
    {
        TranslationProvider.Ollama => !string.IsNullOrWhiteSpace(settings.OllamaModel),
        TranslationProvider.ChatApi => settings.IsChatApiConfigured,
        TranslationProvider.Auto => false,
        _ => true
    };

    private static async Task<TranslationResult> TranslateWithBufferedProgressAsync(
        Task<TranslationResult> task,
        IProgress<string>? progress)
    {
        var result = await task;
        progress?.Report(result.Text);
        return result;
    }

    private async Task<TranslationResult> TranslateWithGoogleAsync(
        string text,
        string sourceCode,
        string target,
        CancellationToken cancellationToken)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl="
                  + Uri.EscapeDataString(sourceCode) + "&tl="
                  + Uri.EscapeDataString(target) + "&dt=t&q=" + Uri.EscapeDataString(text);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await BufferResponseAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException("Google 返回格式无效。");

        var output = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array
                && segment.GetArrayLength() > 0
                && segment[0].ValueKind == JsonValueKind.String)
                output.Append(segment[0].GetString());
        }
        var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString() ?? "auto"
            : "auto";
        var translated = output.ToString().Trim();
        if (translated.Length == 0) throw new InvalidOperationException("Google 返回了空翻译结果。");
        return new TranslationResult(translated, TranslationProviderCatalog.DisplayName(TranslationProvider.Google), detected);
    }

    private async Task<TranslationResult> TranslateWithTransmartAsync(
        string text,
        string sourceCode,
        string target,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            header = new
            {
                fn = "auto_translation",
                client_key = "browser-chrome-120.0.0-cross-platform-snaptranslate",
                job_id = string.Empty,
                session = string.Empty,
                user = string.Empty
            },
            source = new { lang = sourceCode, text_list = new[] { text } },
            target = new { lang = NormalizeLanguageCode(target, false) }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("https://transmart.qq.com/api/imt", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await BufferResponseAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var translated = root.TryGetProperty("auto_translation", out var items)
                         && items.ValueKind == JsonValueKind.Array
                         && items.GetArrayLength() > 0
            ? items[0].GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Transmart 返回了空翻译结果。");
        var detected = root.TryGetProperty("src_lang", out var sourceElement)
            ? sourceElement.GetString() ?? "auto"
            : "auto";
        return new TranslationResult(translated, TranslationProviderCatalog.DisplayName(TranslationProvider.Transmart), detected);
    }

    private async Task<TranslationResult> TranslateWithMyMemoryAsync(
        string text,
        string configuredSource,
        string target,
        CancellationToken cancellationToken)
    {
        var containsChinese = text.Any(ch => ch is >= '\u3400' and <= '\u9fff');
        var normalizedSource = NormalizeLanguageCode(configuredSource, false);
        var source = configuredSource == "auto"
            ? containsChinese ? "zh-CN" : "en"
            : normalizedSource == "zh" ? "zh-CN" : normalizedSource;
        var normalizedTarget = NormalizeLanguageCode(target, false);
        var targetCode = normalizedTarget == "zh" ? "zh-CN" : normalizedTarget;
        var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
                  + "&langpair=" + Uri.EscapeDataString(source + "|" + targetCode);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await BufferResponseAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("responseData", out var responseData)
            || !responseData.TryGetProperty("translatedText", out var translatedElement))
            throw new InvalidDataException("MyMemory 返回格式无效。");
        var translated = WebUtility.HtmlDecode(translatedElement.GetString())?.Trim();
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("MyMemory 返回了空翻译结果。");
        return new TranslationResult(translated, TranslationProviderCatalog.DisplayName(TranslationProvider.MyMemory), source);
    }

    private async Task<TranslationResult> TranslateWithOllamaAsync(
        string text,
        string source,
        string target,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OllamaModel))
            throw new InvalidOperationException("请先填写 Ollama 模型名。");
        var endpointText = settings.OllamaEndpoint.TrimEnd('/') + "/api/chat";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Ollama 地址必须是有效的 HTTP 或 HTTPS 地址。");

        var payload = JsonSerializer.Serialize(new
        {
            model = settings.OllamaModel.Trim(),
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = TranslationPrompt.Build(settings.TranslationPrompt, source, target) },
                new { role = "user", content = text }
            }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await BufferResponseAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var translated = document.RootElement.TryGetProperty("message", out var message)
                         && message.TryGetProperty("content", out var result)
            ? result.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Ollama 返回了空翻译结果。");
        return new TranslationResult(
            translated,
            TranslationProviderCatalog.ResultLabel(TranslationProvider.Ollama, settings.OllamaModel),
            "auto");
    }

    private async Task<TranslationResult> TranslateWithChatApiAsync(
        string text,
        string source,
        string target,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var profile = settings.ActiveChatApiProfile
                      ?? throw new InvalidOperationException("还没有大模型渠道，请先在设置里新建一个。");
        var model = profile.Model.Trim();
        if (model.Length == 0)
            throw new InvalidOperationException($"渠道「{profile.DisplayLabel}」还没有填写模型名称。");
        var format = profile.Format;
        var endpoint = ChatEndpoint(profile.BaseUrl, format);
        var instruction = TranslationPrompt.Build(settings.TranslationPrompt, source, target);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        switch (format)
        {
            case ChatApiFormat.Anthropic:
                AddAnthropicHeaders(request, profile.ApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model,
                    max_tokens = 4096,
                    system = instruction,
                    messages = new object[] { new { role = "user", content = text } }
                }), Encoding.UTF8, "application/json");
                break;

            case ChatApiFormat.Responses:
                AddOpenAiHeaders(request, profile.ApiKey);
                // Responses API 用 instructions 承载系统提示，input 直接放用户文本。
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model,
                    instructions = instruction,
                    input = text
                }), Encoding.UTF8, "application/json");
                break;

            default:
                AddOpenAiHeaders(request, profile.ApiKey);
                // 不传 temperature：部分推理模型（o1/gpt-5 等）只接受默认值，传了会直接报错。
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model,
                    stream = false,
                    messages = new object[]
                    {
                        new { role = "system", content = instruction },
                        new { role = "user", content = text }
                    }
                }), Encoding.UTF8, "application/json");
                break;
        }

        using var response = await _chatHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                await DescribeFailureAsync(response, "翻译接口请求", cancellationToken),
                null,
                response.StatusCode);
        }
        await BufferResponseAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        EnsureNotTruncated(document.RootElement, format);
        var translated = format switch
        {
            ChatApiFormat.Anthropic => ReadAnthropicText(document.RootElement),
            ChatApiFormat.Responses => ReadResponsesText(document.RootElement),
            _ => ReadOpenAiText(document.RootElement)
        };
        var label = TranslationProviderCatalog.ProfileLabel(profile);
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException($"{label} 返回了空翻译结果。");
        return new TranslationResult(translated.Trim(), label, "auto");
    }

    /// <summary>读取当前渠道的 {baseUrl}/models，返回可供选择的模型名称。</summary>
    public Task<IReadOnlyList<string>> ListModelsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profile = settings.ActiveChatApiProfile
                      ?? throw new InvalidOperationException("还没有大模型渠道，请先在设置里新建一个。");
        return ListModelsAsync(profile, cancellationToken);
    }

    /// <summary>按指定渠道读取模型列表，用于设置页里编辑还没设为当前的那条渠道。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(
        ChatApiProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var endpoint = ModelsEndpoint(profile.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (profile.Format == ChatApiFormat.Anthropic) AddAnthropicHeaders(request, profile.ApiKey);
        else AddOpenAiHeaders(request, profile.ApiKey);

        using var response = await _chatHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                await DescribeFailureAsync(response, "获取模型列表", cancellationToken),
                null,
                response.StatusCode);
        }
        await BufferResponseAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("模型列表返回格式无效，期望 data 数组。");

        var models = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
        }
        if (models.Count == 0)
            throw new InvalidOperationException("接口没有返回任何模型，请检查接口地址与 API Key。");
        return models.Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>输出被 max_tokens 截断时必须报错，否则会悄悄返回半截译文。</summary>
    private static void EnsureNotTruncated(JsonElement root, ChatApiFormat format)
    {
        const string message = "模型输出触发了长度上限，译文可能不完整；请缩短文本或换用上下文更大的模型。";
        if (format == ChatApiFormat.Responses)
        {
            if (root.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                && string.Equals(statusElement.GetString(), "incomplete", StringComparison.Ordinal))
                throw new InvalidOperationException(message);
            return;
        }

        if (format == ChatApiFormat.Anthropic)
        {
            if (root.TryGetProperty("stop_reason", out var stopReason)
                && string.Equals(stopReason.GetString(), "max_tokens", StringComparison.Ordinal))
                throw new InvalidOperationException(message);
            return;
        }

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("finish_reason", out var finishReason)
            && string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    private static string? ReadOpenAiText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null;

        var first = choices[0];
        if (first.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                        builder.Append(partText.GetString());
                }
                return builder.ToString();
            }
        }
        return first.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String
            ? legacy.GetString()
            : null;
    }

    /// <summary>Responses API 的 output 里可能有 reasoning / message 等多种条目，只取 message 的 output_text。</summary>
    private static string? ReadResponsesText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return root.TryGetProperty("output_text", out var shortcut) && shortcut.ValueKind == JsonValueKind.String
                ? shortcut.GetString()
                : null;
        }

        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("type", out var itemType)
                && itemType.ValueKind == JsonValueKind.String
                && !string.Equals(itemType.GetString(), "message", StringComparison.Ordinal))
                continue;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (part.TryGetProperty("type", out var partType)
                    && partType.ValueKind == JsonValueKind.String
                    && !string.Equals(partType.GetString(), "output_text", StringComparison.Ordinal))
                    continue;
                if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    builder.Append(textElement.GetString());
            }
        }
        return builder.ToString();
    }

    private static string? ReadAnthropicText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && !string.Equals(type.GetString(), "text", StringComparison.Ordinal))
                continue;
            if (block.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                builder.Append(textElement.GetString());
        }
        return builder.ToString();
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("接口地址必须是有效的 HTTP 或 HTTPS 地址，例如 https://api.openai.com/v1。");
        return trimmed;
    }

    private static string ChatEndpoint(string? baseUrl, ChatApiFormat format)
    {
        var root = NormalizeBaseUrl(baseUrl);
        if (root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || root.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
            || root.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return root;
        return root + format switch
        {
            ChatApiFormat.Anthropic => "/messages",
            ChatApiFormat.Responses => "/responses",
            _ => "/chat/completions"
        };
    }

    private static string ModelsEndpoint(string? baseUrl)
    {
        var root = NormalizeBaseUrl(baseUrl);
        return root.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ? root : root + "/models";
    }

    private static void AddOpenAiHeaders(HttpRequestMessage request, string? apiKey)
    {
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static void AddAnthropicHeaders(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Anthropic 格式需要填写 API Key。");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.Accept.ParseAdd("application/json");
    }

    private static async Task<string> DescribeFailureAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
        var detail = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            detail = ExtractErrorMessage(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            // 读不出错误体就只报状态码。
        }
        return $"{action}失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}";
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return "：" + error.GetString();
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                    return "：" + nested.GetString();
            }
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return "：" + message.GetString();
        }
        catch (JsonException)
        {
            // 非 JSON 错误体按原文截断返回。
        }
        var trimmed = body.Trim();
        return "：" + (trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed);
    }

    private async Task<TranslationResult> TranslateWithYoudaoAsync(
        string text,
        string source,
        string target,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        const int maximumAttempts = 4;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _youdaoRequestSlots.WaitAsync(cancellationToken);
            try
            {
                return await TranslateWithYoudaoOnceAsync(text, source, target, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientYoudaoFailure(ex))
            {
                lastError = ex;
                _youdaoAuth = null;
            }
            finally
            {
                _youdaoRequestSlots.Release();
            }

            if (attempt < maximumAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(350 * (1 << (attempt - 1)) + RandomNumberGenerator.GetInt32(80, 260));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"有道翻译请求连续失败，已自动重试 {maximumAttempts - 1} 次：{InnermostMessage(lastError)}",
            lastError);
    }

    private async Task<TranslationResult> TranslateWithYoudaoOnceAsync(
        string text,
        string source,
        string target,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var auth = await GetYoudaoAuthAsync(cancellationToken);
        var parameters = new Dictionary<string, string>
        {
            ["product"] = YoudaoProduct,
            ["appVersion"] = "12.0.0",
            ["client"] = "webaitrans",
            ["mid"] = "1",
            ["vendor"] = "web",
            ["screen"] = RandomNumberGenerator.GetInt32(1, 3).ToString(),
            ["model"] = RandomNumberGenerator.GetInt32(1, 3).ToString(),
            ["imei"] = RandomDigits(15),
            ["network"] = new[] { "wifi", "4g", "5g" }[RandomNumberGenerator.GetInt32(3)],
            ["keyfrom"] = "webfanyi.webaitrans",
            ["keyid"] = YoudaoChatKeyId,
            ["mysticTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["yduuid"] = RandomHex(16),
            ["functionEnglishName"] = "LLM_translate",
            ["input"] = Uri.EscapeDataString(text),
            ["useTerm"] = "0",
            ["free"] = "true",
            ["singleBox"] = "false",
            ["fromLang"] = ToYoudaoLanguage(source, true),
            ["toLang"] = ToYoudaoLanguage(target, false),
            ["roundNo"] = RandomNumberGenerator.GetInt32(1, 51).ToString(),
            ["showSuggest"] = "0",
            ["token"] = auth.Token,
            ["source"] = "webaitrans"
        };
        var signed = BuildYoudaoSign(parameters, auth.SecretKey);
        parameters["sign"] = signed.Sign;
        parameters["pointParam"] = signed.PointParam;

        var boundary = "----YD" + RandomHex(16);
        var body = BuildMultipart(parameters, boundary);
        using var request = new HttpRequestMessage(HttpMethod.Post, YoudaoApi + "/translate_llm/v3/chat");
        AddYoudaoHeaders(request);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("Translation response exceeds 4 MB limit.");
        if (!response.IsSuccessStatusCode)
        {
            await BufferResponseAsync(response, cancellationToken);
            throw new HttpRequestException($"?? HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }
        var translated = await ReadYoudaoSseStreamAsync(response, progress, cancellationToken);
        return new TranslationResult(
            translated,
            TranslationProviderCatalog.DisplayName(TranslationProvider.Youdao),
            "auto");
    }

    /// <summary>有道网页版「普通翻译」：先取 translate/key 的密钥，再走 webtranslate/sse 流式返回。</summary>
    private async Task<TranslationResult> TranslateWithYoudaoWebAsync(
        string text,
        string source,
        string target,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _youdaoRequestSlots.WaitAsync(cancellationToken);
            try
            {
                return await TranslateWithYoudaoWebOnceAsync(text, source, target, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientYoudaoFailure(ex))
            {
                lastError = ex;
                _youdaoWebAuth = null;
            }
            finally
            {
                _youdaoRequestSlots.Release();
            }

            if (attempt < maximumAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(350 * (1 << (attempt - 1)) + RandomNumberGenerator.GetInt32(80, 260));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"有道普通翻译连续失败，已自动重试 {maximumAttempts - 1} 次：{InnermostMessage(lastError)}",
            lastError);
    }

    private async Task<TranslationResult> TranslateWithYoudaoWebOnceAsync(
        string text,
        string source,
        string target,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var auth = await GetYoudaoWebAuthAsync(cancellationToken);
        var parameters = new Dictionary<string, string>
        {
            ["product"] = YoudaoProduct,
            ["appVersion"] = "1",
            ["client"] = "webmain",
            ["mid"] = "1",
            ["vendor"] = "web",
            ["screen"] = "1",
            ["model"] = "1",
            ["imei"] = "1",
            ["network"] = "wifi",
            ["keyfrom"] = "webfanyi.webmain",
            ["keyid"] = YoudaoWebKeyId,
            ["keyId"] = YoudaoWebKeyId,
            ["mysticTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["yduuid"] = auth.Yduuid,
            ["modelName"] = "llmLite",
            ["useTerm"] = "false",
            ["i"] = Uri.EscapeDataString(text),
            ["from"] = ToYoudaoLanguage(source, true),
            ["to"] = ToYoudaoLanguage(target, false),
            ["signSecretKey"] = auth.SecretKey,
            ["token"] = auth.Token,
            ["source"] = "webmain"
        };
        var signed = BuildYoudaoSign(parameters, auth.SecretKey);
        parameters["sign"] = signed.Sign;
        parameters["pointParam"] = signed.PointParam;

        var boundary = "----YD" + RandomHex(16);
        var body = BuildMultipart(parameters, boundary);
        using var request = new HttpRequestMessage(HttpMethod.Post, YoudaoWebApi + "/webtranslate/sse");
        AddYoudaoHeaders(request);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("Translation response exceeds 4 MB limit.");
        if (!response.IsSuccessStatusCode)
        {
            await BufferResponseAsync(response, cancellationToken);
            throw new HttpRequestException($"有道普通翻译 HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }
        var translated = await ReadYoudaoSseStreamAsync(response, progress, cancellationToken);
        return new TranslationResult(
            translated,
            TranslationProviderCatalog.DisplayName(TranslationProvider.YoudaoWeb),
            "auto");
    }

    private async Task<YoudaoWebAuth> GetYoudaoWebAuthAsync(CancellationToken cancellationToken)
    {
        if (_youdaoWebAuth is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached;
        await _youdaoWebAuthLock.WaitAsync(cancellationToken);
        try
        {
            if (_youdaoWebAuth is { } current && current.ExpiresAt > DateTimeOffset.UtcNow) return current;
            var yduuid = RandomHex(16);
            var parameters = new Dictionary<string, string>
            {
                ["product"] = YoudaoProduct,
                ["appVersion"] = "12.0.0",
                ["client"] = "webmain",
                ["mid"] = "1",
                ["vendor"] = "web",
                ["screen"] = "1",
                ["model"] = "1",
                ["imei"] = "1",
                ["network"] = "wifi",
                ["keyfrom"] = "webfanyi.webmain",
                ["keyid"] = YoudaoWebKeyGetter,
                ["mysticTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                ["yduuid"] = yduuid,
                ["abtest"] = "0",
                ["targetKeyid"] = YoudaoWebKeyId
            };
            var signed = BuildYoudaoSign(parameters, YoudaoWebKeyGetterSecret);
            parameters["sign"] = signed.Sign;
            parameters["pointParam"] = signed.PointParam;
            var query = string.Join("&", parameters.Select(item =>
                Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));

            using var request = new HttpRequestMessage(HttpMethod.Post, YoudaoWebApi + "/translate/key?" + query);
            AddYoudaoHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await BufferResponseAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"有道普通翻译鉴权失败：HTTP {(int)response.StatusCode}", null, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var codeElement)
                && codeElement.TryGetInt32(out var code)
                && code != 0)
            {
                var message = root.TryGetProperty("msg", out var messageElement) ? messageElement.GetString() : null;
                throw new InvalidOperationException($"有道普通翻译鉴权失败：{message}（{code}）");
            }
            if (!root.TryGetProperty("data", out var data))
                throw new InvalidDataException("有道普通翻译鉴权返回格式异常。");
            var secretKey = data.TryGetProperty("secretKey", out var secretElement) ? secretElement.GetString() : null;
            var token = data.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("有道普通翻译鉴权失败：没有拿到密钥。");

            _youdaoWebAuth = new YoudaoWebAuth(secretKey, token, yduuid, DateTimeOffset.UtcNow.AddMinutes(30));
            return _youdaoWebAuth;
        }
        finally
        {
            _youdaoWebAuthLock.Release();
        }
    }

    private async Task<YoudaoAuth> GetYoudaoAuthAsync(CancellationToken cancellationToken)
    {
        if (_youdaoAuth is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached;
        await _youdaoAuthLock.WaitAsync(cancellationToken);
        try
        {
            if (_youdaoAuth is { } current && current.ExpiresAt > DateTimeOffset.UtcNow) return current;
            _youdaoAuth = null;
            var yduuid = RandomHex(16);
            var mysticTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var sign = Md5Hex($"client=fanyideskweb&mysticTime={mysticTime}&product={YoudaoProduct}&key={YoudaoPreSecret}");
            var query = new Dictionary<string, string>
            {
                ["keyid"] = YoudaoPreKeyId,
                ["sign"] = sign,
                ["client"] = "fanyideskweb",
                ["product"] = YoudaoProduct,
                ["appVersion"] = "12.0.0",
                ["vendor"] = "web",
                ["pointParam"] = "client,mysticTime,product",
                ["mysticTime"] = mysticTime,
                ["keyfrom"] = "fanyi.web",
                ["mid"] = "1",
                ["screen"] = "1",
                ["model"] = "1",
                ["network"] = "wifi",
                ["abtest"] = "0",
                ["yduuid"] = yduuid
            };
            var url = YoudaoApi + "/translate_llm/secret?" + string.Join("&",
                query.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddYoudaoHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await BufferResponseAsync(response, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : -1;
            if (code != 0 || !root.TryGetProperty("data", out var data))
            {
                var message = root.TryGetProperty("msg", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                throw new InvalidOperationException(message ?? "获取有道认证信息失败。");
            }
            var secretKey = data.GetProperty("secretKey").GetString();
            var token = data.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("有道认证信息为空。");
            _youdaoAuth = new YoudaoAuth(secretKey, token, DateTimeOffset.UtcNow.AddMinutes(30));
            return _youdaoAuth;
        }
        finally
        {
            _youdaoAuthLock.Release();
        }
    }

    private static YoudaoSign BuildYoudaoSign(IReadOnlyDictionary<string, string> parameters, string secretKey)
    {
        var keys = parameters.Where(item => !string.IsNullOrEmpty(item.Value))
            .Select(item => item.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        var signText = string.Join("&", keys.Select(key => key + "=" + parameters[key])) + "&key=" + secretKey;
        return new YoudaoSign(Md5Hex(signText), string.Join(",", keys.Append("key")));
    }

    private static string BuildMultipart(IReadOnlyDictionary<string, string> parameters, string boundary)
    {
        var body = new StringBuilder();
        foreach (var item in parameters)
        {
            body.Append("--").Append(boundary).Append("\r\n")
                .Append("Content-Disposition: form-data; name=\"").Append(item.Key).Append("\"\r\n\r\n")
                .Append(item.Value).Append("\r\n");
        }
        return body.Append("--").Append(boundary).Append("--\r\n").ToString();
    }

    private static async Task<string> ReadYoudaoSseStreamAsync(
        HttpResponseMessage response,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = new StringBuilder();
        long approximateBytes = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // ReadLine strips the delimiter. Count two bytes so chunked CRLF streams
            // cannot bypass the same response limit enforced for buffered providers.
            approximateBytes += Encoding.UTF8.GetByteCount(line) + 2;
            if (approximateBytes > MaxResponseBytes)
                throw new InvalidDataException("Translation response exceeds 4 MB limit.");
            AppendYoudaoSseLine(line, content, progress);
        }

        var translated = content.ToString().Trim();
        if (translated.Length == 0) throw new InvalidOperationException("Youdao returned an empty translation result.");
        return translated;
    }

    private static string ParseYoudaoSse(string raw)
    {
        var content = new StringBuilder();
        foreach (var line in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            AppendYoudaoSseLine(line, content, null);
        var translated = content.ToString().Trim();
        if (translated.Length == 0) throw new InvalidOperationException("Youdao returned an empty translation result.");
        return translated;
    }

    private static void AppendYoudaoSseLine(string line, StringBuilder content, IProgress<string>? progress)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var json = line[5..].Trim();
        if (json.Length == 0) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var codeElement)
                && codeElement.TryGetInt32(out var code)
                && code != 0)
            {
                var message = root.TryGetProperty("msg", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                throw new InvalidOperationException(message ?? $"Youdao error {code}");
            }
            // AI 通道返回 content，普通翻译通道返回 transIncre
            if ((root.TryGetProperty("content", out var value) || root.TryGetProperty("transIncre", out value))
                && value.ValueKind == JsonValueKind.String)
            {
                var fragment = value.GetString();
                if (string.IsNullOrEmpty(fragment)) return;
                content.Append(fragment);
                progress?.Report(content.ToString());
            }
        }
        catch (JsonException)
        {
            // The event stream also contains keepalive and completion markers.
        }
    }

    private static void AddYoudaoHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.Referrer = new Uri("https://fanyi.youdao.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://fanyi.youdao.com");
        request.Headers.TryAddWithoutValidation("User-Agent", YoudaoUserAgent);
    }

    private static bool IsTransientYoudaoFailure(Exception exception)
    {
        if (exception is HttpRequestException http)
        {
            return http.StatusCode is null
                   || http.StatusCode == HttpStatusCode.RequestTimeout
                   || http.StatusCode == HttpStatusCode.TooManyRequests
                   || (int)http.StatusCode >= 500;
        }
        if (exception is IOException or TimeoutException or TaskCanceledException) return true;
        if (exception is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.Any(IsTransientYoudaoFailure);
        if (exception.InnerException is not null && IsTransientYoudaoFailure(exception.InnerException)) return true;

        var message = exception.Message;
        return message.Contains("服务异常", StringComparison.OrdinalIgnoreCase)
               || message.Contains("稍后重试", StringComparison.OrdinalIgnoreCase)
               || message.Contains("频繁", StringComparison.OrdinalIgnoreCase)
               || message.Contains("token", StringComparison.OrdinalIgnoreCase)
               || message.Contains("认证", StringComparison.OrdinalIgnoreCase)
               || message.Contains("stream", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection", StringComparison.OrdinalIgnoreCase);
    }

    private static string InnermostMessage(Exception? exception)
    {
        if (exception is null) return "未知网络错误";
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception.Message;
    }

    private static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SnapTranslate/1.0");
        return client;
    }

    private static async Task BufferResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("翻译服务响应超过 4 MB 限制。");
        cancellationToken.ThrowIfCancellationRequested();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RandomHex(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static string RandomDigits(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => RandomNumberGenerator.GetInt32(10).ToString()));

    private static string ResolveTarget(string text, string configured)
    {
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)) return configured;
        return text.Any(ch => ch is >= '\u3400' and <= '\u9fff') ? "en" : "zh-CN";
    }

    private static string NormalizeLanguageCode(string value, bool allowAuto)
    {
        var code = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        if (allowAuto && code == "auto") return "auto";
        if (code.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        return code switch
        {
            "jp" => "ja",
            "kr" => "ko",
            _ => code == "auto" ? "en" : code
        };
    }

    private static string ToYoudaoLanguage(string value, bool allowAuto)
    {
        var code = NormalizeLanguageCode(value, allowAuto);
        if (allowAuto && code == "auto") return "auto";
        return code == "zh" ? "zh-CHS" : code;
    }

    private sealed record YoudaoAuth(string SecretKey, string Token, DateTimeOffset ExpiresAt);

    private sealed record YoudaoWebAuth(string SecretKey, string Token, string Yduuid, DateTimeOffset ExpiresAt);
    private sealed record YoudaoSign(string Sign, string PointParam);
}
