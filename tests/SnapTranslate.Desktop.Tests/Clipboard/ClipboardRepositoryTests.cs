using SnapTranslate.Desktop.Clipboard;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Clipboard;

public sealed class ClipboardRepositoryTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void InsertAndQueryRoundTrip()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var capture = TextCapture("你好，世界");

        var item = repository.InsertOrUpdate(capture);

        Assert.Equal(1, repository.Count());
        var loaded = repository.GetById(item.Id);
        Assert.NotNull(loaded);
        Assert.Equal(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text, loaded.Kind);
        Assert.Equal("你好，世界", loaded.Text);
        Assert.Null(loaded.FilePath);
        Assert.False(loaded.Favorite);
        Assert.Equal(string.Empty, loaded.Note);
        Assert.Equal(capture.Hash, loaded.Hash);
    }

    [Fact]
    public void DuplicateHashIsDeduplicated()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var capture = TextCapture("重复内容");

        var first = repository.InsertOrUpdate(capture);
        var second = repository.InsertOrUpdate(capture);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, repository.Count());
    }

    [Fact]
    public void SearchMatchesTextFileNameAndNote()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        repository.InsertOrUpdate(TextCapture("Hello 世界", At(0)));
        repository.InsertOrUpdate(FileCapture(["/tmp/report.pdf", "/tmp/photo.png"], At(1)));
        repository.InsertOrUpdate(ImageCapture("/tmp/clipboard-images/a.png", At(2)));

        var byText = repository.Search("世界");
        Assert.Single(byText);
        Assert.Equal("Hello 世界", byText[0].Text);

        var byFile = repository.Search("report.pdf");
        Assert.Single(byFile);
        Assert.Equal(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Files, byFile[0].Kind);

        Assert.Empty(repository.Search("不存在的关键词"));
    }

    [Fact]
    public void FilterReturnsOnlyRequestedKind()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        repository.InsertOrUpdate(TextCapture("文本一", At(0)));
        repository.InsertOrUpdate(TextCapture("文本二", At(1)));
        repository.InsertOrUpdate(FileCapture(["/tmp/a.txt"], At(2)));
        repository.InsertOrUpdate(ImageCapture("/tmp/clipboard-images/b.png", At(3)));

        Assert.Equal(2, repository.Search(kind: SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text).Count);
        Assert.Single(repository.Search(kind: SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Files));
        Assert.Single(repository.Search(kind: SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Image));
        Assert.Equal(4, repository.Count());
        Assert.Equal(2, repository.Count(kind: SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text));
    }

    [Fact]
    public void FavoriteAndNoteArePersisted()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var item = repository.InsertOrUpdate(TextCapture("收藏我"));

        Assert.True(repository.ToggleFavorite(item.Id));
        Assert.True(repository.GetById(item.Id)!.Favorite);
        repository.SetFavorite(item.Id, false);
        Assert.False(repository.GetById(item.Id)!.Favorite);

        repository.SetNote(item.Id, "  备注内容  ");
        Assert.Equal("备注内容", repository.GetById(item.Id)!.Note);
        Assert.Single(repository.Search("备注内容"));
        Assert.False(repository.ToggleFavorite("不存在的id"));
    }

    [Fact]
    public void DeleteAndClearRemoveRecords()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var first = repository.InsertOrUpdate(TextCapture("一"));
        var second = repository.InsertOrUpdate(TextCapture("二"));

        Assert.True(repository.Delete(first.Id));
        Assert.False(repository.Delete(first.Id));
        Assert.Equal(1, repository.Count());
        Assert.Null(repository.GetById(first.Id));
        Assert.NotNull(repository.GetById(second.Id));

        repository.Clear();
        Assert.Equal(0, repository.Count());
    }

    [Fact]
    public void TrimKeepsOnlyNewestRecords()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 510; index++)
            repository.InsertOrUpdate(TextCapture($"条目 {index}", start.AddMinutes(index)));

        Assert.Equal(ClipboardRepository.DefaultMaxItems, repository.Count());
        var newest = repository.Search(limit: 1);
        Assert.Single(newest);
        Assert.Equal("条目 509", newest[0].Text);
        Assert.DoesNotContain(repository.Search(), item => item.Text == "条目 0");
    }

    [Fact]
    public void SearchUsesDescendingPaging()
    {
        using var repository = new ClipboardRepository(_directory.Root);
        var start = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 5; index++)
            repository.InsertOrUpdate(TextCapture($"记录 {index}", start.AddMinutes(index)));

        var firstPage = repository.Search(limit: 2, offset: 0);
        var secondPage = repository.Search(limit: 2, offset: 2);

        Assert.Equal(new[] { "记录 4", "记录 3" }, firstPage.Select(item => item.Text));
        Assert.Equal(new[] { "记录 2", "记录 1" }, secondPage.Select(item => item.Text));
    }

    private static DateTimeOffset At(int minutes)
        => new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static SnapTranslate.Desktop.Platform.ClipboardCapture TextCapture(string text, DateTimeOffset? capturedAt = null)
        => new(
            SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text,
            text,
            null,
            capturedAt ?? DateTimeOffset.Now,
            ClipboardHash.Compute(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text, text));

    private static SnapTranslate.Desktop.Platform.ClipboardCapture FileCapture(string[] paths, DateTimeOffset capturedAt)
    {
        var text = string.Join("\n", paths);
        return new SnapTranslate.Desktop.Platform.ClipboardCapture(
            SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Files,
            text,
            paths[0],
            capturedAt,
            ClipboardHash.Compute(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Files, text));
    }

    private static SnapTranslate.Desktop.Platform.ClipboardCapture ImageCapture(string path, DateTimeOffset capturedAt)
        => new(
            SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Image,
            string.Empty,
            path,
            capturedAt,
            ClipboardHash.Compute(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Image, path));

    public void Dispose() => _directory.Dispose();
}
