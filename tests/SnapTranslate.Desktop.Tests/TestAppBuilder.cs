using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;

[assembly: AvaloniaTestApplication(typeof(SnapTranslate.Desktop.Tests.TestAppBuilder))]

namespace SnapTranslate.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
