using System.Diagnostics;
using System.Text;

namespace SnapTranslate.Desktop.Platform;

/// <summary>
/// 调用随应用打包的 Swift 辅助程序 SnapTranslateVision（系统 Vision 框架）。
/// 输出为纯文本行，每行一段。
/// </summary>
public sealed class MacVisionTextRecognizer : ITextRecognizer
{
    private const string HelperName = "SnapTranslateVision";

    public bool IsSupported => OperatingSystem.IsMacOS();

    public async Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("要识别的图片不存在。", imagePath);
        if (!IsSupported)
            throw new PlatformNotSupportedException("当前平台没有可用的 OCR 引擎。");

        var helper = ResolveHelperPath();
        var startInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(imagePath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("无法启动 macOS Vision OCR 辅助程序。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "macOS Vision OCR 执行失败。" : error.Trim());
        }

        return output.Trim();
    }

    private static string ResolveHelperPath()
    {
        var configured = Environment.GetEnvironmentVariable("SNAPTRANSLATE_VISION_HELPER");
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, HelperName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", HelperName))
        };
        var helper = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return helper ?? throw new FileNotFoundException(
            "应用包中缺少 macOS Vision OCR 辅助程序。请使用 scripts/package-macos.sh 生成应用包。",
            HelperName);
    }
}
