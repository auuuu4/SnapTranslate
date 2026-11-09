using Avalonia;
using Avalonia.Headless.XUnit;
using SnapTranslate.Desktop.Capture;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Capture;

public sealed class CaptureComposerTests
{
    [AvaloniaFact]
    public void CropUsesRequestedPixelRegion()
    {
        using var source = CaptureTestImages.CreateGradientBitmap(800, 600);

        using var result = CaptureComposer.Render(source, null, new PixelRect(100, 50, 320, 240));

        Assert.Equal(320, result.PixelSize.Width);
        Assert.Equal(240, result.PixelSize.Height);
    }

    [AvaloniaFact]
    public void EmptyAnnotationsMatchCrop()
    {
        using var source = CaptureTestImages.CreateGradientBitmap(800, 600);
        var region = new PixelRect(100, 50, 320, 240);

        using var result = CaptureComposer.Render(source, null, region);

        var sourcePixels = CaptureTestImages.ReadBgra(source);
        var resultPixels = CaptureTestImages.ReadBgra(result);
        var mismatches = 0;
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var expected = CaptureTestImages.PixelAt(sourcePixels, source.PixelSize.Width, region.X + x, region.Y + y);
                var actual = CaptureTestImages.PixelAt(resultPixels, region.Width, x, y);
                if (Math.Abs(expected.R - actual.R) > 2
                    || Math.Abs(expected.G - actual.G) > 2
                    || Math.Abs(expected.B - actual.B) > 2)
                {
                    mismatches++;
                }
            }
        }

        Assert.Equal(0, mismatches);
    }

    [AvaloniaFact]
    public void MosaicChangesPixels()
    {
        using var source = CaptureTestImages.CreateGradientBitmap(400, 300);
        var region = new PixelRect(0, 0, 400, 300);
        var mosaic = Annotations.Mosaic(new Rect(50, 50, 200, 150), 16);

        using var result = CaptureComposer.Render(source, [mosaic], region);

        var sourcePixels = CaptureTestImages.ReadBgra(source);
        var resultPixels = CaptureTestImages.ReadBgra(result);
        var changed = 0;
        for (var y = 60; y < 190; y += 7)
        {
            for (var x = 60; x < 240; x += 7)
            {
                var expected = CaptureTestImages.PixelAt(sourcePixels, 400, x, y);
                var actual = CaptureTestImages.PixelAt(resultPixels, 400, x, y);
                if (expected != actual) changed++;
            }
        }

        Assert.True(changed > 100, $"马赛克应该改变像素，实际只有 {changed} 个采样点变化。");
    }

    [AvaloniaFact]
    public void MosaicBlocksAreUniform()
    {
        using var source = CaptureTestImages.CreateGradientBitmap(400, 300);
        var mosaic = Annotations.Mosaic(new Rect(40, 40, 160, 120), 20);

        using var result = CaptureComposer.Render(source, [mosaic], new PixelRect(0, 0, 400, 300));
        var pixels = CaptureTestImages.ReadBgra(result);

        // 第一块马赛克内部（避开右/下边界）应该是同一种颜色。
        var first = CaptureTestImages.PixelAt(pixels, 400, 42, 42);
        var second = CaptureTestImages.PixelAt(pixels, 400, 48, 48);
        Assert.Equal(first, second);
    }

    [AvaloniaFact]
    public void RegionOutsideImageIsClamped()
    {
        using var source = CaptureTestImages.CreateGradientBitmap(200, 100);

        using var result = CaptureComposer.Render(source, null, new PixelRect(190, 90, 100, 100));

        Assert.Equal(10, result.PixelSize.Width);
        Assert.Equal(10, result.PixelSize.Height);
    }
}
