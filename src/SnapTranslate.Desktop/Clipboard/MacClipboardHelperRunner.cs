using System.Diagnostics;
using System.Text;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>调用随应用打包的 Swift 辅助程序 SnapTranslatePasteboard。</summary>
public sealed class MacClipboardHelperRunner : IClipboardHelperRunner
{
    /// <summary>辅助程序文件名。</summary>
    public const string HelperName = "SnapTranslatePasteboard";

    private const string HelperEnvironmentVariable = "SNAPTRANSLATE_PASTEBOARD_HELPER";

    private readonly string _imagesDirectory;

    /// <summary>创建 runner；图片默认写入 &lt;AppData&gt;/Data/clipboard-images。</summary>
    public MacClipboardHelperRunner(string? imagesDirectory = null)
    {
        _imagesDirectory = string.IsNullOrWhiteSpace(imagesDirectory)
            ? Path.Combine(AppDataPaths.ApplicationDataDirectory, "Data", "clipboard-images")
            : imagesDirectory;
    }

    /// <summary>当前平台是否支持剪贴板辅助程序。</summary>
    public bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>读取 changeCount。</summary>
    public async Task<int> GetChangeCountAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync([ClipboardHelperProtocol.ChangeCountArgument], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw CreateException(result, "读取剪贴板状态失败。");
        return ClipboardHelperProtocol.ParseChangeCountOutput(result.StandardOutput);
    }

    /// <summary>读取剪贴板内容。</summary>
    public async Task<ClipboardHelperReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_imagesDirectory);
        var result = await RunAsync(
            [ClipboardHelperProtocol.ImagesDirectoryArgument, _imagesDirectory],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw CreateException(result, "读取剪贴板内容失败。");
        return ClipboardHelperProtocol.ParseReadOutput(result.StandardOutput);
    }

    /// <summary>把一条历史写回剪贴板。</summary>
    public async Task<bool> CopyAsync(Platform.ClipboardCapture item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var payload = ClipboardHelperProtocol.BuildCopyPayload(item);
        var result = await RunAsync(
            [ClipboardHelperProtocol.CopyArgument, payload],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>按环境变量、应用目录、Resources 目录的顺序查找辅助程序。</summary>
    public static string ResolveHelperPath()
    {
        var configured = Environment.GetEnvironmentVariable(HelperEnvironmentVariable);
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, HelperName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", HelperName))
        };
        var helper = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return helper ?? throw new FileNotFoundException(
            "应用包中缺少剪贴板辅助程序。请先运行 scripts/build-macos-helper.sh 生成，再随应用打包。",
            HelperName);
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("剪贴板辅助程序只在 macOS 上可用。");

        var startInfo = new ProcessStartInfo(ResolveHelperPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("无法启动 macOS 剪贴板辅助程序。");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static Exception CreateException(ProcessResult result, string fallback)
        => new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? fallback : result.StandardError.Trim());

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
