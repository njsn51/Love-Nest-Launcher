using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using LoveNestLauncher.Common;

namespace LoveNestLauncher.Services;

public sealed class ArchiveService
{
    private static readonly string[] SevenZipCandidates =
    [
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe"
    ];

    private static readonly string[] WinRarCandidates =
    [
        @"C:\Program Files\WinRAR\WinRAR.exe",
        @"C:\Program Files (x86)\WinRAR\WinRAR.exe"
    ];

    public bool HasRarExtractor => FindExecutable(SevenZipCandidates, "7z.exe") is not null
                                   || FindExecutable(WinRarCandidates, "WinRAR.exe") is not null;

    public string ExtractionToolName => FindExecutable(SevenZipCandidates, "7z.exe") is not null
        ? "7-Zip"
        : FindExecutable(WinRarCandidates, "WinRAR.exe") is not null
            ? "WinRAR"
            : "未检测到";

    public async Task<string> ExtractAsync(
        string archivePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("压缩包不存在，可能已被移动或删除。", archivePath);
        }

        var destination = BuildDestination(archivePath);
        Directory.CreateDirectory(destination);
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();

        if (extension == ".zip")
        {
            await Task.Run(() => ExtractZip(archivePath, destination, progress, cancellationToken), cancellationToken);
        }
        else if (extension is ".rar" or ".7z")
        {
            await ExtractWithExternalToolAsync(archivePath, destination, cancellationToken);
            progress?.Report(100);
        }
        else
        {
            throw new NotSupportedException($"暂不支持解压 {extension} 格式。");
        }

        LogService.Info($"解压完成：{archivePath} -> {destination}");
        return destination;
    }

    private static async Task ExtractZip(
        string archivePath,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var totalBytes = Math.Max(1, archive.Entries.Sum(entry => entry.Length));
        long extractedBytes = 0;
        var buffer = new byte[128 * 1024];
        var lastReport = DateTime.UtcNow;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("压缩包包含不安全的文件路径，已停止解压。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var source = entry.Open();
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                extractedBytes += read;

                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 150)
                {
                    progress?.Report(extractedBytes * 100d / totalBytes);
                    lastReport = DateTime.UtcNow;
                }
            }
        }

        progress?.Report(100);
    }

    private async Task ExtractWithExternalToolAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var sevenZip = FindExecutable(SevenZipCandidates, "7z.exe");
        var winRar = FindExecutable(WinRarCandidates, "WinRAR.exe");

        ProcessStartInfo startInfo;
        if (sevenZip is not null)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = sevenZip,
                Arguments = $"x -y -bd -o\"{destination}\" \"{archivePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }
        else if (winRar is not null)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = winRar,
                Arguments = $"x -y -o+ \"{archivePath}\" \"{destination}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }
        else
        {
            throw new InvalidOperationException(
                "解压 RAR 需要安装 7-Zip 或 WinRAR。你仍然可以使用压缩包，或安装解压工具后重试。");
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动解压程序。");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have already exited.
            }

            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"解压工具返回错误（代码 {process.ExitCode}）：{detail.Trim()}");
        }
    }

    private static string BuildDestination(string archivePath)
    {
        var directory = Path.GetDirectoryName(archivePath)!;
        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        var destination = Path.Combine(directory, baseName);
        var suffix = 2;
        while (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            destination = Path.Combine(directory, $"{baseName} ({suffix++})");
        }

        return destination;
    }

    private static string? FindExecutable(IEnumerable<string> candidates, string executableName)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }
}
