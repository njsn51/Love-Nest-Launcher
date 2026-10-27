using System.IO;
using System.Text;
using LoveNestLauncher.Common;

namespace LoveNestLauncher.Services;

public static class LogService
{
    private static readonly object SyncRoot = new();

    public static void Initialize()
    {
        try
        {
            var file = new FileInfo(AppPaths.LogFile);
            if (file.Exists && file.Length > 2 * 1024 * 1024)
            {
                var backup = AppPaths.LogFile + ".old";
                File.Copy(AppPaths.LogFile, backup, overwrite: true);
                File.Delete(AppPaths.LogFile);
            }
        }
        catch
        {
            // Logging must never prevent the launcher from starting.
        }

        Info($"Love Nest Launcher v{AppInfo.Version} started. Data directory: {AppPaths.DataDirectory}");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string context, Exception exception)
        => Write("ERROR", $"{context}: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");

    public static string ReadTail(int maxCharacters = 30000)
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
            {
                return "日志为空。";
            }

            var text = File.ReadAllText(AppPaths.LogFile, Encoding.UTF8);
            return text.Length <= maxCharacters ? text : text[^maxCharacters..];
        }
        catch (Exception exception)
        {
            return $"无法读取日志：{exception.Message}";
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.AppendAllText(
                    AppPaths.LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never prevent the launcher from working.
        }
    }
}
