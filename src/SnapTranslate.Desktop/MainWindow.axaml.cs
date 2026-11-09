using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using SnapTranslate.Desktop.Capture;
using SnapTranslate.Desktop.CaptureTranslation;
using SnapTranslate.Desktop.Clipboard;
using SnapTranslate.Desktop.Platform;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace SnapTranslate.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsService _storage;
    private readonly TranslationService _translator = new();
    private readonly DocumentTranslationService _documentTranslator;
    private readonly ClipboardRepository _clipboardRepository;
    private IClipboardMonitor? _clipboardMonitor;
    private ClipboardWindow? _clipboardWindow;
    private bool _captureInProgress;
    private readonly ObservableCollection<HistoryEntry> _history;
    private readonly ObservableCollection<DocumentTranslationTask> _documentTasks = [];
    private readonly ObservableCollection<ChatApiProfile> _chatProfiles = [];
    private readonly SemaphoreSlim _documentTaskSlots = new(2, 2);
    private AppSettings _settings;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? dataDirectory)
    {
        InitializeComponent();
        _storage = new SettingsService(dataDirectory);
        _settings = _storage.LoadSettings();
        _history = new ObservableCollection<HistoryEntry>(_storage.LoadHistory());
        _documentTranslator = new DocumentTranslationService(_translator);
        HistoryList.ItemsSource = _history;
        DocumentTaskList.ItemsSource = _documentTasks;
        ChatProfileList.ItemsSource = _chatProfiles;
        InitializeProviderCombo();
        InitializeLanguageCombos();
        LoadSettingsControls();
        RegisterHotkeys(showStatus: false);

        _clipboardRepository = new ClipboardRepository(_storage.DataDirectory);
        if (OperatingSystem.IsMacOS())
        {
            var runner = new MacClipboardHelperRunner(_clipboardRepository.ImagesDirectory);
            var monitor = new MacPasteboardMonitor(runner);
            monitor.Captured += (_, capture) =>
            {
                try
                {
                    _clipboardRepository.InsertOrUpdate(capture);
                }
                catch (Exception ex)
                {
                    FooterStatus.Text = $"剪贴板记录失败：{ex.Message}";
                }
            };
            monitor.Start();
            _clipboardMonitor = monitor;
            PlatformServices.Clipboard = monitor;
        }
        UpdateDocumentTaskCount();
        FooterStatus.Text = _storage.LastLoadWarning ?? "就绪";
        Closed += Window_Closed;
        Closing += Window_ClosingHandler;
    }

    private async void TranslateInput_Click(object? sender, RoutedEventArgs e) =>
        await TranslateInputAsync();

    private async Task TranslateInputAsync()
    {
        var source = InputText.Text?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            FooterStatus.Text = "请输入需要翻译的文本";
            return;
        }

        TranslateButton.IsEnabled = false;
        FooterStatus.Text = "正在翻译...";
        try
        {
            var requestSettings = CreateTranslationSettingsSnapshot();
            requestSettings.TargetLanguage = SelectedTag(QuickTargetCombo, _settings.TargetLanguage);
            var result = await _translator.TranslateAsync(source, requestSettings);
            InputResultText.Text = result.Text;
            _history.Insert(0, new HistoryEntry
            {
                CreatedAt = DateTime.Now,
                Source = source,
                Translation = result.Text,
                Provider = result.Provider
            });
            while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
            _storage.SaveHistory(_history);
            FooterStatus.Text = $"翻译完成 · {result.Provider}";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = ErrorMessage(ex);
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    private void ClearInput_Click(object? sender, RoutedEventArgs e)
    {
        InputText.Clear();
        InputResultText.Clear();
        FooterStatus.Text = "就绪";
    }

    private async void CopyInputResult_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputResultText.Text) && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(InputResultText.Text);
    }


    private async void AddDocumentFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加文档翻译任务",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("支持的文档")
                {
                    Patterns = ["*.txt", "*.md", "*.markdown", "*.csv", "*.tsv", "*.html", "*.htm", "*.docx", "*.xlsx", "*.pptx", "*.pdf"]
                }
            ]
        });
        QueueDocumentFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private void QueueDocumentFiles(IEnumerable<string> paths)
    {
        var files = paths.Where(DocumentTranslationService.IsSupported)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        foreach (var path in files)
        {
            var task = new DocumentTranslationTask(path);
            _documentTasks.Insert(0, task);
            _ = ProcessDocumentTaskAsync(task, CreateDocumentSettingsSnapshot());
        }
        if (files.Length > 0)
        {
            MainTabs.SelectedIndex = 2;
            FooterStatus.Text = $"已添加 {files.Length} 个文档任务";
        }
        UpdateDocumentTaskCount();
    }

    private async Task ProcessDocumentTaskAsync(DocumentTranslationTask task, AppSettings settings)
    {
        var acquired = false;
        try
        {
            task.StatusText = "等待处理";
            await _documentTaskSlots.WaitAsync(task.Cancellation.Token);
            acquired = true;
            task.Status = DocumentTaskStatus.Processing;
            task.Progress = 1;
            task.StatusText = "正在准备";
            var progress = new Progress<DocumentTranslationProgress>(value =>
            {
                task.Progress = value.Percentage;
                task.StatusText = value.Stage;
            });
            task.OutputPath = await Task.Run(
                () => _documentTranslator.TranslateAsync(task.SourcePath, settings, progress, task.Cancellation.Token),
                task.Cancellation.Token);
            task.Progress = 100;
            task.Status = DocumentTaskStatus.Completed;
            task.StatusText = Path.GetFileName(task.OutputPath);
        }
        catch (OperationCanceledException)
        {
            task.Status = DocumentTaskStatus.Canceled;
            task.StatusText = "任务已取消";
        }
        catch (Exception ex)
        {
            task.Status = DocumentTaskStatus.Failed;
            task.Error = ErrorMessage(ex);
            task.StatusText = task.Error;
        }
        finally
        {
            if (acquired) _documentTaskSlots.Release();
            UpdateDocumentTaskCount();
        }
    }

    private void CancelDocumentTask_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentTranslationTask { CanCancel: true } task)
            task.Cancellation.Cancel();
    }

    private void OpenDocumentOutput_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DocumentTranslationTask { CanOpen: true } task) return;
        if (OperatingSystem.IsMacOS())
        {
            var startInfo = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(task.OutputPath);
            Process.Start(startInfo);
        }
        else
        {
            Process.Start(new ProcessStartInfo(task.OutputPath) { UseShellExecute = true });
        }
    }

    private void ClearCompletedDocumentTasks_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var task in _documentTasks
                     .Where(item => item.Status is DocumentTaskStatus.Completed or DocumentTaskStatus.Failed or DocumentTaskStatus.Canceled)
                     .ToArray())
        {
            task.Cancellation.Dispose();
            _documentTasks.Remove(task);
        }
        UpdateDocumentTaskCount();
    }

    private void UpdateDocumentTaskCount()
    {
        var active = _documentTasks.Count(task => task.Status is DocumentTaskStatus.Queued or DocumentTaskStatus.Processing);
        DocumentTaskCountText.Text = active == 0
            ? $"{_documentTasks.Count} 个任务"
            : $"{active} 个进行中 · {_documentTasks.Count} 个任务";
    }

    private async void BrowseDocumentOutputDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文档翻译输出目录",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) DocumentOutputDirectoryText.Text = path;
    }

    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            CommitProfileFields();
            ApplySettingsControls();
            RegisterHotkeys(showStatus: true);
            _storage.SaveSettings(_settings);
            SelectByTag(QuickTargetCombo, _settings.TargetLanguage);
            UpdateActiveProfileText();
            SettingsStatus.Text = "设置已保存";
            FooterStatus.Text = "设置已保存";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = ErrorMessage(ex);
        }
    }

    private async void TestProvider_Click(object? sender, RoutedEventArgs e)
    {
        ApplySettingsControls();
        SettingsStatus.Text = "正在测试...";
        try
        {
            SettingsStatus.Text = await _translator.TestAsync(CreateTranslationSettingsSnapshot());
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = ErrorMessage(ex);
        }
    }

    private void LoadSettingsControls()
    {
        SelectByTag(ProviderCombo, _settings.Provider.ToString());
        SelectByTag(QuickTargetCombo, _settings.TargetLanguage);
        SelectByTag(SettingsTargetCombo, _settings.TargetLanguage);
        SelectByTag(DocumentSourceCombo, _settings.DocumentSourceLanguage);
        SelectByTag(DocumentTargetCombo, _settings.DocumentTargetLanguage);

        _chatProfiles.Clear();
        foreach (var profile in _settings.ChatApiProfiles) _chatProfiles.Add(profile);
        var active = _settings.ActiveChatApiProfile;
        if (active is not null)
        {
            ChatProfileList.SelectedItem = _chatProfiles.FirstOrDefault(item => item.Id == active.Id);
        }
        else if (_chatProfiles.Count > 0)
        {
            ChatProfileList.SelectedIndex = 0;
            _settings.ActiveChatApiProfileId = _chatProfiles[0].Id;
        }

        UpdateActiveProfileText();

        TranslationPromptText.Text = TranslationPrompt.Resolve(_settings.TranslationPrompt);
        DocumentOutputDirectoryText.Text = _settings.DocumentOutputDirectory;
        DocumentOutputSuffixText.Text = _settings.DocumentOutputSuffix;
        DocumentTranslationConcurrencyText.Text = _settings.DocumentTranslationConcurrency.ToString();

        InputHotkeyText.Text = _settings.InputHotkey;
        ScreenshotHotkeyText.Text = _settings.ScreenshotHotkey;
        ScreenshotTranslateHotkeyText.Text = _settings.ScreenshotTranslateHotkey;
        SilentOcrHotkeyText.Text = _settings.SilentOcrHotkey;
        ScreenshotOcrHotkeyText.Text = _settings.ScreenshotOcrHotkey;
        ClipboardHotkeyText.Text = _settings.ClipboardHotkey;
        PinnedImageHotkeyText.Text = _settings.PinnedImageHotkey;
    }

    private void ApplySettingsControls()
    {
        if (!Enum.TryParse<TranslationProvider>(SelectedTag(ProviderCombo, "Auto"), true, out var provider))
            provider = TranslationProvider.Auto;
        _settings.Provider = provider;

        foreach (var profile in _chatProfiles)
        {
            profile.Name = profile.Name.Trim();
            profile.BaseUrl = profile.BaseUrl.Trim();
            profile.ApiKey = profile.ApiKey.Trim();
            profile.Model = profile.Model.Trim();
        }

        _settings.ChatApiProfiles = _chatProfiles.ToList();
        _settings.ActiveChatApiProfileId = (ChatProfileList.SelectedItem as ChatApiProfile)?.Id ?? string.Empty;
        _settings.TranslationPrompt = TranslationPrompt.NormalizeForStorage(TranslationPromptText.Text);
        _settings.TargetLanguage = SelectedTag(SettingsTargetCombo, "auto");

        _settings.InputHotkey = InputHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.ScreenshotHotkey = ScreenshotHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.ScreenshotTranslateHotkey = ScreenshotTranslateHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.SilentOcrHotkey = SilentOcrHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.ScreenshotOcrHotkey = ScreenshotOcrHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.ClipboardHotkey = ClipboardHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.PinnedImageHotkey = PinnedImageHotkeyText.Text?.Trim() ?? string.Empty;
        _settings.DocumentSourceLanguage = SelectedTag(DocumentSourceCombo, "auto");
        _settings.DocumentTargetLanguage = SelectedTag(DocumentTargetCombo, "zh-CN");
        _settings.DocumentOutputDirectory = DocumentOutputDirectoryText.Text?.Trim() ?? string.Empty;
        _settings.DocumentOutputSuffix = DocumentOutputSuffixText.Text?.Trim() ?? "_translation";
        if (!int.TryParse(DocumentTranslationConcurrencyText.Text?.Trim(), out var documentConcurrency)
            || documentConcurrency <= 0)
            throw new ArgumentException("文档并发请求数必须是大于 0 的整数");
        _settings.DocumentTranslationConcurrency = documentConcurrency;
    }

    private AppSettings CreateTranslationSettingsSnapshot() => new()
    {
        Provider = _settings.Provider,
        OllamaEndpoint = _settings.OllamaEndpoint,
        OllamaModel = _settings.OllamaModel,
        SourceLanguage = _settings.SourceLanguage,
        TargetLanguage = _settings.TargetLanguage,
        TranslationPrompt = _settings.TranslationPrompt,
        ChatApiProfiles = _settings.ChatApiProfiles.Select(profile => profile.Clone()).ToList(),
        ActiveChatApiProfileId = _settings.ActiveChatApiProfileId
    };

    private AppSettings CreateDocumentSettingsSnapshot() => new()
    {
        Provider = _settings.Provider,
        OllamaEndpoint = _settings.OllamaEndpoint,
        OllamaModel = _settings.OllamaModel,
        DocumentSourceLanguage = _settings.DocumentSourceLanguage,
        DocumentTargetLanguage = _settings.DocumentTargetLanguage,
        DocumentOutputDirectory = _settings.DocumentOutputDirectory,
        DocumentOutputSuffix = _settings.DocumentOutputSuffix,
        DocumentTranslationConcurrency = _settings.DocumentTranslationConcurrency
    };

    private async void CopyHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(string.IsNullOrWhiteSpace(entry.Translation) ? entry.Source : entry.Translation);
    }

    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        _history.Clear();
        try
        {
            _storage.SaveHistory(_history);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"历史清空后保存失败：{ex.Message}";
        }
    }

    private void InitializeProviderCombo()
    {
        ProviderCombo.Items.Clear();
        foreach (var provider in TranslationProviderCatalog.SelectableProviders)
        {
            ProviderCombo.Items.Add(new ComboBoxItem
            {
                Content = TranslationProviderCatalog.DisplayName(provider),
                Tag = provider.ToString()
            });
        }

        ProviderCombo.SelectedIndex = 0;
    }

    private static readonly (string Tag, string Label)[] Languages =
    [
        ("auto", "自动检测"),
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("en", "English"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("ru", "Русский")
    ];

    private void InitializeLanguageCombos()
    {
        FillLanguageCombo(QuickTargetCombo, includeAuto: false);
        FillLanguageCombo(SettingsTargetCombo, includeAuto: false);
        FillLanguageCombo(DocumentSourceCombo, includeAuto: true);
        FillLanguageCombo(DocumentTargetCombo, includeAuto: false);
    }

    private static void FillLanguageCombo(ComboBox combo, bool includeAuto)
    {
        combo.Items.Clear();
        foreach (var (tag, label) in Languages)
        {
            if (!includeAuto && tag == "auto") continue;
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        combo.SelectedIndex = 0;
    }

    private void UpdateActiveProfileText()
    {
        var active = _chatProfiles.FirstOrDefault(profile => profile.Id == _settings.ActiveChatApiProfileId);
        ActiveProfileText.Text = active is null
            ? "当前渠道：（未选择）"
            : $"当前渠道：{TranslationProviderCatalog.ProfileLabel(active)}";
    }

    private void CommitProfileFields()
    {
        if (ChatProfileList.SelectedItem is not ChatApiProfile profile) return;
        profile.Name = ChatProfileNameText.Text?.Trim() ?? string.Empty;
        profile.BaseUrl = ChatProfileBaseUrlText.Text?.Trim() ?? string.Empty;
        profile.ApiKey = ChatProfileApiKeyText.Text?.Trim() ?? string.Empty;
        profile.Model = ChatProfileModelText.Text?.Trim() ?? string.Empty;
        if (Enum.TryParse<ChatApiFormat>(SelectedTag(ChatProfileFormatCombo, "OpenAi"), true, out var format))
            profile.Format = format;
        ChatProfileList.ItemsSource = null;
        ChatProfileList.ItemsSource = _chatProfiles;
        ChatProfileList.SelectedItem = profile;
    }

    private void NewChatProfile_Click(object? sender, RoutedEventArgs e)
    {
        CommitProfileFields();
        var index = 0;
        string name;
        do
        {
            index++;
            name = $"渠道 {index}";
        }
        while (_chatProfiles.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal)));

        var profile = new ChatApiProfile { Name = name };
        _chatProfiles.Add(profile);
        ChatProfileList.SelectedItem = profile;
        _settings.ActiveChatApiProfileId = profile.Id;
        UpdateActiveProfileText();
    }

    private void DeleteChatProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (ChatProfileList.SelectedItem is not ChatApiProfile profile) return;
        _chatProfiles.Remove(profile);
        if (_chatProfiles.Count > 0) ChatProfileList.SelectedIndex = 0;
        else ClearProfileFields();
    }

    private void ActivateChatProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (ChatProfileList.SelectedItem is not ChatApiProfile profile) return;
        _settings.ActiveChatApiProfileId = profile.Id;
        UpdateActiveProfileText();
        SettingsStatus.Text = $"已把「{profile.Name}」设为当前渠道（保存后生效）";
    }

    private void ChatProfileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ChatProfileList.SelectedItem is ChatApiProfile profile)
        {
            ChatProfileNameText.Text = profile.Name;
            ChatProfileBaseUrlText.Text = profile.BaseUrl;
            ChatProfileApiKeyText.Text = profile.ApiKey;
            ChatProfileModelText.Text = profile.Model;
            SelectByTag(ChatProfileFormatCombo, profile.Format.ToString());
        }
        else
        {
            ClearProfileFields();
        }
    }

    private void ClearProfileFields()
    {
        ChatProfileNameText.Text = string.Empty;
        ChatProfileBaseUrlText.Text = string.Empty;
        ChatProfileApiKeyText.Text = string.Empty;
        ChatProfileModelText.Text = string.Empty;
        SelectByTag(ChatProfileFormatCombo, ChatApiFormat.OpenAi.ToString());
    }

    private void ResetPrompt_Click(object? sender, RoutedEventArgs e)
    {
        TranslationPromptText.Text = TranslationPrompt.Default;
        SettingsStatus.Text = "已恢复默认提示词，点「保存设置」生效";
    }

    private bool RegisterHotkeys(bool showStatus)
    {
        var hotkeys = new[]
        {
            new GlobalHotkey("input", "输入翻译", _settings.InputHotkey),
            new GlobalHotkey("screenshot", "截图", _settings.ScreenshotHotkey),
            new GlobalHotkey("screenshot-translate", "截图翻译", _settings.ScreenshotTranslateHotkey),
            new GlobalHotkey("silent-ocr", "静默 OCR", _settings.SilentOcrHotkey),
            new GlobalHotkey("screenshot-ocr", "截图 OCR", _settings.ScreenshotOcrHotkey),
            new GlobalHotkey("clipboard", "剪贴板历史", _settings.ClipboardHotkey),
            new GlobalHotkey("pinned", "贴图", _settings.PinnedImageHotkey)
        };

        var ok = PlatformServices.Hotkeys.TryRegister(hotkeys, out var error);
        if (showStatus || !ok)
        {
            HotkeyStatus.Text = ok
                ? $"已注册 {PlatformServices.Hotkeys.Active.Count} 个全局快捷键"
                : string.IsNullOrWhiteSpace(error) ? "当前平台不支持全局快捷键" : error;
        }

        return ok;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectByTag(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)) continue;
            combo.SelectedItem = item;
            return;
        }
        combo.SelectedIndex = 0;
    }

    private static string ErrorMessage(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join("；", aggregate.InnerExceptions.Select(item => item.Message))
            : exception.GetBaseException().Message;

    private bool _allowRealClose;

    /// <summary>托盘退出时调用，允许窗口真正关闭。</summary>
    public void AllowRealClose() => _allowRealClose = true;

    /// <summary>托盘点击或快捷键唤起主窗口。</summary>
    public void ShowFromBackground()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>打开（或唤起）剪贴板历史窗口。</summary>
    public void ShowClipboardHistory()
    {
        if (_clipboardWindow is null)
        {
            _clipboardWindow = new ClipboardWindow(_clipboardRepository, _clipboardMonitor);
            _clipboardWindow.Closed += (_, _) => _clipboardWindow = null;
            _clipboardWindow.Show();
        }
        else if (!_clipboardWindow.IsVisible)
        {
            _clipboardWindow.Show();
        }

        _clipboardWindow?.Activate();
    }

    private void Window_ClosingHandler(object? sender, WindowClosingEventArgs e)
    {
        if (_allowRealClose) return;
        // 只在有桌面生命周期（真实运行）时把关闭改成隐藏；无头测试里直接关闭。
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) return;
        e.Cancel = true;
        Hide();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        foreach (var task in _documentTasks)
        {
            if (task.CanCancel) task.Cancellation.Cancel();
            else task.Cancellation.Dispose();
        }

        _clipboardMonitor?.Dispose();
        _clipboardMonitor = null;
        _clipboardRepository.Dispose();
    }
}
