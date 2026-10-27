using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using LoveNestLauncher.Common;
using LoveNestLauncher.Models;

namespace LoveNestLauncher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            return AppSettings.CreateDefault().Normalize();
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? AppSettings.CreateDefault();
            return settings.Normalize();
        }
        catch (Exception exception)
        {
            LogService.Error("读取设置失败，已回退到默认设置", exception);
            TryBackupBrokenSettings();
            return AppSettings.CreateDefault().Normalize();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(settings.DownloadDirectory);
        Directory.CreateDirectory(AppPaths.DataDirectory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temporaryFile = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporaryFile, json);
        File.Move(temporaryFile, AppPaths.SettingsFile, overwrite: true);
        LogService.Info($"设置已保存，下载目录：{settings.DownloadDirectory}");
    }

    private static void TryBackupBrokenSettings()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", overwrite: true);
            }
        }
        catch
        {
            // Ignore backup errors.
        }
    }
}
