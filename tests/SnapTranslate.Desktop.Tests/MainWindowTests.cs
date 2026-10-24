using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace SnapTranslate.Desktop.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindowRendersAtMinimumAndDefaultSizes()
    {
        using var temporary = new TestDataDirectory();
        var window = new MainWindow(temporary.Path);
        try
        {
            window.Show();
            AssertLayout(window, 900, 680);
            AssertLayout(window, 720, 560);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EveryPrimaryTabHasVisibleContent()
    {
        using var temporary = new TestDataDirectory();
        var window = new MainWindow(temporary.Path);
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("MainTabs");
            Assert.NotNull(tabs);

            for (var index = 0; index < 5; index++)
            {
                tabs.SelectedIndex = index;
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.True(frame.PixelSize.Width >= 720);
                Assert.True(frame.PixelSize.Height >= 560);
                SaveFrame(frame, $"tab-{index}.png");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertLayout(MainWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal((int)width, frame.PixelSize.Width);
        Assert.Equal((int)height, frame.PixelSize.Height);

        var translateButton = window.FindControl<Button>("TranslateButton");
        var input = window.FindControl<TextBox>("InputText");
        var result = window.FindControl<TextBox>("InputResultText");
        Assert.NotNull(translateButton);
        Assert.NotNull(input);
        Assert.NotNull(result);
        Assert.True(translateButton.Bounds.Width >= 80);
        Assert.True(input.Bounds.Height > 100);
        Assert.True(result.Bounds.Height > 100);
        SaveFrame(frame, $"window-{(int)width}x{(int)height}.png");
    }

    private static void SaveFrame(Bitmap frame, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("SNAPTRANSLATE_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, fileName));
    }

    private sealed class TestDataDirectory : IDisposable
    {
        public TestDataDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SnapTranslate.Desktop.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
