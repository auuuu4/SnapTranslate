using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Forms = System.Windows.Forms;

namespace SnapTranslate;

public partial class ScreenshotTranslationWindow : Window
{
    private const int HwndTopmost = -1;
    private const uint SwpShowWindow = 0x0040;
    private BitmapSource _image;
    private System.Drawing.Rectangle _screenBounds;
    private readonly OcrService _ocr;
    private readonly ITranslationService _translator;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _translationCancellation;
    private OcrLayoutResult? _layout;
    private string[] _translatedLines = Array.Empty<string>();
    private string _translatedText = string.Empty;
    private string _provider = string.Empty;
    private DisplayMode _displayMode = DisplayMode.Translation;
    private bool _ready;
    private bool _previewMode;
    private bool _changingLanguages;
    private bool _dragging;
    private bool _resizing;
    private bool _refreshingSelection;
    private System.Drawing.Rectangle _resizeStartBounds;
    private Vector _resizeDeltaPixels;
    private double _contentWidth;
    private double _contentHeight;

    public event Action<string, string, string>? TranslationCompleted;

    public ScreenshotTranslationWindow(
        BitmapSource image,
        System.Drawing.Rectangle screenBounds,
        OcrService ocr,
        ITranslationService translator,
        AppSettings settings)
    {
        InitializeComponent();
        _image = image;
        _screenBounds = screenBounds;
        _ocr = ocr;
        _translator = translator;
        _settings = settings;
        SourceImage.Source = image;
        SetLanguageSelections(settings.SourceLanguage, ResolveDefaultTarget(settings.TargetLanguage));

        Loaded += async (_, _) =>
        {
            ConfigurePlacement();
            SetMode(DisplayMode.Translation);
            if (_previewMode)
            {
                SetBusy(false, string.Empty);
                StatusText.Text = $"{_layout?.Lines.Count ?? 0} 行 · {_provider}";
                RenderTextOverlay();
            }
            else
            {
                await RecognizeAndTranslateAsync(true);
            }
        };
        Closed += (_, _) =>
        {
            _translationCancellation?.Cancel();
            _translationCancellation?.Dispose();
        };
    }

    internal ScreenshotTranslationWindow(
        BitmapSource image,
        System.Drawing.Rectangle screenBounds,
        OcrLayoutResult layout,
        IReadOnlyList<string> translatedLines)
        : this(image, screenBounds, new OcrService(), new TranslationService(), new AppSettings())
    {
        _layout = layout;
        _translatedLines = translatedLines.ToArray();
        _translatedText = string.Join(Environment.NewLine, _translatedLines);
        _provider = "有道 AI";
        _ready = true;
        _previewMode = true;
    }

    private void ConfigurePlacement()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = Math.Max(1, dpi.DpiScaleX);
        var scaleY = Math.Max(1, dpi.DpiScaleY);
        var work = Forms.Screen.FromRectangle(_screenBounds).WorkingArea;
        var compactToolbar = _screenBounds.Width < 760 * scaleX;
        ConfigureToolbarLayout(compactToolbar);

        // 宽屏（非紧凑）时先量出「一行放下所有按钮」需要多宽，工具条至少给到这个宽度，
        // 否则包裹面板会把按钮折到第二行，再被固定高度裁掉（关闭按钮以前就是这么消失的）。
        // 紧凑模式本来就是两行小工具条，不去拉宽。
        var contentWidth = 0d;
        if (!compactToolbar)
        {
            TranslationToolbarLayout.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (double.IsFinite(TranslationToolbarLayout.DesiredSize.Width))
                contentWidth = TranslationToolbarLayout.DesiredSize.Width + 16;
        }

        var minimumToolbarWidth = compactToolbar ? 340d : 860d;
        var toolbarWidthPixels = Math.Min(
            work.Width,
            (int)Math.Ceiling(Math.Max(
                Math.Max(_screenBounds.Width, minimumToolbarWidth * scaleX),
                contentWidth * scaleX)));

        // 高度按实际排版来：一行就是一行的高度，屏幕窄到折行时才跟着长高，
        // 这样既不会裁掉按钮，也不会在小选区上留一大块空白。
        var toolbarHeightDips = compactToolbar ? 96d : 58d;
        TranslationToolbarLayout.Measure(new System.Windows.Size(
            Math.Max(160d, toolbarWidthPixels / scaleX - 16),
            double.PositiveInfinity));
        if (double.IsFinite(TranslationToolbarLayout.DesiredSize.Height))
        {
            toolbarHeightDips = Math.Max(56d, Math.Ceiling(TranslationToolbarLayout.DesiredSize.Height) + 16);
        }

        var toolbarHeight = toolbarHeightDips;
        var toolbarHeightPixels = (int)Math.Ceiling(toolbarHeight * scaleY);
        var windowLeft = Math.Clamp(_screenBounds.Left, work.Left, Math.Max(work.Left, work.Right - toolbarWidthPixels));
        var contentOffsetPixels = Math.Max(0, _screenBounds.Left - windowLeft);
        var toolbarAbove = _screenBounds.Bottom + toolbarHeightPixels > work.Bottom
                           && _screenBounds.Top - toolbarHeightPixels >= work.Top;
        var windowTop = toolbarAbove ? _screenBounds.Top - toolbarHeightPixels : _screenBounds.Top;
        var windowHeightPixels = _screenBounds.Height + toolbarHeightPixels;

        _contentWidth = Math.Max(1, _screenBounds.Width / scaleX);
        _contentHeight = Math.Max(1, _screenBounds.Height / scaleY);
        var windowWidth = toolbarWidthPixels / scaleX;
        var windowHeight = windowHeightPixels / scaleY;

        Width = windowWidth;
        Height = windowHeight;
        RootGrid.Width = windowWidth;
        RootGrid.Height = windowHeight;
        CaptureHost.Width = _contentWidth;
        CaptureHost.Height = _contentHeight;
        CaptureHost.Margin = new Thickness(contentOffsetPixels / scaleX, 0, 0, 0);
        CaptureFrame.Width = _contentWidth;
        CaptureFrame.Height = _contentHeight;
        CaptureSurface.Width = _contentWidth;
        CaptureSurface.Height = _contentHeight;
        TextOverlay.Width = _contentWidth;
        TextOverlay.Height = _contentHeight;
        TranslationToolbar.Width = windowWidth;
        TranslationToolbar.Height = toolbarHeight;

        Grid.SetRow(TranslationToolbar, toolbarAbove ? 0 : 1);
        Grid.SetRow(CaptureHost, toolbarAbove ? 1 : 0);

        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, new IntPtr(HwndTopmost), windowLeft, windowTop,
            toolbarWidthPixels, windowHeightPixels, SwpShowWindow);
    }

    private void ConfigureToolbarLayout(bool compact)
    {
        SourceLanguageCombo.Width = compact ? 112 : 132;
        TargetLanguageCombo.Width = compact ? 112 : 132;
        StatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LanguageGroup.Width = compact ? 276 : double.NaN;
        ActionGroup.Width = compact ? 306 : double.NaN;
        ActionGroup.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        OriginalModeButton.Width = compact ? 40 : 50;
        TranslationModeButton.Width = compact ? 40 : 50;
        CompareModeButton.Width = compact ? 40 : 50;
        CopySourceButton.Width = compact ? 52 : 70;
        CopyTranslationButton.Width = compact ? 52 : 70;
        CopySourceButton.Content = compact ? "原文" : "复制原文";
        CopyTranslationButton.Content = compact ? "译文" : "复制译文";
        ModeSelector.Margin = compact ? new Thickness(2, 0, 4, 0) : new Thickness(8, 0, 8, 0);
    }

    private async Task RecognizeAndTranslateAsync(bool initializeLanguages)
    {
        SetBusy(true, "正在识别...");
        try
        {
            _layout = await _ocr.RecognizeLayoutAsync(
                _image,
                SelectedTag(SourceLanguageCombo, "auto"));
            if (string.IsNullOrWhiteSpace(_layout.Text) || _layout.Lines.Count == 0)
                throw new InvalidOperationException("选区中没有识别到文本。");

            if (initializeLanguages)
                SetLanguageSelections(_settings.SourceLanguage, ResolveDefaultTarget(_settings.TargetLanguage));

            _ready = true;
            await TranslateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(RecognizeAndTranslateAsync), ex);
            SetBusy(false, ex.Message);
            StatusText.Text = ex.Message;
        }
    }

    private async Task TranslateAsync()
    {
        if (_layout is null || !_ready) return;
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        var cancellationToken = _translationCancellation.Token;
        SetBusy(true, "正在翻译...");
        StatusText.Text = "正在翻译...";

        try
        {
            var source = SelectedTag(SourceLanguageCombo, "auto");
            var target = SelectedTag(TargetLanguageCombo, "zh-CN");
            var translationSettings = CreateTranslationSettings(source, target);
            var result = await TranslateLayoutAsync(
                _translator,
                _layout,
                translationSettings,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _translatedLines = result.Lines;
            _translatedText = string.Join(Environment.NewLine, result.Lines);
            _provider = result.Provider;
            RenderTextOverlay();
            SetBusy(false, string.Empty);
            StatusText.Text = $"{_layout.Lines.Count} 行 · {FormatSourceStatus(source, _layout.DetectedLanguage)} → {target} · {_provider}";
            TranslationCompleted?.Invoke(_layout.Text, _translatedText, _provider);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(TranslateAsync), ex);
            SetBusy(false, ex.Message);
            StatusText.Text = ex.Message;
        }
    }

    internal static async Task<(string[] Lines, string Provider)> TranslateLayoutAsync(
        ITranslationService translator,
        OcrLayoutResult layout,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);
        if (layout.Lines.Count == 0 || string.IsNullOrWhiteSpace(layout.Text))
            throw new ArgumentException("OCR 结果中没有可翻译文本。", nameof(layout));

        // A single request preserves the context that is lost when each OCR line is translated independently.
        var result = await translator.TranslateAsync(layout.Text, settings, cancellationToken);
        var lines = MapTranslationToLines(layout.Lines, result.Text);
        return (lines, string.IsNullOrWhiteSpace(result.Provider) ? "翻译" : result.Provider);
    }

    internal static string[] MapTranslationToLines(
        IReadOnlyList<OcrTextLine> sourceLines,
        string translatedText)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        if (sourceLines.Count == 0) return Array.Empty<string>();

        var normalized = (translatedText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
            throw new InvalidOperationException("翻译服务返回了空结果。");

        var returnedLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (returnedLines.Length == sourceLines.Count)
            return returnedLines;
        if (sourceLines.Count == 1)
            return [CleanOverlayText(normalized)];

        var sourceWeights = sourceLines
            .Select(line => Math.Max(1, line.Text.Count(character => !char.IsWhiteSpace(character))))
            .ToArray();
        var totalWeight = sourceWeights.Sum();
        var boundaries = new int[sourceLines.Count + 1];
        boundaries[^1] = normalized.Length;
        var consumedWeight = 0;

        for (var index = 1; index < sourceLines.Count; index++)
        {
            consumedWeight += sourceWeights[index - 1];
            var ideal = (int)Math.Round(normalized.Length * consumedWeight / (double)totalWeight);
            var minimum = Math.Min(normalized.Length, boundaries[index - 1] + 1);
            var maximum = Math.Max(minimum, normalized.Length - (sourceLines.Count - index));
            boundaries[index] = FindNaturalBoundary(normalized, ideal, minimum, maximum);
        }

        var mapped = new string[sourceLines.Count];
        for (var index = 0; index < mapped.Length; index++)
        {
            var start = Math.Min(boundaries[index], normalized.Length);
            var end = Math.Clamp(boundaries[index + 1], start, normalized.Length);
            mapped[index] = CleanOverlayText(normalized[start..end]);
        }

        // Extremely short provider output cannot be split into non-empty regions. Keep it visible
        // in the nearest region instead of silently dropping translated content.
        for (var index = 0; index < mapped.Length; index++)
        {
            if (mapped[index].Length > 0) continue;
            var donor = Array.FindIndex(mapped, item => item.Length > 1);
            if (donor < 0)
            {
                mapped[index] = normalized;
                continue;
            }
            var split = Math.Max(1, mapped[donor].Length / 2);
            mapped[index] = mapped[donor][split..].Trim();
            mapped[donor] = mapped[donor][..split].Trim();
        }
        return mapped;
    }

    private static int FindNaturalBoundary(string text, int ideal, int minimum, int maximum)
    {
        ideal = Math.Clamp(ideal, minimum, maximum);
        var best = -1;
        var bestScore = int.MaxValue;
        for (var position = minimum; position <= maximum && position < text.Length; position++)
        {
            if (position > 0 && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]))
                continue;
            var previous = text[position - 1];
            var priority = previous == '\n'
                ? 0
                : IsSentenceBoundary(previous)
                    ? 1
                    : char.IsWhiteSpace(previous) ? 2 : -1;
            if (priority < 0) continue;
            var score = Math.Abs(position - ideal) * 4 + priority * 3;
            if (score >= bestScore) continue;
            best = position;
            bestScore = score;
        }
        return best >= 0 ? best : ideal;
    }

    private static bool IsSentenceBoundary(char character)
        => character is '.' or '!' or '?' or ';' or ':' or ','
            or '。' or '！' or '？' or '；' or '：' or '，' or '、';

    private static string CleanOverlayText(string value)
        => string.Join(" ", value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();

    private AppSettings CreateTranslationSettings(string source, string target)
    {
        var settings = _settings.Clone();
        settings.SourceLanguage = source;
        settings.TargetLanguage = target;
        return settings;
    }

    private void RenderTextOverlay()
    {
        TextOverlay.Children.Clear();
        if (_layout is null || _displayMode == DisplayMode.Original) return;
        var scaleX = _contentWidth / Math.Max(1, _image.PixelWidth);
        var scaleY = _contentHeight / Math.Max(1, _image.PixelHeight);
        var bounds = CalculateOverlayBounds(_layout.Lines, scaleX, scaleY, _contentWidth, _contentHeight);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var index = 0; index < _layout.Lines.Count; index++)
        {
            var line = _layout.Lines[index];
            var translated = index < _translatedLines.Length ? _translatedLines[index] : string.Empty;
            if (string.IsNullOrWhiteSpace(translated)) continue;
            var box = bounds[index];
            if (box.IsEmpty || box.Width < 1 || box.Height < 1) continue;
            var rows = _displayMode == DisplayMode.Compare
                ? new[] { line.Text, translated }
                : new[] { translated };
            var contentWidth = Math.Max(1, box.Width - 4);
            var contentHeight = Math.Max(1, box.Height - 1);
            var rowHeight = contentHeight / rows.Length;
            var fontSize = FitFontSize(rows, contentWidth, rowHeight, pixelsPerDip);

            var panel = new StackPanel();
            if (_displayMode == DisplayMode.Compare)
            {
                panel.Children.Add(CreateLineText(line.Text, fontSize, rowHeight, Color.FromRgb(91, 99, 109)));
                panel.Children.Add(CreateLineText(translated, fontSize, rowHeight, Color.FromRgb(22, 27, 32)));
            }
            else
            {
                panel.Children.Add(CreateLineText(translated, fontSize, rowHeight, Color.FromRgb(22, 27, 32)));
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(244, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 32, 164, 126)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 0, 2, 0),
                Width = box.Width,
                Height = box.Height,
                ClipToBounds = true,
                Child = panel
            };
            Canvas.SetLeft(border, box.Left);
            Canvas.SetTop(border, box.Top);
            TextOverlay.Children.Add(border);
            border.Measure(new System.Windows.Size(box.Width, box.Height));
            border.Arrange(new Rect(box.Left, box.Top, box.Width, box.Height));
        }
        TextOverlay.InvalidateMeasure();
        TextOverlay.InvalidateArrange();
        TextOverlay.UpdateLayout();
    }

    internal static IReadOnlyList<Rect> CalculateOverlayBounds(
        IReadOnlyList<OcrTextLine> lines,
        double scaleX,
        double scaleY,
        double contentWidth,
        double contentHeight)
    {
        var initial = lines.Select(line =>
        {
            var left = Math.Clamp(line.X * scaleX - 1, 0, Math.Max(0, contentWidth - 1));
            var top = Math.Clamp(line.Y * scaleY - 1, 0, Math.Max(0, contentHeight - 1));
            var width = Math.Max(1, Math.Min(line.Width * scaleX + 2, contentWidth - left));
            var height = Math.Max(1, Math.Min(line.Height * scaleY + 2, contentHeight - top));
            return new Rect(left, top, width, height);
        }).ToArray();
        var suppressed = new bool[initial.Length];
        for (var first = 0; first < initial.Length; first++)
        {
            if (suppressed[first]) continue;
            for (var second = first + 1; second < initial.Length; second++)
            {
                if (suppressed[second] || !AreDuplicateRegions(initial[first], initial[second])) continue;
                var firstScore = lines[first].Text.Trim().Length * 1000d + initial[first].Width * initial[first].Height;
                var secondScore = lines[second].Text.Trim().Length * 1000d + initial[second].Width * initial[second].Height;
                if (firstScore >= secondScore) suppressed[second] = true;
                else
                {
                    suppressed[first] = true;
                    break;
                }
            }
        }
        var result = new Rect[initial.Length];

        for (var index = 0; index < initial.Length; index++)
        {
            if (suppressed[index])
            {
                result[index] = Rect.Empty;
                continue;
            }
            var current = initial[index];
            var right = current.Right;
            var bottom = current.Bottom;
            for (var otherIndex = 0; otherIndex < initial.Length; otherIndex++)
            {
                if (otherIndex == index || suppressed[otherIndex]) continue;
                var other = initial[otherIndex];
                if (other.Left > current.Left + 1 && other.Left < right)
                {
                    var verticalOverlap = Math.Max(0, Math.Min(current.Bottom, other.Bottom) - Math.Max(current.Top, other.Top));
                    if (verticalOverlap >= Math.Min(current.Height, other.Height) * 0.35)
                        right = Math.Max(current.Left + 1, other.Left - 1);
                }
                if (other.Top > current.Top + 1 && other.Top < bottom)
                {
                    var horizontalOverlap = Math.Max(0, Math.Min(current.Right, other.Right) - Math.Max(current.Left, other.Left));
                    if (horizontalOverlap >= Math.Min(current.Width, other.Width) * 0.35)
                        bottom = Math.Max(current.Top + 1, other.Top - 1);
                }
            }
            result[index] = new Rect(current.Left, current.Top,
                Math.Max(1, right - current.Left), Math.Max(1, bottom - current.Top));
        }
        return result;
    }

    private static bool AreDuplicateRegions(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        if (intersection.IsEmpty) return false;
        var minimumArea = Math.Max(1, Math.Min(first.Width * first.Height, second.Width * second.Height));
        var overlapRatio = intersection.Width * intersection.Height / minimumArea;
        var sameOrigin = Math.Abs(first.Left - second.Left) <= Math.Max(3, Math.Min(first.Width, second.Width) * 0.18)
                         && Math.Abs(first.Top - second.Top) <= Math.Max(3, Math.Min(first.Height, second.Height) * 0.35);
        return overlapRatio >= 0.72 && sameOrigin;
    }

    private static TextBlock CreateLineText(string text, double fontSize, double height, Color color) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Microsoft YaHei UI"),
        FontSize = fontSize,
        Foreground = new SolidColorBrush(color),
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        LineHeight = Math.Max(1, Math.Min(height, fontSize * 1.15)),
        Height = height,
        VerticalAlignment = VerticalAlignment.Center
    };

    internal static double FitFontSize(
        IReadOnlyList<string> rows,
        double width,
        double rowHeight,
        double pixelsPerDip = 1)
    {
        var minimum = Math.Min(4, Math.Max(1, rowHeight * 0.65));
        var low = minimum;
        var high = Math.Max(minimum, Math.Min(30, rowHeight / 1.15));
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var candidate = (low + high) / 2;
            if (rows.All(row => MeasureTextWidth(row, candidate, pixelsPerDip) <= width)) low = candidate;
            else high = candidate;
        }
        return low;
    }

    private static double MeasureTextWidth(string text, double fontSize, double pixelsPerDip) =>
        new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            fontSize,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace;

    private void SetMode(DisplayMode mode)
    {
        _displayMode = mode;
        var active = new SolidColorBrush(Color.FromRgb(17, 133, 107));
        var inactive = Brushes.Transparent;
        OriginalModeButton.Background = mode == DisplayMode.Original ? active : inactive;
        TranslationModeButton.Background = mode == DisplayMode.Translation ? active : inactive;
        CompareModeButton.Background = mode == DisplayMode.Compare ? active : inactive;
        ApplyDisplayVisuals();
        RenderTextOverlay();
    }

    private void SetBusy(bool busy, string message)
    {
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message;
        TextOverlay.Visibility = busy || _dragging || _displayMode == DisplayMode.Original
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyDisplayVisuals()
    {
        SourceImage.Visibility = !_dragging && _displayMode is DisplayMode.Original or DisplayMode.Compare
            ? Visibility.Visible
            : Visibility.Collapsed;
        TextOverlay.Visibility = !_dragging && BusyOverlay.Visibility != Visibility.Visible
                                 && _displayMode != DisplayMode.Original
            ? Visibility.Visible
            : Visibility.Collapsed;
        CaptureFrame.Background = _displayMode == DisplayMode.Translation
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
    }

    private static string ResolveDefaultTarget(string configured)
        => string.IsNullOrWhiteSpace(configured) || configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : configured;

    internal static string FormatSourceStatus(string selected, string detected)
        => string.Equals(selected, "auto", StringComparison.OrdinalIgnoreCase)
            ? $"自动({detected})"
            : selected;

    private void SetLanguageSelections(string source, string target)
    {
        _changingLanguages = true;
        try
        {
            SelectLanguage(SourceLanguageCombo, string.IsNullOrWhiteSpace(source) ? "auto" : source);
            SelectLanguage(TargetLanguageCombo, ResolveDefaultTarget(target));
        }
        finally { _changingLanguages = false; }
    }

    private static void SelectLanguage(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) continue;
            combo.SelectedItem = item;
            return;
        }
    }

    private static string SelectedTag(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private void OriginalMode_Click(object sender, RoutedEventArgs e) => SetMode(DisplayMode.Original);
    private void TranslationMode_Click(object sender, RoutedEventArgs e) => SetMode(DisplayMode.Translation);
    private void CompareMode_Click(object sender, RoutedEventArgs e) => SetMode(DisplayMode.Compare);

    private async void SourceLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingLanguages && _ready && !_previewMode) await RecognizeAndTranslateAsync(false);
    }

    private async void TargetLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingLanguages && _ready && !_previewMode) await TranslateAsync();
    }

    private async void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || string.IsNullOrWhiteSpace(_layout.Text)) return;
        await CopyTextAsync(_layout.Text, "已复制原文", nameof(CopySource_Click));
    }

    private async void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_translatedText)) return;
        await CopyTextAsync(_translatedText, "已复制译文", nameof(CopyTranslation_Click));
    }

    private async Task CopyTextAsync(string text, string successMessage, string context)
    {
        try
        {
            await ClipboardService.SetTextAsync(text);
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            AppLog.Error(context, ex);
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await TranslateAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        _ = DragResultWindowAsync();
    }

    private void CaptureFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _ = DragResultWindowAsync();
        e.Handled = true;
    }

    private async Task DragResultWindowAsync()
    {
        if (_refreshingSelection || _resizing) return;
        _dragging = true;
        _translationCancellation?.Cancel();
        ApplyDisplayVisuals();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        await RefreshSelectionFromScreenAsync();
    }

    private void ResizeHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_refreshingSelection || sender is not Thumb { Tag: string }) return;
        _translationCancellation?.Cancel();
        _resizing = true;
        _dragging = true;
        _resizeStartBounds = _screenBounds;
        _resizeDeltaPixels = new Vector();
        ApplyDisplayVisuals();
        e.Handled = true;
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_resizing || sender is not Thumb { Tag: string tag }
                       || !Enum.TryParse<SelectionHandle>(tag, out var handle)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _resizeDeltaPixels += new Vector(e.HorizontalChange * dpi.DpiScaleX, e.VerticalChange * dpi.DpiScaleY);
        _screenBounds = ResizeScreenBounds(
            _resizeStartBounds,
            handle,
            _resizeDeltaPixels,
            Forms.SystemInformation.VirtualScreen);
        ConfigurePlacement();
        e.Handled = true;
    }

    private async void ResizeHandle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Handled = true;
        await RefreshSelectionFromScreenAsync();
    }

    private async Task RefreshSelectionFromScreenAsync()
    {
        if (_refreshingSelection) return;
        _refreshingSelection = true;
        var previousOpacity = Opacity;
        try
        {
            _screenBounds = GetCaptureFrameScreenBounds();
            if (_previewMode)
            {
                StatusText.Text = "选区已更新";
                return;
            }

            _ready = false;
            _layout = null;
            _translatedLines = Array.Empty<string>();
            _translatedText = string.Empty;
            TextOverlay.Children.Clear();
            SetBusy(true, "正在重新识别...");

            Opacity = 0;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(70);
            var image = ScreenshotService.CaptureRegion(_screenBounds);
            _image = image;
            SourceImage.Source = image;
            Opacity = previousOpacity;
            ConfigurePlacement();
            await RecognizeAndTranslateAsync(false);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(RefreshSelectionFromScreenAsync), ex);
            SetBusy(false, ex.Message);
            StatusText.Text = ex.Message;
        }
        finally
        {
            Opacity = previousOpacity;
            _dragging = false;
            _refreshingSelection = false;
            ApplyDisplayVisuals();
            RenderTextOverlay();
        }
    }

    private System.Drawing.Rectangle GetCaptureFrameScreenBounds()
    {
        var topLeft = CaptureFrame.PointToScreen(new System.Windows.Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(CaptureFrame);
        var width = Math.Max(1, (int)Math.Round(CaptureFrame.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(CaptureFrame.ActualHeight * dpi.DpiScaleY));
        var bounds = new System.Drawing.Rectangle(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            width,
            height);
        return System.Drawing.Rectangle.Intersect(bounds, Forms.SystemInformation.VirtualScreen);
    }

    internal static System.Drawing.Rectangle ResizeScreenBounds(
        System.Drawing.Rectangle original,
        SelectionHandle handle,
        Vector delta,
        System.Drawing.Rectangle desktop)
    {
        const int minimumWidth = 48;
        const int minimumHeight = 32;
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;
        var deltaX = (int)Math.Round(delta.X);
        var deltaY = (int)Math.Round(delta.Y);

        if (handle is SelectionHandle.NorthWest or SelectionHandle.West or SelectionHandle.SouthWest)
            left = Math.Clamp(original.Left + deltaX, desktop.Left, right - minimumWidth);
        if (handle is SelectionHandle.NorthEast or SelectionHandle.East or SelectionHandle.SouthEast)
            right = Math.Clamp(original.Right + deltaX, left + minimumWidth, desktop.Right);
        if (handle is SelectionHandle.NorthWest or SelectionHandle.North or SelectionHandle.NorthEast)
            top = Math.Clamp(original.Top + deltaY, desktop.Top, bottom - minimumHeight);
        if (handle is SelectionHandle.SouthWest or SelectionHandle.South or SelectionHandle.SouthEast)
            bottom = Math.Clamp(original.Bottom + deltaY, top + minimumHeight, desktop.Bottom);

        return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or ComboBox or ComboBoxItem or Thumb) return true;
        }
        return false;
    }

    private enum DisplayMode { Original, Translation, Compare }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
