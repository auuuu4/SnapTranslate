using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using SnapTranslate.Desktop.Capture;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Capture;

public sealed class PinnedImageWindowTests
{
    [AvaloniaFact]
    public void PinnedWindowRendersZoomsAndClosesWithEscape()
    {
        var image = CaptureTestImages.CreateGradientBitmap(400, 300);
        var window = new PinnedImageWindow(image);
        try
        {
            window.Show();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var initialWidth = window.Width;

            window.MouseWheel(new Point(120, 90), new Vector(0, 1));
            Assert.True(window.Width > initialWidth, "滚轮向上应该放大贴图。");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(window.IsVisible, "Esc 应该关闭贴图窗口。");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            image.Dispose();
        }
    }
}
