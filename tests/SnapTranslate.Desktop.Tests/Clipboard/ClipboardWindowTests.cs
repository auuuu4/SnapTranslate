using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using SnapTranslate.Desktop.Clipboard;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Clipboard;

public sealed class ClipboardWindowTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [AvaloniaFact]
    public void EmptyHistoryShowsEmptyStateAndRenders()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var window = new ClipboardWindow(repository);
        try
        {
            window.Show();
            window.RefreshItems();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width > 0);
            Assert.True(frame.PixelSize.Height > 0);

            var emptyState = window.FindControl<StackPanel>("EmptyState");
            Assert.NotNull(emptyState);
            Assert.True(emptyState.IsVisible);

            var list = window.FindControl<ListBox>("ItemsList");
            Assert.NotNull(list);
            Assert.Equal(0, list.ItemCount);

            var countText = window.FindControl<TextBlock>("CountText");
            Assert.NotNull(countText);
            Assert.Equal("0 条", countText.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HistoryItemsAreListedAfterRefresh()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        repository.InsertOrUpdate(TextCapture("第一段文本", DateTimeOffset.Now));
        repository.InsertOrUpdate(TextCapture("第二段文本", DateTimeOffset.Now.AddSeconds(1)));

        var window = new ClipboardWindow(repository);
        try
        {
            window.Show();
            window.RefreshItems();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var list = window.FindControl<ListBox>("ItemsList");
            Assert.NotNull(list);
            Assert.Equal(2, list.ItemCount);

            var emptyState = window.FindControl<StackPanel>("EmptyState");
            Assert.NotNull(emptyState);
            Assert.False(emptyState.IsVisible);

            var copyButton = window.FindControl<Button>("CopyButton");
            Assert.NotNull(copyButton);
            Assert.True(copyButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    private static SnapTranslate.Desktop.Platform.ClipboardCapture TextCapture(string text, DateTimeOffset capturedAt)
        => new(
            SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text,
            text,
            null,
            capturedAt,
            ClipboardHash.Compute(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text, text));

    public void Dispose() => _directory.Dispose();
}
