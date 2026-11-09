using System.Text.Json;
using SnapTranslate.Desktop.Clipboard;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Clipboard;

public sealed class ClipboardHelperProtocolTests
{
    [Fact]
    public void ParsesTextPayload()
    {
        var result = ClipboardHelperProtocol.ParseReadOutput(
            """{"changeCount":7,"kind":"text","text":"你好，剪贴板","hash":"abc123"}""");

        Assert.Equal(7, result.ChangeCount);
        var capture = Assert.IsType<SnapTranslate.Desktop.Platform.ClipboardCapture>(result.Capture);
        Assert.Equal(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text, capture.Kind);
        Assert.Equal("你好，剪贴板", capture.Text);
        Assert.Null(capture.FilePath);
        Assert.Equal("abc123", capture.Hash);
    }

    [Fact]
    public void ParsesImagePayload()
    {
        var result = ClipboardHelperProtocol.ParseReadOutput(
            """{"changeCount":3,"kind":"image","text":"","path":"/tmp/clipboard-images/abc.png","hash":"image-hash"}""");

        var capture = Assert.IsType<SnapTranslate.Desktop.Platform.ClipboardCapture>(result.Capture);
        Assert.Equal(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Image, capture.Kind);
        Assert.Equal(string.Empty, capture.Text);
        Assert.Equal("/tmp/clipboard-images/abc.png", capture.FilePath);
        Assert.Equal("image-hash", capture.Hash);
    }

    [Fact]
    public void ParsesFilesPayload()
    {
        var result = ClipboardHelperProtocol.ParseReadOutput(
            """{"changeCount":9,"kind":"files","files":["/tmp/a.txt","/tmp/b.txt"],"path":"/tmp/a.txt","hash":"files-hash"}""");

        var capture = Assert.IsType<SnapTranslate.Desktop.Platform.ClipboardCapture>(result.Capture);
        Assert.Equal(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Files, capture.Kind);
        Assert.Equal("/tmp/a.txt\n/tmp/b.txt", capture.Text);
        Assert.Equal("/tmp/a.txt", capture.FilePath);
        Assert.Equal("files-hash", capture.Hash);
    }

    [Fact]
    public void ParsesEmptyPasteboardAsNone()
    {
        var result = ClipboardHelperProtocol.ParseReadOutput("""{"changeCount":3,"kind":"none","text":""}""");

        Assert.Equal(3, result.ChangeCount);
        Assert.Null(result.Capture);
    }

    [Fact]
    public void ParsesChangeCountOutput()
    {
        Assert.Equal(42, ClipboardHelperProtocol.ParseChangeCountOutput("42\n"));
        Assert.Equal(-1, ClipboardHelperProtocol.ParseChangeCountOutput(string.Empty));
        Assert.Equal(-1, ClipboardHelperProtocol.ParseChangeCountOutput("abc"));
    }

    [Fact]
    public void MalformedJsonThrows()
    {
        Assert.Throws<FormatException>(() => ClipboardHelperProtocol.ParseReadOutput("[]"));
        Assert.ThrowsAny<JsonException>(() => ClipboardHelperProtocol.ParseReadOutput("not json"));
    }

    [Fact]
    public void CopyPayloadContainsKindPathAndFiles()
    {
        var capture = new SnapTranslate.Desktop.Platform.ClipboardCapture(
            SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Files,
            "/tmp/a.txt\n/tmp/b.txt",
            "/tmp/a.txt",
            DateTimeOffset.Now,
            "hash");

        using var document = JsonDocument.Parse(ClipboardHelperProtocol.BuildCopyPayload(capture));
        var root = document.RootElement;
        Assert.Equal("files", root.GetProperty("kind").GetString());
        Assert.Equal("/tmp/a.txt\n/tmp/b.txt", root.GetProperty("text").GetString());
        Assert.Equal("/tmp/a.txt", root.GetProperty("path").GetString());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.Equal("/tmp/b.txt", root.GetProperty("files")[1].GetString());
    }
}
