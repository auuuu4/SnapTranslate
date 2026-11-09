using SnapTranslate.Desktop.CaptureTranslation;
using Xunit;

namespace SnapTranslate.Desktop.Tests.CaptureTranslation;

public sealed class TranslationLineMapperTests
{
    [Fact]
    public void MatchedLineCountKeepsProviderLines()
    {
        var lines = TranslationLineMapper.Split(["Hello world.", "Second line."], "你好，世界。\n第二行。");

        Assert.Equal(2, lines.Count);
        Assert.Equal("你好，世界。", lines[0]);
        Assert.Equal("第二行。", lines[1]);
    }

    [Fact]
    public void MismatchedLineCountSplitsAtSentenceBoundary()
    {
        var lines = TranslationLineMapper.Split(
            ["Hello world.", "Second line."],
            "这是第一行译文。这是第二行译文。");

        Assert.Equal(2, lines.Count);
        Assert.Contains("第一", lines[0]);
        Assert.Contains("第二", lines[1]);
    }

    [Fact]
    public void EmptyTranslationReturnsEmptyLines()
    {
        var lines = TranslationLineMapper.Split(["Hello.", "World."], "   ");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void SingleSourceLineKeepsWholeTranslation()
    {
        var lines = TranslationLineMapper.Split(["only line"], "第一段\n第二段\n第三段");

        var line = Assert.Single(lines);
        Assert.Contains("第一段", line);
        Assert.Contains("第三段", line);
    }
}
