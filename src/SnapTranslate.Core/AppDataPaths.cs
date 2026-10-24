namespace SnapTranslate;

public static class AppDataPaths
{
    public const string ApplicationName = "SnapTranslate";

    public static string ApplicationDataDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(root, ApplicationName);
        }
    }

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ExpandUserPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                 || expanded.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        return Path.GetFullPath(expanded);
    }
}
