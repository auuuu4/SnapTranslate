using Avalonia;
using SnapTranslate.Desktop.Capture;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Capture;

public sealed class CaptureGeometryTests
{
    [Fact]
    public void ReverseDragIsNormalized()
    {
        var rect = CaptureGeometry.Normalize(new Point(520, 380), new Point(120, 80));

        Assert.Equal(new Rect(120, 80, 400, 300), rect);
    }

    [Fact]
    public void SelectionSmallerThanMinimumIsExpanded()
    {
        var rect = CaptureGeometry.Clamp(new Rect(100, 100, 2, 1), new Size(800, 600));

        Assert.Equal(CaptureGeometry.MinimumSelectionSize, rect.Width);
        Assert.Equal(CaptureGeometry.MinimumSelectionSize, rect.Height);
        Assert.Equal(100, rect.X);
        Assert.Equal(100, rect.Y);
    }

    [Fact]
    public void SelectionOutsideBoundsIsPulledBack()
    {
        var rect = CaptureGeometry.Clamp(new Rect(795, 595, 300, 300), new Size(800, 600));

        Assert.Equal(500, rect.X);
        Assert.Equal(300, rect.Y);
        Assert.Equal(800, rect.Right);
        Assert.Equal(600, rect.Bottom);
    }

    [Fact]
    public void RetinaScaleConvertsBetweenDipAndPixels()
    {
        var imageRect = CaptureGeometry.ToImageRect(new Rect(10, 20, 30, 40), 2);

        Assert.Equal(new Rect(20, 40, 60, 80), imageRect);
        Assert.Equal(new Point(5, 10), CaptureGeometry.ToDipPoint(new Point(10, 20), 2));
        Assert.Equal(new Point(10, 20), CaptureGeometry.ToImagePoint(new Point(5, 10), 2));

        var pixelRect = CaptureGeometry.ToPixelRect(new Rect(10, 20, 30, 40), 2, new PixelSize(800, 600));
        Assert.Equal(new PixelRect(20, 40, 60, 80), pixelRect);
        Assert.Equal(new Rect(10, 20, 30, 40), CaptureGeometry.ToDipRect(pixelRect, 2));
    }

    [Fact]
    public void PixelRectIsClampedToImage()
    {
        // DIP 矩形在 2x 下换算成 (780,580,600,600)，超出 800x600 图像的部分应被裁掉。
        var pixelRect = CaptureGeometry.ToPixelRect(new Rect(390, 290, 300, 300), 2, new PixelSize(800, 600));

        Assert.Equal(new PixelRect(780, 580, 20, 20), pixelRect);
    }

    [Fact]
    public void ResizeKeepsMinimumSizeAndStaysInsideBounds()
    {
        var resized = CaptureGeometry.Resize(
            new Rect(100, 100, 200, 200), SelectionHandle.East, new Vector(-500, 0), new Size(800, 600));

        Assert.Equal(CaptureGeometry.MinimumSelectionSize, resized.Width);
        Assert.Equal(100, resized.Left);
    }
}