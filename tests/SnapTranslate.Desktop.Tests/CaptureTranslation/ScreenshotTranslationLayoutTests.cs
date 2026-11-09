using SnapTranslate.Desktop.CaptureTranslation;
using Xunit;

namespace SnapTranslate.Desktop.Tests.CaptureTranslation;

public sealed class ScreenshotTranslationLayoutTests
{
    [Fact]
    public void ShortTextProducesSingleFloatingRow()
    {
        var layout = ScreenshotTranslationLayout.Compute(1200, 800, 1, 12);

        Assert.False(layout.BottomPanel);
        Assert.False(layout.Compact);
        Assert.Equal(1, layout.RequiredRows);
        Assert.Equal(1, layout.VisibleLines);
        Assert.Equal(20d, layout.FontSize);
        Assert.Equal(30d, layout.LineHeight);
        Assert.Equal(24d, layout.PanelMargin);
        Assert.Equal(78d, layout.PanelHeight);
        Assert.InRange(layout.PanelWidth, 900d, 1000d);
        Assert.True(layout.MaxCharsPerLine >= 12);
    }

    [Fact]
    public void LongTextWrapsIntoMultipleRows()
    {
        var layout = ScreenshotTranslationLayout.Compute(1200, 800, 2, 900);

        Assert.False(layout.BottomPanel);
        Assert.True(layout.RequiredRows > 2);
        Assert.Equal(layout.RequiredRows, layout.VisibleLines);
        Assert.True(layout.MaxCharsPerLine < 900);
        Assert.True(layout.PanelHeight <= 800 * ScreenshotTranslationLayout.MaxOverlayHeightRatio + 1);
    }

    [Fact]
    public void ExtremelyLongTextFallsBackToBottomPanel()
    {
        var layout = ScreenshotTranslationLayout.Compute(1200, 800, 4, 4000);

        Assert.True(layout.BottomPanel);
        Assert.Equal(400d, layout.PanelHeight);
        Assert.Equal(9d, layout.FontSize);
        Assert.True(layout.VisibleLines > 0);
        Assert.Equal(1152d, layout.PanelWidth);
        Assert.InRange(layout.LineHeight, 13d, 14d);
    }

    [Fact]
    public void TinyImageDoesNotThrowAndKeepsSaneValues()
    {
        var layout = ScreenshotTranslationLayout.Compute(1, 1, 3, 50);

        Assert.True(layout.BottomPanel);
        Assert.True(layout.Compact);
        Assert.True(layout.FontSize > 0);
        Assert.True(layout.LineHeight > 0);
        Assert.True(layout.PanelWidth >= 1);
        Assert.True(layout.PanelHeight >= 1);
        Assert.True(layout.PanelMargin > 0);
        Assert.True(layout.MaxCharsPerLine >= 4);
        Assert.True(layout.RequiredRows >= 1);
        Assert.True(layout.VisibleLines >= 1);
    }

    [Fact]
    public void NonPositiveInputsAreClamped()
    {
        var layout = ScreenshotTranslationLayout.Compute(0, -10, -5, -100);

        Assert.True(layout.FontSize > 0);
        Assert.True(layout.LineHeight > 0);
        Assert.True(layout.PanelWidth >= 1);
        Assert.True(layout.PanelHeight >= 1);
        Assert.True(layout.RequiredRows >= 1);
        Assert.True(layout.VisibleLines >= 1);
        Assert.True(layout.BottomPanel);
    }

    [Fact]
    public void SmallWindowUsesCompactModeWithoutBottomPanel()
    {
        var layout = ScreenshotTranslationLayout.Compute(400, 260, 2, 100);

        Assert.True(layout.Compact);
        Assert.False(layout.BottomPanel);
        Assert.True(layout.FontSize >= ScreenshotTranslationLayout.MinimumReadableFontSize);
    }

    [Fact]
    public void SourceLinesAreSplitAndNormalized()
    {
        Assert.Equal(["第一行", "第二行"], ScreenshotTranslationLayout.SplitSourceLines("第一行\r\n第二行"));
        var empty = ScreenshotTranslationLayout.SplitSourceLines("   ");
        Assert.Single(empty);
        Assert.Equal(string.Empty, empty[0]);
    }
}
