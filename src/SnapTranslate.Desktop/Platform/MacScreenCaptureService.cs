using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnapTranslate.Desktop.Platform;

/// <summary>
/// macOS 整屏截图。用系统自带的 screencapture，不引入额外 interop；
/// 首次调用会触发「屏幕录制」权限申请。
/// </summary>
public sealed class MacScreenCaptureService : IScreenCaptureService
{
    private const string CaptureExecutable = "/usr/sbin/screencapture";

    public bool IsSupported => OperatingSystem.IsMacOS();

    public async Task<CapturedScreen?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("整屏截图只在 macOS 上可用。");

        var directory = Path.Combine(AppDataPaths.ApplicationDataDirectory, "Temp");
        Directory.CreateDirectory(directory);
        CleanOldFiles(directory);
        var path = Path.Combine(directory, $"screen-{Guid.NewGuid():N}.png");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(CaptureExecutable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-x");
            process.StartInfo.ArgumentList.Add(path);
            if (!process.Start()) return null;
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(path)) return null;

            await using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var primary = lifetime?.MainWindow?.Screens.Primary;
            var scale = primary?.Scaling ?? 1d;
            PixelRect bounds = primary is null
                ? new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height)
                : new PixelRect(
                    0,
                    0,
                    Math.Max(bitmap.PixelSize.Width, (int)Math.Round(primary.Bounds.Width * scale)),
                    Math.Max(bitmap.PixelSize.Height, (int)Math.Round(primary.Bounds.Height * scale)));
            return new CapturedScreen(bitmap, bounds, scale);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CleanOldFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "screen-*.png"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
