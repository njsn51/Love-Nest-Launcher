using System.IO;
using LoveNestLauncher.Common;

namespace LoveNestLauncher.Models;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = AppPaths.DefaultDownloadDirectory;
    public int MaxConcurrentDownloads { get; set; } = 2;
    public bool AutoExtract { get; set; } = true;
    public bool EnableAcrylic { get; set; } = true;
    public string GameExecutablePath { get; set; } = string.Empty;

    public static AppSettings CreateDefault() => new();

    public AppSettings Normalize()
    {
        if (string.IsNullOrWhiteSpace(DownloadDirectory) || !Path.IsPathFullyQualified(DownloadDirectory))
        {
            DownloadDirectory = AppPaths.DefaultDownloadDirectory;
        }

        DownloadDirectory = Path.GetFullPath(DownloadDirectory.Trim());
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 3);
        GameExecutablePath = GameExecutablePath?.Trim() ?? string.Empty;
        if (GameExecutablePath.Length > 0)
        {
            try
            {
                GameExecutablePath = Path.GetFullPath(GameExecutablePath);
            }
            catch
            {
                GameExecutablePath = string.Empty;
            }
        }

        return this;
    }
}
