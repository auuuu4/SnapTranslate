using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SnapTranslate.Desktop.CaptureTranslation;

/// <summary>
/// 截图翻译结果窗口：上方按可用宽度等比显示截图，译文以半透明面板覆盖在图片上。
/// 打开后自动翻译一次，支持原文 / 译文 / 对照三种显示模式，可切换语言重新翻译、复制原文或译文。
/// </summary>
public partial class ScreenshotTranslationWindow : Window
{
    /// <summary>排版计算时使用的图片显示宽度上限。</summary>
    private const double MaxDisplayWidth = 960;

    private static readonly (string Label, string Code)[] SourceLanguages =
    [
        ("自动检测", "auto"),
        ("简体中文", "zh"),
        ("English", "en"),
        ("日本語", "ja"),
        ("한국어", "ko"),
        ("Français", "fr"),
        ("Deutsch", "de"),
        ("Español", "es"),
        ("Русский", "ru")
    ];

    private static readonly (string Label, string Code)[] TargetLanguages =
    [
        ("简体中文", "zh-CN"),
        ("English", "en"),
        ("日本語", "ja"),
        ("한국어", "ko"),
        ("Français", "fr"),
        ("Deutsch", "de"),
        ("Español", "es"),
        ("Русский", "ru")
    ];

    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#697078"));
    private static readonly IBrush InkTextBrush = new SolidColorBrush(Color.Parse("#202428"));

    private readonly Bitmap _image;
    private readonly string[] _sourceLines;
    private readonly AppSettings _settings;
    private readonly TranslationService _translator;
    private CancellationTokenSource? _translationCancellation;
    private TranslationOverlayLayout? _layout;
    private string _translatedText = string.Empty;
    private string _provider = string.Empty;
    private DisplayMode _displayMode = DisplayMode.Compare;
    private bool _ready;

    /// <summary>翻译成功完成时触发，主窗口用它把原文 / 译文 / 通道写入历史。</summary>
    public event EventHandler<TranslationResultEventArgs>? TranslationCompleted;

    /// <summary>截图在屏幕中的像素范围（Retina 下是物理像素）。</summary>
    public PixelRect Region { get; }

    /// <summary>本次要翻译的原文。</summary>
    public string SourceText { get; }

    /// <summary>最近一次翻译的译文；还没翻译完成时为空。</summary>
    public string TranslatedText => _translatedText;

    /// <summary>最近一次翻译使用的通道名称；还没翻译完成时为空。</summary>
    public string Provider => _provider;

    /// <summary>
    /// 无参构造函数：XAML 运行时加载器与设计器需要它，正常业务请使用带参数的构造函数。
    /// 没有截图和原文时窗口只会显示一条「没有可翻译的文本」提示。
    /// </summary>
    public ScreenshotTranslationWindow()
        : this(CreatePlaceholderImage(), default, string.Empty, new AppSettings(), new TranslationService())
    {
    }

    /// <summary>构造结果窗口，窗口第一次显示时会自动翻译一次。</summary>
    public ScreenshotTranslationWindow(
        Bitmap image,
        PixelRect region,
        string sourceText,
        AppSettings settings,
        TranslationService translator)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(translator);

        InitializeComponent();

        _image = image;
        _settings = settings.Clone();
        _translator = translator;
        Region = region;
        SourceText = string.IsNullOrWhiteSpace(sourceText) ? string.Empty : sourceText.Trim();
        _sourceLines = ScreenshotTranslationLayout.SplitSourceLines(SourceText);

        SourceImage.Source = image;
        PopulateLanguageCombos();
        ApplyLayout();
        ApplyDisplayMode();
        UpdateSubtitle();

        SourceLanguageCombo.SelectionChanged += SourceLanguageCombo_SelectionChanged;
        TargetLanguageCombo.SelectionChanged += TargetLanguageCombo_SelectionChanged;
        KeyDown += Window_KeyDown;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    /// <summary>按当前语言设置重新翻译一次。</summary>
    public Task RetranslateAsync() => TranslateAsync();

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        await TranslateAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        var cancellation = _translationCancellation;
        _translationCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private async Task TranslateAsync()
    {
        if (SourceText.Length == 0)
        {
            ShowError(new InvalidOperationException("没有可翻译的文本。"));
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = _translationCancellation;
        _translationCancellation = cancellation;
        previous?.Cancel();
        previous?.Dispose();
        var token = cancellation.Token;

        var sourceCode = SelectedTag(SourceLanguageCombo, "auto");
        var targetCode = SelectedTag(TargetLanguageCombo, "zh-CN");
        SetBusy(true, "正在翻译…");
        try
        {
            var requestSettings = _settings.Clone();
            requestSettings.SourceLanguage = sourceCode;
            requestSettings.TargetLanguage = targetCode;
            var progress = new Progress<string>(partial =>
            {
                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(partial)) return;
                BusyText.Text = $"正在翻译… 已返回 {partial.Length:N0} 字";
            });

            var result = await _translator.TranslateAsync(SourceText, requestSettings, progress, token);
            token.ThrowIfCancellationRequested();
            _translatedText = result.Text;
            _provider = result.Provider;

            ApplyTranslationResult();
            SetBusy(false, string.Empty);
            StatusText.Text =
                $"{_sourceLines.Length} 行 · {FormatSourceStatus(sourceCode, result.DetectedLanguage)} → " +
                $"{LanguageLabel(targetCode)} · {_provider}";
            UpdateSubtitle();
            TranslationCompleted?.Invoke(this, new TranslationResultEventArgs(SourceText, _translatedText, _provider));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 关闭窗口或切换语言会取消上一次翻译，这里不算错误。
        }
        catch (Exception exception)
        {
            SetBusy(false, string.Empty);
            ShowError(exception);
        }
    }

    private void ApplyTranslationResult()
    {
        ApplyLayout();
        TranslationText.Text = _translatedText;
        RenderCompareLines();
        ApplyDisplayMode();
    }

    private void ApplyLayout()
    {
        var pixelSize = _image.PixelSize;
        var displayWidth = Math.Min(pixelSize.Width, MaxDisplayWidth);
        var displayHeight = Math.Max(
            1,
            (int)Math.Round(pixelSize.Height * displayWidth / Math.Max(1, pixelSize.Width)));
        var estimatedLength = _translatedText.Length > 0 ? _translatedText.Length : SourceText.Length;

        // 对照模式每行原文后面跟一行译文，按两倍行数估算；译文模式整段译文自己换行。
        var renderedLines = _displayMode switch
        {
            DisplayMode.Compare => Math.Max(1, _sourceLines.Length * 2),
            DisplayMode.Translation => 1,
            _ => Math.Max(1, _sourceLines.Length)
        };
        _layout = ScreenshotTranslationLayout.Compute(
            (int)displayWidth,
            displayHeight,
            renderedLines,
            estimatedLength);

        // 覆盖层标题和内边距的额外高度不在纯排版结果里，窗口这边补上，避免文字被裁掉。
        var titleHeight = Math.Max(10, Math.Round(_layout.FontSize * 0.7)) * 1.7 + 10;
        TranslationOverlay.Padding = new Thickness(_layout.PanelMargin);
        TranslationOverlay.MaxHeight = _layout.PanelHeight + titleHeight;
        TranslationOverlay.HorizontalAlignment = _layout.BottomPanel
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Center;
        TranslationOverlay.MaxWidth = _layout.BottomPanel ? double.PositiveInfinity : _layout.PanelWidth;
        TranslationText.FontSize = _layout.FontSize;
        TranslationText.LineHeight = _layout.LineHeight;
        OverlayTitle.FontSize = Math.Max(10, Math.Round(_layout.FontSize * 0.7));
    }

    private void RenderCompareLines()
    {
        ComparePanel.Children.Clear();
        if (_layout is not { } layout) return;
        var translatedLines = TranslationLineMapper.Split(_sourceLines, _translatedText);
        for (var index = 0; index < _sourceLines.Length; index++)
        {
            var sourceLine = _sourceLines[index].Trim();
            if (sourceLine.Length > 0)
                ComparePanel.Children.Add(CreateCompareLine(sourceLine, layout.FontSize * 0.82, MutedTextBrush));
            if (index >= translatedLines.Count) continue;
            var translatedLine = translatedLines[index];
            if (translatedLine.Length == 0) continue;
            ComparePanel.Children.Add(CreateCompareLine(translatedLine, layout.FontSize, InkTextBrush));
        }
    }

    private static TextBlock CreateCompareLine(string text, double fontSize, IBrush foreground) => new()
    {
        Text = text,
        FontSize = Math.Max(4, fontSize),
        LineHeight = Math.Max(6, fontSize * 1.35),
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap
    };

    private void ApplyDisplayMode()
    {
        var original = _displayMode == DisplayMode.Original;
        var translation = _displayMode == DisplayMode.Translation;
        var compare = _displayMode == DisplayMode.Compare;

        // 原文模式只显示截图；译文模式只显示译文；对照模式图片与覆盖层同时显示。
        SourceImage.IsVisible = !translation;
        TranslationOverlay.IsVisible = !original;
        ComparePanel.IsVisible = compare;
        TranslationText.IsVisible = !compare;
        OverlayTitle.Text = compare ? "原文 / 译文" : "译文";
        TranslationOverlay.VerticalAlignment = translation ? VerticalAlignment.Center : VerticalAlignment.Bottom;

        OriginalModeButton.Classes.Set("active", original);
        TranslationModeButton.Classes.Set("active", translation);
        CompareModeButton.Classes.Set("active", compare);
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        _displayMode = mode;
        ApplyLayout();
        ApplyDisplayMode();
    }

    private void SetBusy(bool busy, string message)
    {
        BusyPanel.IsVisible = busy;
        if (!string.IsNullOrWhiteSpace(message)) BusyText.Text = message;
        if (busy) ErrorPanel.IsVisible = false;
    }

    private void ShowError(Exception exception)
    {
        var message = ToReadableMessage(exception);
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
        StatusText.Text = message;
    }

    private static string ToReadableMessage(Exception exception)
    {
        var message = exception switch
        {
            AggregateException aggregate => string.Join(
                "；",
                aggregate.Flatten().InnerExceptions.Select(item => item.GetBaseException().Message)),
            _ => exception.GetBaseException().Message
        };
        return string.IsNullOrWhiteSpace(message) ? "翻译失败，请稍后重试。" : message;
    }

    private async Task CopyTextAsync(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "没有可复制的内容。";
            return;
        }

        try
        {
            var clipboard = Clipboard;
            if (clipboard is null)
            {
                StatusText.Text = "当前平台没有可用的剪贴板。";
                return;
            }

            await clipboard.SetTextAsync(text);
            StatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制失败：{exception.GetBaseException().Message}";
        }
    }

    private void PopulateLanguageCombos()
    {
        foreach (var language in SourceLanguages)
            SourceLanguageCombo.Items.Add(new ComboBoxItem { Content = language.Label, Tag = language.Code });
        foreach (var language in TargetLanguages)
            TargetLanguageCombo.Items.Add(new ComboBoxItem { Content = language.Label, Tag = language.Code });
        SelectByTag(SourceLanguageCombo, NormalizeSourceTag(_settings.SourceLanguage));
        SelectByTag(TargetLanguageCombo, NormalizeTargetTag(_settings.TargetLanguage));
    }

    private void UpdateSubtitle()
    {
        var parts = new List<string>
        {
            $"选区 {Math.Max(0, Region.Width)}×{Math.Max(0, Region.Height)}",
            $"原文 {_sourceLines.Length} 行 · {SourceText.Length:N0} 字"
        };
        if (_translatedText.Length > 0) parts.Add($"译文 {_translatedText.Length:N0} 字");
        SubtitleText.Text = string.Join(" · ", parts);
    }

    private async void SourceLanguageCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        await TranslateAsync();
    }

    private async void TargetLanguageCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        await TranslateAsync();
    }

    private void OriginalMode_Click(object? sender, RoutedEventArgs e) => SetDisplayMode(DisplayMode.Original);

    private void TranslationMode_Click(object? sender, RoutedEventArgs e) => SetDisplayMode(DisplayMode.Translation);

    private void CompareMode_Click(object? sender, RoutedEventArgs e) => SetDisplayMode(DisplayMode.Compare);

    private async void CopySource_Click(object? sender, RoutedEventArgs e) =>
        await CopyTextAsync(SourceText, "已复制原文");

    private async void CopyTranslation_Click(object? sender, RoutedEventArgs e) =>
        await CopyTextAsync(_translatedText, "已复制译文");

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await TranslateAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) continue;
            combo.SelectedItem = item;
            return;
        }

        combo.SelectedIndex = 0;
    }

    private static string NormalizeSourceTag(string? value)
    {
        var code = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        if (code == "auto") return "auto";
        if (code.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        return code switch
        {
            "jp" => "ja",
            "kr" => "ko",
            _ => code
        };
    }

    private static string NormalizeTargetTag(string? value)
    {
        var code = string.IsNullOrWhiteSpace(value) ? "zh-CN" : value.Trim();
        if (code.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        return code.ToLowerInvariant() switch
        {
            "jp" => "ja",
            "kr" => "ko",
            var other => other
        };
    }

    private static string LanguageLabel(string code)
    {
        foreach (var language in SourceLanguages)
        {
            if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))
                return language.Label;
        }

        foreach (var language in TargetLanguages)
        {
            if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))
                return language.Label;
        }

        return code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "简体中文" : code;
    }

    private static string FormatSourceStatus(string selected, string detected) =>
        string.Equals(selected, "auto", StringComparison.OrdinalIgnoreCase)
            ? $"自动({(string.IsNullOrWhiteSpace(detected) ? "未知" : detected)})"
            : LanguageLabel(selected);

    private static Bitmap CreatePlaceholderImage()
    {
        // 1×1 透明 PNG，只用于满足 XAML 运行时加载器对无参构造函数的要求。
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        return new Bitmap(new MemoryStream(png));
    }

    private enum DisplayMode
    {
        Original,
        Translation,
        Compare
    }
}
