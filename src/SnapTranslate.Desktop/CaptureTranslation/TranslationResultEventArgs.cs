namespace SnapTranslate.Desktop.CaptureTranslation;

/// <summary>一次截图翻译完成后的结果，供主窗口写历史记录。</summary>
public sealed record TranslationResultEventArgs(string Source, string Translation, string Provider);
