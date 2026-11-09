using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapTranslate.Desktop.CaptureTranslation;
using Xunit;

namespace SnapTranslate.Desktop.Tests.CaptureTranslation;

public sealed class ScreenshotTranslationWindowTests
{
    [AvaloniaFact]
    public void WindowRendersTranslationAndSwitchesDisplayModes()
    {
        using var bitmap = CreateImage(900, 600);
        using var host = new TranslationHost("你好，世界。\n第二行译文。");
        var window = host.CreateWindow(bitmap, "Hello world.\nSecond line.");
        try
        {
            window.Show();
            PumpUntil(() => window.TranslatedText.Length > 0, TimeSpan.FromSeconds(5));

            Assert.Equal("你好，世界。\n第二行译文。", window.TranslatedText);
            Assert.False(string.IsNullOrWhiteSpace(window.Provider));
            Assert.Equal("Hello world.\nSecond line.", window.SourceText);

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width >= 500);
            Assert.True(frame.PixelSize.Height >= 400);

            var overlay = window.FindControl<Border>("TranslationOverlay");
            var sourceImage = window.FindControl<Image>("SourceImage");
            Assert.NotNull(overlay);
            Assert.NotNull(sourceImage);
            Assert.True(overlay!.IsVisible);
            Assert.True(sourceImage!.IsVisible);

            var copyTranslation = window.FindControl<Button>("CopyTranslationButton");
            Assert.NotNull(copyTranslation);
            Assert.True(copyTranslation!.IsEffectivelyEnabled);
            Assert.True(copyTranslation.Bounds.Width > 0);

            var status = window.FindControl<TextBlock>("StatusText");
            Assert.NotNull(status);
            var statusBeforeCopy = status!.Text;
            copyTranslation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => status.Text != statusBeforeCopy, TimeSpan.FromSeconds(2));
            Assert.NotEqual(statusBeforeCopy, status.Text);

            var originalButton = window.FindControl<Button>("OriginalModeButton");
            var translationButton = window.FindControl<Button>("TranslationModeButton");
            var compareButton = window.FindControl<Button>("CompareModeButton");
            Assert.NotNull(originalButton);
            Assert.NotNull(translationButton);
            Assert.NotNull(compareButton);

            originalButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(overlay.IsVisible);
            Assert.True(sourceImage.IsVisible);

            translationButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(overlay.IsVisible);
            Assert.False(sourceImage.IsVisible);
            var translationText = window.FindControl<TextBlock>("TranslationText");
            Assert.NotNull(translationText);
            Assert.True(translationText!.IsVisible);

            compareButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(overlay.IsVisible);
            Assert.True(sourceImage.IsVisible);
            var comparePanel = window.FindControl<StackPanel>("ComparePanel");
            Assert.NotNull(comparePanel);
            Assert.True(comparePanel!.IsVisible);
            Assert.True(comparePanel.Children.Count > 0);

            var frameAfterSwitch = window.CaptureRenderedFrame();
            Assert.NotNull(frameAfterSwitch);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WindowShowsReadableErrorAndRetryRecovers()
    {
        using var bitmap = CreateImage(640, 480);
        using var host = new TranslationHost("重试成功的译文。", failFirstRequest: true);
        var window = host.CreateWindow(bitmap, "Hello there.");
        try
        {
            window.Show();
            var errorPanel = window.FindControl<Border>("ErrorPanel");
            Assert.NotNull(errorPanel);
            PumpUntil(() => errorPanel!.IsVisible, TimeSpan.FromSeconds(5));
            Assert.True(errorPanel!.IsVisible);

            var errorText = window.FindControl<TextBlock>("ErrorText");
            Assert.NotNull(errorText);
            Assert.False(string.IsNullOrWhiteSpace(errorText!.Text));

            var retryButton = window.FindControl<Button>("RetryButton");
            Assert.NotNull(retryButton);
            retryButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => window.TranslatedText.Length > 0, TimeSpan.FromSeconds(5));

            Assert.NotEmpty(window.TranslatedText);
            Assert.False(errorPanel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static WriteableBitmap CreateImage(int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var row = new byte[width * 4];
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = (byte)(40 + x * 120 / Math.Max(1, width));
                row[x * 4 + 1] = 90;
                row[x * 4 + 2] = 140;
                row[x * 4 + 3] = 255;
            }

            for (var y = 0; y < height; y++)
                Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }

        return bitmap;
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) break;
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private sealed class TranslationHost : IDisposable
    {
        private readonly HttpClient _httpClient;

        public TranslationHost(string translation, bool failFirstRequest = false)
        {
            _httpClient = new HttpClient(new StubHandler(translation, failFirstRequest));
            Translator = new TranslationService(_httpClient);
        }

        public TranslationService Translator { get; }

        public ScreenshotTranslationWindow CreateWindow(Bitmap image, string sourceText)
        {
            var settings = new AppSettings
            {
                Provider = TranslationProvider.Google,
                SourceLanguage = "auto",
                TargetLanguage = "zh-CN"
            };
            return new ScreenshotTranslationWindow(
                image,
                new PixelRect(0, 0, image.PixelSize.Width, image.PixelSize.Height),
                sourceText,
                settings,
                Translator);
        }

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class StubHandler(string translation, bool failFirstRequest) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requests);
            if (failFirstRequest && requestNumber == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("upstream error", Encoding.UTF8, "text/plain")
                });
            }

            var payload = JsonSerializer.Serialize(new object?[]
            {
                new object?[] { new object?[] { translation, "source text", null, null, 1 } },
                null,
                "en"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
