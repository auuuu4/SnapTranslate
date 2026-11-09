namespace SnapTranslate.Desktop.Platform;

/// <summary>
/// 按当前平台装配真实的服务实现。非 macOS 平台保持空实现，
/// 这样在 Windows 上跑无头测试时所有 UI 都能正常构造。
/// </summary>
public static class PlatformBootstrap
{
    public static void Configure()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PlatformServices.ScreenCapture = new MacScreenCaptureService();
        PlatformServices.TextRecognizer = new MacVisionTextRecognizer();
        PlatformServices.Hotkeys = new CarbonHotkeyService();
    }
}
