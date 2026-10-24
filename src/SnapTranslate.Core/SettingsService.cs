using System.Diagnostics;
using System.Text.Json;

namespace SnapTranslate;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    public SettingsService(string? dataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? AppDataPaths.ApplicationDataDirectory
            : Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string? LastLoadWarning { get; private set; }

    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private string HistoryPath => Path.Combine(DataDirectory, "history.json");
    private string DocumentTasksPath => Path.Combine(DataDirectory, "document-tasks.json");

    public AppSettings LoadSettings()
    {
        lock (_gate)
        {
            LastLoadWarning = null;
            var settings = Load(SettingsPath, new AppSettings());
            MigrateLegacyChatApi(settings);
            MigrateLegacyOllamaChannel(settings);
            Normalize(settings);
            return settings;
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ClipboardMaxItems = Math.Clamp(settings.ClipboardMaxItems, 100, 100_000);
        settings.ClipboardRetentionDays = Math.Clamp(settings.ClipboardRetentionDays, 0, 3650);
        settings.DocumentTranslationConcurrency = Math.Max(1, settings.DocumentTranslationConcurrency);
        Normalize(settings);
        WriteAtomic(SettingsPath, settings);
    }

    public List<HistoryEntry> LoadHistory()
    {
        lock (_gate)
        {
            return Load(HistoryPath, new List<HistoryEntry>());
        }
    }

    public void SaveHistory(IEnumerable<HistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        WriteAtomic(HistoryPath, history.Take(100).ToArray());
    }

    public List<DocumentTaskSnapshot> LoadDocumentTasks()
    {
        lock (_gate)
        {
            return Load(DocumentTasksPath, new List<DocumentTaskSnapshot>());
        }
    }

    public void SaveDocumentTasks(IEnumerable<DocumentTaskSnapshot> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        WriteAtomic(DocumentTasksPath, tasks.Take(500).ToArray());
    }

    /// <summary>统一收拾「自动顺序」和「大模型渠道」这两处列表字段。</summary>
    private static void Normalize(AppSettings settings)
    {
        settings.AutoChain = AppSettings.NormalizeAutoChain(settings.AutoChain);
        settings.ChatApiProfiles = AppSettings.NormalizeChatApiProfiles(settings.ChatApiProfiles);
        settings.ActiveChatApiProfileId = settings.ChatApiProfiles.Any(profile => profile.Id == settings.ActiveChatApiProfileId)
            ? settings.ActiveChatApiProfileId
            : settings.ChatApiProfiles.FirstOrDefault()?.Id ?? string.Empty;
    }

    /// <summary>
    /// 老版本的设置里只有一套大模型配置（ChatApiBaseUrl / ChatApiKey / ChatApiModel / ChatApiFormat），
    /// 升级后把这套配置搬成一条「默认渠道」，避免用户重新填一遍。
    /// </summary>
    private void MigrateLegacyChatApi(AppSettings settings)
    {
        if (settings.ChatApiProfiles.Count > 0 || !File.Exists(SettingsPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("ChatApiModel", out var modelElement)
                || modelElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modelElement.GetString()))
                return;

            var profile = new ChatApiProfile
            {
                Name = "默认渠道",
                BaseUrl = ReadLegacyString(root, "ChatApiBaseUrl") ?? "https://api.openai.com/v1",
                ApiKey = ReadLegacyString(root, "ChatApiKey") ?? string.Empty,
                Model = modelElement.GetString()!.Trim(),
                Format = root.TryGetProperty("ChatApiFormat", out var formatElement)
                         && formatElement.TryGetInt32(out var formatValue)
                         && Enum.IsDefined(typeof(ChatApiFormat), formatValue)
                    ? (ChatApiFormat)formatValue
                    : ChatApiFormat.OpenAi
            };
            settings.ChatApiProfiles.Add(profile);
            settings.ActiveChatApiProfileId = profile.Id;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("迁移旧的大模型配置失败: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Windows 端把 Ollama 并进了「自定义 API」：旧的 Ollama 通道会转成一条指向本地 /v1 的渠道，
    /// 这样老的本地模型配置不用重填也能继续用。
    /// </summary>
    private static void MigrateLegacyOllamaChannel(AppSettings settings)
    {
        if (settings.Provider != TranslationProvider.Ollama) return;
        var endpoint = string.IsNullOrWhiteSpace(settings.OllamaEndpoint)
            ? "http://127.0.0.1:11434"
            : settings.OllamaEndpoint.Trim().TrimEnd('/');
        var baseUrl = endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? endpoint : endpoint + "/v1";
        var model = settings.OllamaModel?.Trim() ?? string.Empty;

        var profile = settings.ChatApiProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new ChatApiProfile
            {
                Name = "Ollama（本地）",
                BaseUrl = baseUrl,
                Model = model
            };
            settings.ChatApiProfiles.Add(profile);
        }
        else if (string.IsNullOrWhiteSpace(profile.Model))
        {
            profile.Model = model;
        }

        settings.ActiveChatApiProfileId = profile.Id;
        settings.Provider = TranslationProvider.ChatApi;
    }

    private static string? ReadLegacyString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private T Load<T>(string path, T fallback)
    {
        if (!File.Exists(path)) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var warning = $"无法读取 {Path.GetFileName(path)}，已使用默认值：{ex.Message}";
            LastLoadWarning = string.IsNullOrWhiteSpace(LastLoadWarning)
                ? warning
                : LastLoadWarning + "；" + warning;
            Trace.TraceWarning(LastLoadWarning);
            PreserveInvalidFile(path);
            return fallback;
        }
    }

    private void WriteAtomic<T>(string path, T value)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DataDirectory);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private void PreserveInvalidFile(string path)
    {
        try
        {
            var backup = path + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(path, backup, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("无法备份损坏的配置文件 {0}: {1}", path, ex.Message);
        }
    }
}
