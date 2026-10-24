using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace SnapTranslate.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsService _storage;
    private readonly TranslationService _translator = new();
    private readonly DocumentTranslationService _documentTranslator;
    private readonly MacCaptureOcrService _captureOcr = new();
    private readonly ObservableCollection<HistoryEntry> _history;
    private readonly ObservableCollection<DocumentTranslationTask> _documentTasks = [];
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
        LoadSettingsControls();
        UpdateDocumentTaskCount();
        FooterStatus.Text = _storage.LastLoadWarning ?? "就绪";
        Closed += Window_Closed;
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

    private async void ScreenshotTranslate_Click(object? sender, RoutedEventArgs e) =>
        await RunScreenshotAsync(true);

    private async void ScreenshotOcr_Click(object? sender, RoutedEventArgs e) =>
        await RunScreenshotAsync(false);

    private async Task RunScreenshotAsync(bool translate)
    {
        ScreenshotStatus.Text = "请选择截图区域...";
        FooterStatus.Text = "正在截图";
        try
        {
            var text = await _captureOcr.CaptureAndRecognizeAsync();
            if (text is null)
            {
                ScreenshotStatus.Text = "已取消截图";
                FooterStatus.Text = "就绪";
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("截图中没有识别到文本。");

            InputText.Text = text;
            MainTabs.SelectedIndex = 0;
            ScreenshotStatus.Text = $"已识别 {text.Length:N0} 个字符";
            FooterStatus.Text = "截图 OCR 完成";
            if (translate) await TranslateInputAsync();
        }
        catch (Exception ex)
        {
            ScreenshotStatus.Text = ErrorMessage(ex);
            FooterStatus.Text = "截图 OCR 失败";
        }
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
            ApplySettingsControls();
            _storage.SaveSettings(_settings);
            SelectByTag(QuickTargetCombo, _settings.TargetLanguage);
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
        OllamaEndpointText.Text = _settings.OllamaEndpoint;
        OllamaModelText.Text = _settings.OllamaModel;
        DocumentOutputDirectoryText.Text = _settings.DocumentOutputDirectory;
        DocumentOutputSuffixText.Text = _settings.DocumentOutputSuffix;
        DocumentTranslationConcurrencyText.Text = _settings.DocumentTranslationConcurrency.ToString();
    }

    private void ApplySettingsControls()
    {
        if (!Enum.TryParse<TranslationProvider>(SelectedTag(ProviderCombo, "Auto"), true, out var provider))
            provider = TranslationProvider.Auto;
        _settings.Provider = provider;
        _settings.OllamaEndpoint = OllamaEndpointText.Text?.Trim() ?? string.Empty;
        _settings.OllamaModel = OllamaModelText.Text?.Trim() ?? string.Empty;
        _settings.TargetLanguage = SelectedTag(SettingsTargetCombo, "auto");
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
        TargetLanguage = _settings.TargetLanguage
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

    private void Window_Closed(object? sender, EventArgs e)
    {
        foreach (var task in _documentTasks)
        {
            if (task.CanCancel) task.Cancellation.Cancel();
            else task.Cancellation.Dispose();
        }
    }
}
