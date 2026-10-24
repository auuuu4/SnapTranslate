using System.Diagnostics;
using System.Text;

namespace SnapTranslate.Desktop;

public sealed class MacCaptureOcrService
{
    private const string HelperName = "SnapTranslateVision";

    public async Task<string?> CaptureAndRecognizeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("截图 OCR 目前仅在 macOS 客户端中可用。");

        var temporaryDirectory = Path.Combine(AppDataPaths.ApplicationDataDirectory, "Temp");
        Directory.CreateDirectory(temporaryDirectory);
        CleanOldFiles(temporaryDirectory);
        var imagePath = Path.Combine(temporaryDirectory, $"capture-{Guid.NewGuid():N}.png");

        try
        {
            var captureExitCode = await RunProcessAsync(
                "/usr/sbin/screencapture",
                ["-i", "-x", imagePath],
                cancellationToken);
            if (captureExitCode != 0 || !File.Exists(imagePath)) return null;

            var helperPath = ResolveHelperPath();
            var result = await RunProcessForOutputAsync(helperPath, [imagePath], cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? "macOS Vision OCR 执行失败。"
                        : result.StandardError.Trim());
            return result.StandardOutput.Trim();
        }
        finally
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
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

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: false);
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {fileName}。");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<ProcessResult> RunProcessForOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: true);
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {fileName}。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static Process CreateProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            StandardOutputEncoding = redirectOutput ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirectOutput ? Encoding.UTF8 : null
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }
    }

    private static void CleanOldFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "capture-*.png"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
            }
            catch (IOException)
            {
                // A concurrent process can still own its temporary capture.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best effort and must not block capture.
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
