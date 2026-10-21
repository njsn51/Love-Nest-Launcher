using System.IO;

namespace LoveNestLauncher.Common;

public static class AppPaths
{
    private static bool _initialized;

    public static string DataDirectory { get; private set; } = string.Empty;
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string CustomSourceFile => Path.Combine(DataDirectory, "download-sources.txt");
    public static string LogFile => Path.Combine(DataDirectory, "launcher.log");
    public static string TempDirectory => Path.Combine(DataDirectory, "temp");

    public static string DefaultDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LoveNestLauncher");

    public static void Initialize(IReadOnlyList<string> args)
    {
        if (_initialized)
        {
            return;
        }

        var dataOverride = args
            .FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1]
            .Trim('"');

        if (!string.IsNullOrWhiteSpace(dataOverride))
        {
            DataDirectory = Path.GetFullPath(dataOverride);
        }
        else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")))
        {
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        }
        else
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoveNestLauncher");
        }

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(TempDirectory);
        _initialized = true;
    }
}
