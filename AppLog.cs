using System.IO;
using System.Text;

namespace SnapTranslate;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SnapTranslate",
        "logs");

    public static void Error(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"SnapTranslate-{DateTime.Now:yyyyMMdd}.log");
            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [ERROR] ")
                .AppendLine(context)
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            lock (Sync)
                File.AppendAllText(path, entry, Encoding.UTF8);
        }
        catch
        {
            // Logging must never replace the original failure.
        }
    }
}
