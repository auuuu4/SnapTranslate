namespace SnapTranslate.Desktop.CaptureTranslation;

/// <summary>
/// 截图翻译覆盖层的排版参数，长度单位都是 DIP。
/// 只做纯计算、不依赖任何控件，方便在无头环境里直接单元测试。
/// </summary>
public sealed record TranslationOverlayLayout(
    double PanelWidth,
    double PanelHeight,
    double PanelMargin,
    double FontSize,
    double LineHeight,
    int MaxCharsPerLine,
    int RequiredRows,
    int VisibleLines,
    bool BottomPanel,
    bool Compact);

/// <summary>
/// 根据截图尺寸与文本量推导翻译覆盖层的字号、行高、每行字符数和面板尺寸。
/// 文本短时覆盖层悬浮在图片上；文本太长（或图片太小）时退化为贴底的整宽面板。
/// </summary>
public static class ScreenshotTranslationLayout
{
    /// <summary>覆盖层最高占图片高度的比例，超过就退化为底部面板。</summary>
    public const double MaxOverlayHeightRatio = 0.72;

    /// <summary>底部面板高度占图片高度的比例。</summary>
    public const double BottomPanelHeightRatio = 0.5;

    /// <summary>正常尺寸下允许的最小字号；只有极小图片才允许更小。</summary>
    public const double MinimumReadableFontSize = 9;

    private const double MinimumFontSize = 4;
    private const double MaximumFontSize = 20;

    /// <summary>
    /// 计算覆盖层排版。
    /// </summary>
    /// <param name="imageWidth">图片显示宽度（通常按窗口可用宽度把截图像素折算成 DIP）。</param>
    /// <param name="imageHeight">图片显示高度（同上）。</param>
    /// <param name="lineCount">原文行数。</param>
    /// <param name="textLength">译文长度；还没翻译时传原文长度即可。</param>
    public static TranslationOverlayLayout Compute(int imageWidth, int imageHeight, int lineCount, int textLength)
    {
        var width = Math.Max(1d, imageWidth);
        var height = Math.Max(1d, imageHeight);
        var lines = Math.Max(0, lineCount);
        var length = Math.Max(0, textLength);

        var compact = width < 520 || height < 320;
        var margin = Clamp(Math.Min(width, height) * 0.035, 4, 24);
        var font = Clamp(height * 0.034, MinimumReadableFontSize, MaximumFontSize);
        var panelWidth = ResolvePanelWidth(width, margin, bottomPanel: false);
        var lineHeight = font * 1.5;
        var charsPerLine = CountCharsPerLine(panelWidth, font);
        var rows = EstimateRows(lines, length, charsPerLine);
        var neededHeight = rows * lineHeight + margin * 2;
        var maxOverlayHeight = Math.Max(1, height * MaxOverlayHeightRatio);
        var bottomPanel = width < 220 || height < 120 || neededHeight > maxOverlayHeight;

        double panelHeight;
        if (bottomPanel)
        {
            // 退化为贴底面板：占满可用宽度，高度取图片的一半，按行数反推字号。
            panelWidth = ResolvePanelWidth(width, margin, bottomPanel: true);
            panelHeight = Math.Min(height, Math.Max(1, height * BottomPanelHeightRatio));
            var fittingFont = (panelHeight - margin * 2) / Math.Max(1, rows) / 1.5;
            font = Clamp(Math.Min(font, fittingFont), MinimumFontSize, MaximumFontSize);
            if (font < MinimumReadableFontSize && panelHeight >= 120) font = MinimumReadableFontSize;
            lineHeight = font * 1.5;
            charsPerLine = CountCharsPerLine(panelWidth, font);
            rows = EstimateRows(lines, length, charsPerLine);
            panelHeight = Math.Min(
                height,
                Math.Max(panelHeight, Math.Min(maxOverlayHeight, rows * lineHeight + margin * 2)));
        }
        else
        {
            panelHeight = Math.Max(1, Math.Min(maxOverlayHeight, neededHeight));
        }

        var contentHeight = Math.Max(1, panelHeight - margin * 2);
        var visibleLines = Math.Max(1, Math.Min(rows, (int)Math.Floor(contentHeight / lineHeight)));

        return new TranslationOverlayLayout(
            Round(panelWidth),
            Round(panelHeight),
            Round(margin),
            Round(font),
            Round(lineHeight),
            charsPerLine,
            rows,
            visibleLines,
            bottomPanel,
            compact);
    }

    /// <summary>把原文按换行拆成行，空文本也会返回一个空行，避免调用方到处判空。</summary>
    public static string[] SplitSourceLines(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return [string.Empty];
        var normalized = sourceText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        return normalized.Length == 0
            ? [string.Empty]
            : normalized.Split('\n', StringSplitOptions.TrimEntries);
    }

    private static double ResolvePanelWidth(double width, double margin, bool bottomPanel)
    {
        var available = Math.Max(1, width - margin * 2);
        if (bottomPanel) return available;
        var preferred = Math.Max(280, width * 0.82);
        return Math.Max(1, Math.Min(available, preferred));
    }

    private static int CountCharsPerLine(double panelWidth, double fontSize) =>
        Math.Max(4, (int)Math.Floor(panelWidth / Math.Max(1, fontSize * 0.6)));

    private static int EstimateRows(int lineCount, int textLength, int charsPerLine)
    {
        var rows = Math.Max(1, lineCount);
        if (textLength <= 0) return rows;
        var wrappedRows = (int)Math.Ceiling(textLength / (double)Math.Max(1, charsPerLine));
        return Math.Max(rows, wrappedRows);
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsNaN(value) ? minimum : Math.Clamp(value, minimum, maximum);

    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2) : 0;
}
