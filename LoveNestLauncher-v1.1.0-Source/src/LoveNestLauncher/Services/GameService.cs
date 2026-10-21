using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LoveNestLauncher.Models;

namespace LoveNestLauncher.Services;

public sealed class GameService
{
    private static readonly string[] IgnoredDirectoryNames =
    [
        "redist", "_commonredist", "directx", "vcredist", "dotnet", "monobleedingedge",
        "notification_helper", "crashreport", "unitycrashhandler"
    ];

    private static readonly string[] IgnoredExecutableNames =
    [
        "notification_helper", "unitycrashhandler", "unitycrashhandler64", "unins000",
        "unins001", "uninstall", "uninst", "setup", "install", "vc_redist", "vcredist",
        "dxsetup", "crashreport", "crashhandler", "createdump", "7z", "7za", "ffmpeg"
    ];

    public GameLaunchResult Launch(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new GameLaunchResult(GameLaunchStatus.NotFound, "还没有选择游戏程序。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            return new GameLaunchResult(GameLaunchStatus.NotFound, "游戏路径无效。");
        }

        if (!File.Exists(fullPath))
        {
            return new GameLaunchResult(GameLaunchStatus.NotFound, "游戏程序不存在，可能已被移动或删除。");
        }

        if (IsRunning(fullPath) || FindAnyLoveNestProcess() is not null)
        {
            ActivateAnyLoveNest();
            Activate(fullPath);
            return new GameLaunchResult(GameLaunchStatus.AlreadyRunning, "爱之巢已经在运行，已尝试切换到游戏窗口。");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            };
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GameLaunchResult(GameLaunchStatus.Failed, "无法启动游戏程序。");
            }

            return new GameLaunchResult(GameLaunchStatus.Launched, "爱之巢已启动。");
        }
        catch (Exception exception)
        {
            return new GameLaunchResult(GameLaunchStatus.Failed, $"启动失败：{exception.Message}");
        }
    }

    public bool IsRunning(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            var processName = Path.GetFileNameWithoutExtension(fullPath);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (HasSameExecutable(process, fullPath))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Process inspection can fail if a process exits while it is enumerated.
        }

        return false;
    }

    public bool Activate(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            var processName = Path.GetFileNameWithoutExtension(fullPath);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (!HasSameExecutable(process, fullPath))
                {
                    process.Dispose();
                    continue;
                }

                using (process)
                {
                    return BringToFront(process);
                }
            }
        }
        catch
        {
            // Fall through to the generic lookup.
        }

        return ActivateAnyLoveNest();
    }

    public bool ActivateAnyLoveNest()
    {
        var process = FindAnyLoveNestProcess();
        if (process is null)
        {
            return false;
        }

        using (process)
        {
            return BringToFront(process);
        }
    }

    public string? DetectExecutable(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return null;
        }

        var candidates = new List<(string Path, int Score, int Depth, long Size)>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((rootDirectory, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            if (depth > 4)
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (IsIgnoredExecutable(fileName))
                    {
                        continue;
                    }

                    long size = 0;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Ignore inaccessible files.
                    }

                    candidates.Add((file, ScoreExecutable(file, depth), depth, size));
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(child);
                    if (IgnoredDirectoryNames.Any(ignored => name.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
            catch
            {
                // Ignore folders that cannot be read.
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Depth)
            .ThenByDescending(candidate => candidate.Size)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static bool IsIgnoredExecutable(string fileName)
        => IgnoredExecutableNames.Any(ignored =>
            fileName.Contains(ignored, StringComparison.OrdinalIgnoreCase));

    private static int ScoreExecutable(string path, int depth)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parentName = Path.GetFileName(Path.GetDirectoryName(path));
        var score = 1000 - depth * 100;

        if (ContainsNestKeyword(name))
        {
            score += 500;
        }

        if (ContainsNestKeyword(parentName))
        {
            score += 300;
        }

        if (name.Contains("game", StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        if (name.Contains("launcher", StringComparison.OrdinalIgnoreCase)
            || name.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        try
        {
            if (new FileInfo(path).Length > 1_000_000)
            {
                score += 20;
            }
        }
        catch
        {
            // Ignore inaccessible files.
        }

        return score;
    }

    private static bool ContainsNestKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("爱之巢", StringComparison.OrdinalIgnoreCase)
               || value.Contains("lovenest", StringComparison.OrdinalIgnoreCase)
               || value.Contains("love nest", StringComparison.OrdinalIgnoreCase)
               || value.Contains("love_nest", StringComparison.OrdinalIgnoreCase)
               || (value.Contains("love", StringComparison.OrdinalIgnoreCase)
                   && value.Contains("nest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSameExecutable(Process process, string fullPath)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath)
                   && string.Equals(Path.GetFullPath(processPath), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Process? FindAnyLoveNestProcess()
    {
        foreach (var process in Process.GetProcesses())
        {
            if (process.Id == Environment.ProcessId)
            {
                process.Dispose();
                continue;
            }

            try
            {
                var processName = process.ProcessName;
                if (processName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                {
                    process.Dispose();
                    continue;
                }

                if (processName.Contains("爱之巢", StringComparison.OrdinalIgnoreCase)
                    || processName.Contains("LoveNest", StringComparison.OrdinalIgnoreCase)
                    || processName.Contains("Love Nest", StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static bool BringToFront(Process process)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, ShowWindowRestore);
                    SetForegroundWindow(handle);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            Thread.Sleep(120);
        }

        return false;
    }

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
