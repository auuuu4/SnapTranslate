using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SnapTranslate.Desktop.Capture;
using SnapTranslate.Desktop.CaptureTranslation;
using SnapTranslate.Desktop.Clipboard;
using SnapTranslate.Desktop.Platform;
using PlatformClipboard = SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop;

/// <summary>截图流程：抓屏 → 标注浮层 → 按动作分发（复制/保存/贴图/OCR/翻译）。</summary>
public partial class MainWindow
{
    private void Capture_Click(object? sender, RoutedEventArgs e) => _ = StartCaptureAsync(CaptureAction.Copy);

    private void CaptureTranslate_Click(object? sender, RoutedEventArgs e) => _ = StartCaptureAsync(CaptureAction.Translate);

    private void CaptureOcr_Click(object? sender, RoutedEventArgs e) => _ = StartCaptureAsync(CaptureAction.Ocr);

    private void SilentOcr_Click(object? sender, RoutedEventArgs e) => _ = StartCaptureAsync(CaptureAction.Ocr, silent: true);

    private void OpenClipboard_Click(object? sender, RoutedEventArgs e) => ShowClipboardHistory();

    private async Task StartCaptureAsync(CaptureAction action, bool silent = false)
    {
        if (_captureInProgress) return;
        if (!PlatformServices.ScreenCapture.IsSupported)
        {
            ScreenshotStatus.Text = "当前平台不支持截图（需要在 macOS 上运行）";
            return;
        }

        _captureInProgress = true;
        try
        {
            ScreenshotStatus.Text = "正在截屏...";
            Hide();
            await Task.Delay(160);
            var screen = await PlatformServices.ScreenCapture.CaptureAsync();
            if (screen is null)
            {
                ScreenshotStatus.Text = "截图失败，请检查「系统设置 → 隐私与安全性 → 屏幕录制」是否已授权";
                return;
            }

            var outcome = await CaptureOverlayWindow.ShowAsync(this, screen);
            if (outcome is null)
            {
                ScreenshotStatus.Text = "已取消截图";
                FooterStatus.Text = "就绪";
                return;
            }

            await HandleCaptureAsync(outcome, silent, screen.Scale);
        }
        catch (Exception ex)
        {
            ScreenshotStatus.Text = ErrorMessage(ex);
            FooterStatus.Text = "截图处理失败";
        }
        finally
        {
            _captureInProgress = false;
            if (!IsVisible) Show();
        }
    }

    private async Task HandleCaptureAsync(CaptureOutcome outcome, bool silent, double scale)
    {
        switch (outcome.Action)
        {
            case CaptureAction.Copy:
                await CopyImageAsync(outcome.Image);
                outcome.Image.Dispose();
                ScreenshotStatus.Text = "已复制到剪贴板";
                FooterStatus.Text = "截图完成";
                break;

            case CaptureAction.Save:
                outcome.Image.Dispose();
                ScreenshotStatus.Text = "截图已保存";
                FooterStatus.Text = "截图完成";
                break;

            case CaptureAction.Pin:
                PinnedImageWindow.ShowPinned(outcome.Image, scale, _settings.PinnedImageEdgeEffect);
                ScreenshotStatus.Text = "已贴到屏幕上";
                FooterStatus.Text = "贴图完成";
                break;

            case CaptureAction.Ocr:
            {
                var text = await RecognizeAsync(outcome.Image);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("选区中没有识别到文本。");
                if (silent)
                {
                    if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
                    ScreenshotStatus.Text = $"已识别并复制 {text.Length:N0} 个字符";
                }
                else
                {
                    InputText.Text = text;
                    InputResultText.Clear();
                    MainTabs.SelectedIndex = 0;
                    ScreenshotStatus.Text = $"已识别 {text.Length:N0} 个字符";
                }

                outcome.Image.Dispose();
                FooterStatus.Text = "截图 OCR 完成";
                break;
            }

            case CaptureAction.Translate:
            {
                var text = await RecognizeAsync(outcome.Image);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("选区中没有识别到文本。");
                var window = new ScreenshotTranslationWindow(
                    outcome.Image,
                    outcome.Region,
                    text,
                    CreateTranslationSettingsSnapshot(),
                    _translator);
                window.TranslationCompleted += (_, args) => AddHistory(args.Source, args.Translation, args.Provider);
                window.Show();
                ScreenshotStatus.Text = "截图翻译窗口已打开";
                FooterStatus.Text = "正在翻译选区";
                break;
            }
        }
    }

    private async Task<string> RecognizeAsync(Bitmap image)
    {
        if (!PlatformServices.TextRecognizer.IsSupported)
            throw new InvalidOperationException("当前平台没有可用的 OCR 引擎。");
        var path = SaveTemporaryPng(image);
        try
        {
            return await PlatformServices.TextRecognizer.RecognizeAsync(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task CopyImageAsync(Bitmap image)
    {
        var path = SaveTemporaryPng(image);
        var capture = new PlatformClipboard.ClipboardCapture(
            PlatformClipboard.ClipboardCaptureKind.Image,
            string.Empty,
            path,
            DateTimeOffset.Now,
            ClipboardHash.FileDigest(path));
        if (await PlatformServices.Clipboard.CopyToClipboardAsync(capture)) return;
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(path);
    }

    private static string SaveTemporaryPng(Bitmap image)
    {
        var directory = Path.Combine(AppDataPaths.ApplicationDataDirectory, "Temp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"capture-{Guid.NewGuid():N}.png");
        image.Save(path);
        return path;
    }

    private void AddHistory(string source, string translation, string provider)
    {
        _history.Insert(0, new HistoryEntry
        {
            CreatedAt = DateTime.Now,
            Source = source,
            Translation = translation,
            Provider = provider
        });
        while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        try
        {
            _storage.SaveHistory(_history);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"历史保存失败：{ex.Message}";
        }
    }
}