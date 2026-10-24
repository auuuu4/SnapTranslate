using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace SnapTranslate.Core.Tests;

public sealed class ClipboardItemTests
{
    [Fact]
    public void FavoriteAndNoteChangesNotifyBoundUiProperties()
    {
        var item = new ClipboardItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.IsFavorite = true;
        item.Note = "project note";

        Assert.Contains(nameof(ClipboardItem.IsFavorite), changed);
        Assert.Contains(nameof(ClipboardItem.FavoriteDisplay), changed);
        Assert.Contains(nameof(ClipboardItem.FavoriteActionText), changed);
        Assert.Contains(nameof(ClipboardItem.Note), changed);
        Assert.Contains(nameof(ClipboardItem.NoteDisplay), changed);
        Assert.Contains(nameof(ClipboardItem.NoteActionText), changed);
        Assert.Equal("★", item.FavoriteDisplay);
        Assert.Equal("取消收藏", item.FavoriteActionText);
        Assert.Equal("project note", item.NoteDisplay);
        Assert.Equal("编辑备注", item.NoteActionText);
    }
}

public sealed class SettingsServiceTests
{
    [Fact]
    public void NewSettingsDefaultToSimplifiedChinese()
    {
        var settings = new AppSettings();

        Assert.Equal("auto", settings.SourceLanguage);
        Assert.Equal("zh-CN", settings.TargetLanguage);
        Assert.Equal(2, settings.DocumentTranslationConcurrency);
        Assert.False(settings.NotifyScreenshotActions);
        Assert.True(settings.NotifyOcrResults);
        Assert.True(settings.NotifyErrors);
        Assert.True(settings.SilentOcrShowEditor);
        Assert.Equal("Ctrl+Alt+P", settings.PinnedImageHotkey);
        Assert.Equal(PinnedImageEdgeEffect.Gradient, settings.PinnedImageEdgeEffect);
    }

    [Fact]
    public void SettingsRoundTripIsAtomicAndClampsRetentionValues()
    {
        using var temporary = new TemporaryDirectory();
        var service = new SettingsService(temporary.Path);
        var settings = new AppSettings
        {
            Provider = TranslationProvider.Google,
            TargetLanguage = "en",
            ClipboardMaxItems = int.MaxValue,
            ClipboardRetentionDays = -10,
            DocumentTranslationConcurrency = int.MaxValue,
            NotifyScreenshotActions = true,
            NotifyOcrResults = false,
            NotifyErrors = false,
            SilentOcrShowEditor = false,
            PinnedImageHotkey = "Ctrl+Shift+P",
            PinnedImageEdgeEffect = PinnedImageEdgeEffect.None,
            KeepWindowOnTop = true
        };

        service.SaveSettings(settings);
        var loaded = service.LoadSettings();

        Assert.Equal(TranslationProvider.Google, loaded.Provider);
        Assert.Equal("en", loaded.TargetLanguage);
        Assert.True(loaded.KeepWindowOnTop);
        Assert.Equal(100_000, loaded.ClipboardMaxItems);
        Assert.Equal(0, loaded.ClipboardRetentionDays);
        Assert.Equal(int.MaxValue, loaded.DocumentTranslationConcurrency);
        Assert.True(loaded.NotifyScreenshotActions);
        Assert.False(loaded.NotifyOcrResults);
        Assert.False(loaded.NotifyErrors);
        Assert.False(loaded.SilentOcrShowEditor);
        Assert.Equal("Ctrl+Shift+P", loaded.PinnedImageHotkey);
        Assert.Equal(PinnedImageEdgeEffect.None, loaded.PinnedImageEdgeEffect);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public void InvalidSettingsArePreservedBeforeDefaultsAreReturned()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "settings.json"), "{not-json");
        var service = new SettingsService(temporary.Path);

        var loaded = service.LoadSettings();

        Assert.Equal(TranslationProvider.Auto, loaded.Provider);
        Assert.NotNull(service.LastLoadWarning);
        Assert.Single(Directory.EnumerateFiles(temporary.Path, "settings.json.invalid-*.bak"));
    }

    [Fact]
    public void LegacySettingsUseNotificationDefaults()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "settings.json"), "{\"CloseToTray\":false}");

        var loaded = new SettingsService(temporary.Path).LoadSettings();

        Assert.False(loaded.NotifyScreenshotActions);
        Assert.True(loaded.NotifyOcrResults);
        Assert.True(loaded.NotifyErrors);
        Assert.Equal(2, loaded.DocumentTranslationConcurrency);
        Assert.True(loaded.SilentOcrShowEditor);
    }

    [Fact]
    public void DocumentTasksRoundTripAndInterruptedTasksBecomeFailed()
    {
        using var temporary = new TemporaryDirectory();
        var service = new SettingsService(temporary.Path);
        var source = Path.Combine(temporary.Path, "report.xlsx");
        var snapshot = new DocumentTaskSnapshot
        {
            Id = Guid.NewGuid(),
            SourcePath = source,
            Status = DocumentTaskStatus.Processing,
            Progress = 42,
            StatusText = "正在翻译 42/100",
            CreatedAt = DateTime.Now.AddMinutes(-2)
        };

        service.SaveDocumentTasks(new[] { snapshot });
        var loaded = Assert.Single(service.LoadDocumentTasks());
        var restored = DocumentTranslationTask.Restore(loaded);

        Assert.Equal(snapshot.Id, restored.Id);
        Assert.Equal(Path.GetFullPath(source), restored.SourcePath);
        Assert.Equal(42, restored.Progress);
        Assert.Equal(DocumentTaskStatus.Failed, restored.Status);
        Assert.Contains("未完成", restored.StatusText, StringComparison.Ordinal);
    }
}

public sealed class DocumentTranslationServiceTests
{
    [Fact]
    public async Task DuplicateTextIsTranslatedOnceAndLineBreaksArePreserved()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "notes.txt");
        await File.WriteAllTextAsync(source, "Hello\r\nHello\r\n");
        var translator = new FakeTranslator(text => $"[{text}]");
        var service = new DocumentTranslationService(translator);

        var output = await service.TranslateAsync(source, new AppSettings(), null, CancellationToken.None);
        var translated = await File.ReadAllTextAsync(output);

        Assert.Equal("[Hello]\r\n[Hello]\r\n", translated);
        Assert.Equal(1, translator.Calls["Hello"]);
    }

    [Fact]
    public async Task SpreadsheetDuplicateCellTextIsTranslatedOnce()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "catalog.xlsx");
        using (var document = SpreadsheetDocument.Create(source, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var row = new S.Row(
                InlineCell("Repeat product name"),
                InlineCell("Repeat product name"),
                InlineCell("Unique description"));
            worksheetPart.Worksheet = new S.Worksheet(new S.SheetData(row));
            var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Catalog"
            });
            workbookPart.Workbook.Save();
        }
        var translator = new FakeTranslator(text => $"T({text})");
        var service = new DocumentTranslationService(translator);

        await service.TranslateAsync(source, new AppSettings(), null, CancellationToken.None);

        Assert.Equal(1, translator.Calls["Repeat product name"]);
        Assert.Equal(1, translator.Calls["Unique description"]);
    }

    private static S.Cell InlineCell(string text) => new()
    {
        DataType = S.CellValues.InlineString,
        InlineString = new S.InlineString(new S.Text(text))
    };

    [Fact]
    public async Task MarkdownCodeIsNotTranslated()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "guide.md");
        await File.WriteAllTextAsync(source, "# Welcome\n\nUse `keep_code` here.");
        var service = new DocumentTranslationService(new FakeTranslator(text => $"T({text})"));

        var output = await service.TranslateAsync(source, new AppSettings(), null, CancellationToken.None);
        var translated = await File.ReadAllTextAsync(output);

        Assert.Contains("`keep_code`", translated, StringComparison.Ordinal);
        Assert.Contains("T(Welcome)", translated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureDoesNotLeavePartialOutput()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "broken.txt");
        await File.WriteAllTextAsync(source, "Translate me");
        var service = new DocumentTranslationService(new FakeTranslator(_ => throw new HttpRequestException("offline")));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TranslateAsync(source, new AppSettings(), null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(temporary.Path, "broken_translation.txt")));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".*.tmp*"));
    }

    [Fact]
    public async Task LargeDocumentProgressIsThrottledToPercentageChanges()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "large.txt");
        await File.WriteAllLinesAsync(source,
            Enumerable.Range(1, 1_000).Select(index => $"Unique line {index:D4}"));
        var reports = new ConcurrentQueue<DocumentTranslationProgress>();
        var progress = new InlineProgress<DocumentTranslationProgress>(reports.Enqueue);
        var service = new DocumentTranslationService(new FakeTranslator(text => $"T({text})"));

        await service.TranslateAsync(source, new AppSettings(), progress, CancellationToken.None);

        var translationReports = reports.Where(item => item.Stage.StartsWith("正在翻译", StringComparison.Ordinal)).ToArray();
        Assert.InRange(translationReports.Length, 1, 87);
        Assert.InRange(reports.Count, 1, 90);
        Assert.Equal(translationReports.Length,
            translationReports.Select(item => item.Percentage).Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ConfiguredConcurrencyLimitsParallelDocumentRequests(int concurrency)
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, $"parallel-{concurrency}.txt");
        await File.WriteAllLinesAsync(source,
            Enumerable.Range(1, 12).Select(index => $"Unique request {index:D2}"));
        var translator = new TrackingTranslator();
        var service = new DocumentTranslationService(translator);

        await service.TranslateAsync(source, new AppSettings
        {
            DocumentTranslationConcurrency = concurrency
        }, null, CancellationToken.None);

        Assert.Equal(concurrency, translator.MaxActive);
    }
}

public sealed class TranslationServiceTests
{
    [Fact]
    public async Task YoudaoReportsCumulativeStreamingFragments()
    {
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/secret", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"code\":0,\"data\":{\"secretKey\":\"test-secret\",\"token\":\"test-token\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"content\":\"\\u4f60\"}\n" +
                    "data: {\"content\":\"\\u597d\"}\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var service = new TranslationService(new HttpClient(handler));
        var reports = new List<string>();

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Youdao,
            SourceLanguage = "en",
            TargetLanguage = "zh-CN"
        }, new InlineProgress<string>(reports.Add), CancellationToken.None);

        Assert.Equal(new[] { "\u4f60", "\u4f60\u597d" }, reports);
        Assert.Equal("\u4f60\u597d", result.Text);
        Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProvider.Youdao), result.Provider);
    }

    [Fact]
    public async Task YoudaoReceivesConfiguredSourceAndTargetLanguages()
    {
        string? chatBody = null;
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/secret", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"code\":0,\"data\":{\"secretKey\":\"test-secret\",\"token\":\"test-token\"}}",
                        Encoding.UTF8, "application/json")
                };
            }

            chatBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"content\":\"你好\"}\n", Encoding.UTF8, "text/event-stream")
            };
        });
        var service = new TranslationService(new HttpClient(handler));

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Youdao,
            SourceLanguage = "en",
            TargetLanguage = "zh-CN"
        });

        Assert.Equal("你好", result.Text);
        Assert.NotNull(chatBody);
        Assert.Contains("name=\"fromLang\"\r\n\r\nen\r\n", chatBody, StringComparison.Ordinal);
        Assert.Contains("name=\"toLang\"\r\n\r\nzh-CHS\r\n", chatBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YoudaoRefreshesAuthenticationAndRetriesTransientServiceErrors()
    {
        var secretRequests = 0;
        var chatRequests = 0;
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/secret", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref secretRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"code\":0,\"data\":{\"secretKey\":\"test-secret\",\"token\":\"test-token\"}}",
                        Encoding.UTF8, "application/json")
                };
            }

            var attempt = Interlocked.Increment(ref chatRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(attempt == 1
                    ? "data: {\"code\":500,\"msg\":\"服务异常，请稍后重试\"}\n"
                    : "data: {\"content\":\"成功\"}\n", Encoding.UTF8, "text/event-stream")
            };
        });
        var service = new TranslationService(new HttpClient(handler));

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Youdao,
            SourceLanguage = "en",
            TargetLanguage = "zh-CN"
        });

        Assert.Equal("成功", result.Text);
        Assert.Equal(2, secretRequests);
        Assert.Equal(2, chatRequests);
    }

    [Fact]
    public async Task InvalidOllamaEndpointIsRejectedBeforeNetworkAccess()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("network should not be used"));
        var service = new TranslationService(new HttpClient(handler));
        var settings = new AppSettings
        {
            Provider = TranslationProvider.Ollama,
            OllamaEndpoint = "file:///tmp/model",
            OllamaModel = "test"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync("Hello", settings));

        Assert.Contains("HTTP", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GoogleResponseIsParsedWithoutBufferingProviderState()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[[[\"你好\",\"Hello\",null,null,1]],null,\"en\"]", Encoding.UTF8, "application/json")
        });
        var service = new TranslationService(new HttpClient(handler));

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Google,
            SourceLanguage = "auto",
            TargetLanguage = "zh-CN"
        });

        Assert.Equal("你好", result.Text);
        Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProvider.Google), result.Provider);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1, handler.RequestCount);
    }
}


public sealed class ChatApiTranslationTests
{
    [Fact]
    public async Task OpenAiRequestCarriesModelKeyAndCustomPrompt()
    {
        Uri? requestUri = null;
        string? authorization = null;
        string? body = null;
        var handler = new StubHttpHandler(request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"你好\"}}]}");
        });
        var service = new TranslationService(new HttpClient(handler));

        var settings = WithChatApi("https://api.example.com/v1", "sk-test", "gpt-test");
        settings.SourceLanguage = "en";
        settings.TargetLanguage = "zh-CN";
        settings.TranslationPrompt = "把 {source} 译成 {target}，只输出译文。";
        var result = await service.TranslateAsync("Hello", settings);

        Assert.Equal("https://api.example.com/v1/chat/completions", requestUri!.ToString());
        Assert.Equal("Bearer sk-test", authorization);
        using var document = System.Text.Json.JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("把 English 译成 简体中文", messages[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Hello", messages[1].GetProperty("content").GetString());
        Assert.Equal("你好", result.Text);
        Assert.Equal("测试渠道 · gpt-test", result.Provider);
    }

    [Fact]
    public async Task AnthropicRequestUsesMessagesEndpointAndApiKeyHeaders()
    {
        Uri? requestUri = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? body = null;
        var handler = new StubHttpHandler(request =>
        {
            requestUri = request.RequestUri;
            foreach (var header in request.Headers)
                headers[header.Key] = string.Join(",", header.Value);
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{\"content\":[{\"type\":\"text\",\"text\":\"你好\"}]}");
        });
        var service = new TranslationService(new HttpClient(handler));

        var settings = WithChatApi("https://api.anthropic.com/v1", "sk-ant-test", "claude-test", ChatApiFormat.Anthropic);
        settings.SourceLanguage = "en";
        settings.TargetLanguage = "zh-CN";
        var result = await service.TranslateAsync("Hello", settings);

        Assert.Equal("https://api.anthropic.com/v1/messages", requestUri!.ToString());
        Assert.Equal("sk-ant-test", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        using var document = System.Text.Json.JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("claude-test", root.GetProperty("model").GetString());
        Assert.Contains("简体中文", root.GetProperty("system").GetString()!, StringComparison.Ordinal);
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("你好", result.Text);
        Assert.Equal("测试渠道 · claude-test", result.Provider);
    }

    [Fact]
    public async Task ModelListIsRequestedFromModelsEndpointAndSorted()
    {
        Uri? requestUri = null;
        var handler = new StubHttpHandler(request =>
        {
            requestUri = request.RequestUri;
            return Json("{\"data\":[{\"id\":\"gpt-b\"},{\"id\":\"gpt-a\"},{\"id\":\"gpt-b\"}]}");
        });
        var service = new TranslationService(new HttpClient(handler));

        var models = await service.ListModelsAsync(WithChatApi("https://api.example.com/v1/", "sk-test", "unused"));

        Assert.Equal("https://api.example.com/v1/models", requestUri!.ToString());
        Assert.Equal(new[] { "gpt-a", "gpt-b" }, models);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task InvalidBaseUrlIsRejectedBeforeNetworkAccess()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("network should not be used"));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("Hello", WithChatApi("file:///tmp/model", model: "gpt-test")));

        Assert.Contains("HTTP", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AnthropicWithoutApiKeyFailsBeforeNetworkAccess()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("network should not be used"));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("Hello", WithChatApi("https://api.anthropic.com/v1", model: "claude-test", format: ChatApiFormat.Anthropic)));

        Assert.Contains("API Key", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ApiErrorSurfacesStatusAndProviderMessage()
    {
        var handler = new StubHttpHandler(_ => Json(
            "{\"error\":{\"message\":\"invalid api key\"}}",
            HttpStatusCode.Unauthorized));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TranslateAsync("Hello", WithChatApi("https://api.example.com/v1", "sk-test", "gpt-test")));

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.Contains("invalid api key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptTemplateFallsBackToDefaultAndReplacesPlaceholders()
    {
        var fallback = TranslationPrompt.Build(string.Empty, "auto", "zh-CN");
        Assert.Contains("简体中文", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("{target}", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("{source}", fallback, StringComparison.Ordinal);

        var custom = TranslationPrompt.Build("translate from {SOURCE} to {target}", "en", "ja");
        Assert.Equal("translate from English to 日本語", custom);
    }

    [Fact]
    public void DefaultPromptIsStoredAsEmptySoBuiltInImprovementsApply()
    {
        Assert.Equal(string.Empty, TranslationPrompt.NormalizeForStorage(TranslationPrompt.Default));
        Assert.Equal("自定义提示词", TranslationPrompt.NormalizeForStorage("  自定义提示词 "));
    }

    [Fact]
    public void CloneDeepCopiesChatApiProfiles()
    {
        var original = WithChatApi("https://api.example.com/v1", "sk-test", "gpt-test", ChatApiFormat.Anthropic);
        original.TranslationPrompt = "只输出译文";

        var clone = original.Clone();
        clone.ChatApiProfiles[0].Model = "changed";

        Assert.Equal("gpt-test", original.ChatApiProfiles[0].Model);
        Assert.Equal("anthropic", clone.ChatApiProfiles[0].Format.ToString().ToLowerInvariant());
        Assert.Equal("只输出译文", clone.TranslationPrompt);
        Assert.True(clone.IsChatApiConfigured);
        Assert.NotSame(original.ChatApiProfiles, clone.ChatApiProfiles);
    }

    [Fact]
    public void ChatApiProfilesRoundTripThroughSettingsFile()
    {
        using var temporary = new TemporaryDirectory();
        var service = new SettingsService(temporary.Path);
        var settings = new AppSettings { Provider = TranslationProvider.ChatApi, TranslationPrompt = "把内容译成{target}" };
        settings.ChatApiProfiles.Add(new ChatApiProfile
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "sk-test",
            Model = "deepseek-chat"
        });
        settings.ChatApiProfiles.Add(new ChatApiProfile
        {
            Name = "Claude",
            BaseUrl = "https://api.anthropic.com/v1",
            ApiKey = "sk-ant",
            Model = "claude-test",
            Format = ChatApiFormat.Anthropic
        });
        settings.ActiveChatApiProfileId = settings.ChatApiProfiles[1].Id;

        service.SaveSettings(settings);
        var loaded = service.LoadSettings();

        Assert.Equal(2, loaded.ChatApiProfiles.Count);
        Assert.Equal("DeepSeek", loaded.ChatApiProfiles[0].Name);
        Assert.Equal("Claude", loaded.ChatApiProfiles[1].Name);
        Assert.Equal(ChatApiFormat.Anthropic, loaded.ChatApiProfiles[1].Format);
        Assert.Equal("Claude", loaded.ActiveChatApiProfile?.Name);
        Assert.Equal("把内容译成{target}", loaded.TranslationPrompt);
    }

    [Fact]
    public void LegacySingleChatApiSettingsAreMigratedIntoAProfile()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Path);
        File.WriteAllText(Path.Combine(temporary.Path, "settings.json"),
            """
            {
              "Provider": 6,
              "ChatApiBaseUrl": "https://api.deepseek.com/v1",
              "ChatApiKey": "sk-legacy",
              "ChatApiModel": "deepseek-chat",
              "ChatApiFormat": 2,
              "TargetLanguage": "zh-CN"
            }
            """);

        var loaded = new SettingsService(temporary.Path).LoadSettings();

        var profile = Assert.Single(loaded.ChatApiProfiles);
        Assert.Equal("默认渠道", profile.Name);
        Assert.Equal("https://api.deepseek.com/v1", profile.BaseUrl);
        Assert.Equal("sk-legacy", profile.ApiKey);
        Assert.Equal("deepseek-chat", profile.Model);
        Assert.Equal(ChatApiFormat.Responses, profile.Format);
        Assert.Equal(profile.Id, loaded.ActiveChatApiProfileId);
    }

    [Fact]
    public void LegacyOllamaChannelIsMigratedIntoACustomApiProfile()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Path);
        File.WriteAllText(Path.Combine(temporary.Path, "settings.json"),
            """
            {
              "Provider": 4,
              "OllamaEndpoint": "http://127.0.0.1:11434",
              "OllamaModel": "qwen2.5:7b",
              "TargetLanguage": "zh-CN"
            }
            """);

        var loaded = new SettingsService(temporary.Path).LoadSettings();

        Assert.Equal(TranslationProvider.ChatApi, loaded.Provider);
        var profile = Assert.Single(loaded.ChatApiProfiles);
        Assert.Equal("Ollama（本地）", profile.Name);
        Assert.Equal("http://127.0.0.1:11434/v1", profile.BaseUrl);
        Assert.Equal("qwen2.5:7b", profile.Model);
        Assert.Equal(profile.Id, loaded.ActiveChatApiProfileId);
    }

    [Fact]
    public async Task TranslationUsesTheActiveProfile()
    {
        string? body = null;
        var handler = new StubHttpHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var service = new TranslationService(new HttpClient(handler));
        var settings = new AppSettings { Provider = TranslationProvider.ChatApi };
        settings.ChatApiProfiles.Add(new ChatApiProfile
        {
            Name = "旧渠道",
            BaseUrl = "https://old.example.com/v1",
            Model = "old-model"
        });
        var active = new ChatApiProfile
        {
            Name = "新渠道",
            BaseUrl = "https://new.example.com/v1",
            ApiKey = "sk-new",
            Model = "new-model"
        };
        settings.ChatApiProfiles.Add(active);
        settings.ActiveChatApiProfileId = active.Id;

        var result = await service.TranslateAsync("Hello", settings);

        using var document = System.Text.Json.JsonDocument.Parse(body!);
        Assert.Equal("new-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("新渠道 · new-model", result.Provider);
    }

    [Fact]
    public async Task TruncatedOutputIsReportedInsteadOfReturningHalfText()
    {
        var handler = new StubHttpHandler(_ => Json(
            "{\"choices\":[{\"message\":{\"content\":\"半截\"},\"finish_reason\":\"length\"}]}"));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("Hello", WithChatApi("https://api.example.com/v1", model: "gpt-test")));

        Assert.Contains("长度上限", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesRequestUsesResponsesEndpointAndParsesOutputText()
    {
        Uri? requestUri = null;
        string? authorization = null;
        string? body = null;
        var handler = new StubHttpHandler(request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{\"object\":\"response\",\"status\":\"completed\",\"output\":[" +
                        "{\"type\":\"reasoning\",\"summary\":[]}," +
                        "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"你好\"}]}]}");
        });
        var service = new TranslationService(new HttpClient(handler));

        var settings = WithChatApi("https://api.example.com/v1", "sk-test", "gpt-5-mini", ChatApiFormat.Responses);
        settings.SourceLanguage = "en";
        settings.TargetLanguage = "zh-CN";
        settings.TranslationPrompt = "把 {source} 译成 {target}，只输出译文。";
        var result = await service.TranslateAsync("Hello", settings);

        Assert.Equal("https://api.example.com/v1/responses", requestUri!.ToString());
        Assert.Equal("Bearer sk-test", authorization);
        using var document = System.Text.Json.JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("gpt-5-mini", root.GetProperty("model").GetString());
        Assert.Contains("把 English 译成 简体中文", root.GetProperty("instructions").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Hello", root.GetProperty("input").GetString());
        Assert.Equal("你好", result.Text);
        Assert.Equal("测试渠道 · gpt-5-mini", result.Provider);
    }

    [Fact]
    public async Task IncompleteResponsesResultIsReportedAsTruncated()
    {
        var handler = new StubHttpHandler(_ => Json(
            "{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}," +
            "\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"半截\"}]}]}"));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("Hello", WithChatApi("https://api.example.com/v1", model: "gpt-5-mini", format: ChatApiFormat.Responses)));

        Assert.Contains("长度上限", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogKeepsChannelAndFormatNamesUnique()
    {
        var channelNames = TranslationProviderCatalog.SelectableProviders
            .Select(TranslationProviderCatalog.DisplayName)
            .ToArray();
        Assert.Equal(channelNames.Length, channelNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("自定义 API", channelNames);
        Assert.DoesNotContain(channelNames, name => name.StartsWith("Ollama", StringComparison.Ordinal));

        // 不需要配置的通道，名字里要标明「内置免费」；需要配置的「自定义 API」不带后缀
        Assert.Contains("有道 AI（内置免费）", channelNames);
        Assert.Contains("腾讯 Transmart（内置免费）", channelNames);
        Assert.Contains("MyMemory（内置免费）", channelNames);
        Assert.Contains("Google（内置免费）", channelNames);
        Assert.DoesNotContain("自定义 API", channelNames.Where(name => name.Contains('（', StringComparison.Ordinal)));

        var formatNames = TranslationProviderCatalog.SelectableFormats
            .Select(TranslationProviderCatalog.FormatDisplayName)
            .ToArray();
        Assert.Equal(new[] { "Chat Completions", "Responses", "Messages" }, formatNames);

        var chatSettings = WithChatApi("https://api.example.com/v1", "sk-test", "gpt-5-mini", ChatApiFormat.Responses, "我的渠道");
        chatSettings.ChatApiProfiles[0].Name = "我的渠道";
        Assert.Equal("我的渠道 · Responses · gpt-5-mini", TranslationProviderCatalog.Describe(chatSettings));
        Assert.Equal(
            TranslationProviderCatalog.DisplayName(TranslationProvider.Youdao),
            TranslationProviderCatalog.Describe(new AppSettings { Provider = TranslationProvider.Youdao }));
    }

    /// <summary>构造一份只配置了一条自定义渠道的设置。</summary>
    internal static AppSettings WithChatApi(
        string baseUrl,
        string apiKey = "",
        string model = "",
        ChatApiFormat format = ChatApiFormat.OpenAi,
        string name = "测试渠道")
    {
        var settings = new AppSettings { Provider = TranslationProvider.ChatApi };
        settings.ChatApiProfiles.Add(new ChatApiProfile
        {
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            Format = format
        });
        return settings;
    }

    private static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
}


public sealed class AutoChainTests
{
    [Fact]
    public async Task AutoUsesConfiguredOrder()
    {
        var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("googleapis", request.RequestUri!.Host, StringComparison.Ordinal);
            return Json("[[[\"你好\",\"Hello\",null,null,1]],null,\"en\"]");
        });
        var service = new TranslationService(new HttpClient(handler));

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Auto,
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
            AutoChain = [TranslationProvider.Google, TranslationProvider.Transmart]
        });

        Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProvider.Google), result.Provider);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AutoFallsThroughTheChainWhenAChannelFails()
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.Host.Contains("qq.com", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json("[[[\"你好\",\"Hello\",null,null,1]],null,\"en\"]"));
        var service = new TranslationService(new HttpClient(handler));

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Auto,
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
            AutoChain = [TranslationProvider.Transmart, TranslationProvider.Google]
        });

        Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProvider.Google), result.Provider);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AutoSkipsChannelsThatAreNotConfigured()
    {
        var handler = new StubHttpHandler(request =>
        {
            Assert.Contains("googleapis", request.RequestUri!.Host, StringComparison.Ordinal);
            return Json("[[[\"你好\",\"Hello\",null,null,1]],null,\"en\"]");
        });
        var service = new TranslationService(new HttpClient(handler));

        var result = await service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Auto,
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
            AutoChain = [TranslationProvider.Ollama, TranslationProvider.ChatApi, TranslationProvider.Google]
        });

        Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProvider.Google), result.Provider);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AutoReportsWhenNoChannelInTheChainIsUsable()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("network should not be used"));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync("Hello", new AppSettings
        {
            Provider = TranslationProvider.Auto,
            AutoChain = [TranslationProvider.Ollama, TranslationProvider.ChatApi]
        }));

        Assert.Contains("自动顺序", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void NormalizeDropsAutoItselfAndDuplicates()
    {
        var normalized = AppSettings.NormalizeAutoChain(
        [
            TranslationProvider.Google,
            TranslationProvider.Auto,
            TranslationProvider.Youdao,
            TranslationProvider.Google
        ]);

        Assert.Equal([TranslationProvider.Google, TranslationProvider.Youdao], normalized);
    }

    [Fact]
    public void NormalizeDropsOllamaBecauseItIsMergedIntoCustomApi()
    {
        var normalized = AppSettings.NormalizeAutoChain(
            [TranslationProvider.Ollama, TranslationProvider.Google]);

        Assert.Equal([TranslationProvider.Google], normalized);
    }

    [Fact]
    public void EmptyChainFallsBackToTheDefaultOrder()
    {
        Assert.Equal(AppSettings.DefaultAutoChain, AppSettings.NormalizeAutoChain(null));
        Assert.Equal(AppSettings.DefaultAutoChain, AppSettings.NormalizeAutoChain([]));
    }

    [Fact]
    public void AutoChainRoundTripsThroughSettingsFile()
    {
        using var temporary = new TemporaryDirectory();
        var service = new SettingsService(temporary.Path);
        service.SaveSettings(new AppSettings
        {
            Provider = TranslationProvider.Auto,
            AutoChain = [TranslationProvider.Google, TranslationProvider.Youdao]
        });

        var loaded = service.LoadSettings();

        Assert.Equal([TranslationProvider.Google, TranslationProvider.Youdao], loaded.AutoChain);
    }

    [Fact]
    public void CloneDeepCopiesAutoChain()
    {
        var original = new AppSettings { AutoChain = [TranslationProvider.Google] };

        var clone = original.Clone();
        clone.AutoChain.Add(TranslationProvider.Youdao);

        Assert.Single(original.AutoChain);
        Assert.Equal(2, clone.AutoChain.Count);
    }

    private static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
}

public sealed class YoudaoWebTests
{
    [Fact]
    public async Task PlainYoudaoFetchesKeyAndStreamsTranslation()
    {
        var paths = new List<string>();
        var translateBody = "";
        var handler = new StubHttpHandler(request =>
        {
            var uri = request.RequestUri!;
            paths.Add(uri.AbsolutePath);
            if (uri.AbsolutePath.EndsWith("/translate/key", StringComparison.Ordinal))
                return Json("{\"data\":{\"secretKey\":\"secret-1\",\"token\":\"token-1\"},\"code\":0,\"msg\":\"OK\"}");
            translateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event:begin\n" +
                    "data:{\"type\":\"zh-CHS2en\"}\n\n" +
                    "event:message\n" +
                    "data:{\"transIncre\":\"你\"}\n\n" +
                    "event:message\n" +
                    "data:{\"transIncre\":\"好\"}\n\n" +
                    "event:end\n" +
                    "data:{\"type\":\"zh-CHS2en\"}\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var service = new TranslationService(new HttpClient(handler));
        var reports = new List<string>();

        var result = await service.TranslateAsync("你好", new AppSettings
        {
            Provider = TranslationProvider.YoudaoWeb,
            SourceLanguage = "zh",
            TargetLanguage = "en"
        }, new InlineProgress<string>(reports.Add));

        Assert.Equal("你好", result.Text);
        Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProvider.YoudaoWeb), result.Provider);
        Assert.Equal(2, paths.Count);
        Assert.Contains("/translate/key", paths[0], StringComparison.Ordinal);
        Assert.Contains("/webtranslate/sse", paths[1], StringComparison.Ordinal);
        Assert.Contains("name=\"i\"", translateBody, StringComparison.Ordinal);
        Assert.Contains("name=\"pointParam\"", translateBody, StringComparison.Ordinal);
        Assert.Contains("name=\"modelName\"", translateBody, StringComparison.Ordinal);
        Assert.Equal(new[] { "你", "你好" }, reports);
    }

    [Fact]
    public async Task PlainYoudaoReusesTheCachedKey()
    {
        var keyRequests = 0;
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/translate/key", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref keyRequests);
                return Json("{\"data\":{\"secretKey\":\"s\",\"token\":\"t\"},\"code\":0}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event:message\ndata:{\"transIncre\":\"ok\"}\n", Encoding.UTF8, "text/event-stream")
            };
        });
        var service = new TranslationService(new HttpClient(handler));
        var settings = new AppSettings { Provider = TranslationProvider.YoudaoWeb };

        await service.TranslateAsync("a", settings);
        await service.TranslateAsync("b", settings);

        Assert.Equal(1, keyRequests);
    }

    [Fact]
    public async Task PlainYoudaoSurfacesAuthFailureMessage()
    {
        var handler = new StubHttpHandler(_ => Json("{\"code\":40,\"msg\":\"签名错误\"}"));
        var service = new TranslationService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync("你好", new AppSettings
        {
            Provider = TranslationProvider.YoudaoWeb
        }));

        Assert.Contains("签名错误", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
}

internal sealed class FakeTranslator(Func<string, string> translate) : ITranslationService
{
    public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    public Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.AddOrUpdate(text, 1, (_, count) => count + 1);
        return Task.FromResult(new TranslationResult(translate(text), "Fake", "en"));
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(respond(request));
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class TrackingTranslator : ITranslationService
{
    private int _active;
    private int _maxActive;

    public int MaxActive => Volatile.Read(ref _maxActive);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _active);
        UpdateMaximum(active);
        try
        {
            await Task.Delay(25, cancellationToken);
            return new TranslationResult($"T({text})", "Tracking", "en");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void UpdateMaximum(int value)
    {
        var current = Volatile.Read(ref _maxActive);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref _maxActive, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SnapTranslate.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
