using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LoveNestLauncher.Common;
using LoveNestLauncher.Models;
using LoveNestLauncher.Services;
using LoveNestLauncher.ViewModels;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
var artifactRoot = Path.Combine(root, "artifacts", "engine-test");
if (Directory.Exists(artifactRoot))
{
    Directory.Delete(artifactRoot, recursive: true);
}

Directory.CreateDirectory(artifactRoot);
AppPaths.Initialize([$"--data-dir={Path.Combine(artifactRoot, "data")}"]);
LogService.Initialize();

var failures = new List<string>();
var checks = 0;

void Check(bool condition, string message)
{
    checks++;
    if (condition)
    {
        Console.WriteLine($"PASS  {message}");
    }
    else
    {
        failures.Add(message);
        Console.WriteLine($"FAIL  {message}");
    }
}

// 1. Source parser against the real bundled download source.
var sourcePath = Path.Combine(root, "src", "LoveNestLauncher", "Assets", "download-sources.txt");
var sourceItems = SourceService.Parse(File.ReadAllText(sourcePath));
Check(sourceItems.Count == 10, $"download source parses 10 entries (actual {sourceItems.Count})");
Check(sourceItems.All(item => item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)), "all source URLs are HTTP(S)");
Check(sourceItems.All(item => !string.IsNullOrWhiteSpace(item.FileName) && item.FileType is "RAR" or "ZIP"),
    "all source files have a safe name and archive type");
Check(sourceItems.Any(item => item.Category == "二创"), "二创 category detected");
Check(sourceItems.Any(item => item.Category == "测试"), "测试 category detected");
Check(sourceItems.Any(item => item.Category == "展会"), "展会 category detected");

// 2. Settings round-trip.
var settingsService = new SettingsService();
var downloadDirectory = Path.Combine(artifactRoot, "downloads");
var savedSettings = new AppSettings
{
    DownloadDirectory = downloadDirectory,
    MaxConcurrentDownloads = 3,
    AutoExtract = false,
    EnableAcrylic = false
};
settingsService.Save(savedSettings);
var loadedSettings = settingsService.Load();
Check(loadedSettings.DownloadDirectory == downloadDirectory, "settings download path round-trip");
Check(loadedSettings.MaxConcurrentDownloads == 3, "settings concurrency round-trip");
Check(!loadedSettings.AutoExtract && !loadedSettings.EnableAcrylic, "settings toggles round-trip");

// 3. ZIP archive extraction.
var archiveService = new ArchiveService();
var zipPath = Path.Combine(artifactRoot, "sample.zip");
using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
{
    var entry = zip.CreateEntry("nested/readme.txt");
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write("Love Nest test archive");
}
var extractedDirectory = await archiveService.ExtractAsync(zipPath);
Check(File.Exists(Path.Combine(extractedDirectory, "nested", "readme.txt")), "ZIP extraction writes nested file");
Check(new FileInfo(Path.Combine(extractedDirectory, "nested", "readme.txt")).Length > 0, "ZIP extracted content is not empty");
Console.WriteLine($"INFO  RAR extractor: {archiveService.ExtractionToolName}");

// 4. Real ranged HTTP download, including pause and resume.
var payload = new byte[8 * 1024 * 1024];
new Random(20261020).NextBytes(payload);
var expectedHash = Convert.ToHexString(SHA256.HashData(payload));
await using var server = new TestHttpServer(payload, throttleMilliseconds: 18, chunkSize: 32 * 1024);
var url = $"http://127.0.0.1:{server.Port}/test-payload.rar";
var definition = new DownloadDefinition(
    1, "SmokeTest 爱之巢", url, "smoke-test.rar", "RAR", "测试", "Engine smoke test");
var item = new DownloadItem(definition);
var manager = new DownloadManager { MaxConcurrent = 1 };
var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
manager.DownloadCompleted += (_, args) => completed.TrySetResult(args.FilePath);

manager.Enqueue(item, downloadDirectory, openFolderAfterDownload: false);
await WaitUntilAsync(() => item.DownloadedBytes >= 900 * 1024, TimeSpan.FromSeconds(15));
manager.Pause(item);
await WaitUntilAsync(() => item.State == DownloadState.Paused, TimeSpan.FromSeconds(15));
var partialPath = Path.Combine(downloadDirectory, item.FileName) + ".lnlpart";
Check(File.Exists(partialPath), "paused download keeps a resumable .lnlpart file");
Check(new FileInfo(partialPath).Length > 0, "paused partial file contains data");

manager.Enqueue(item, downloadDirectory, openFolderAfterDownload: false);
var completedPath = await completed.Task.WaitAsync(TimeSpan.FromSeconds(40));
Check(File.Exists(completedPath), "download completes and moves the final file into place");
Check(!File.Exists(partialPath), "temporary file removed after completion");
var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(completedPath)));
Check(actualHash == expectedHash, "downloaded file SHA-256 matches source");
Check(item.State == DownloadState.Completed, "download item reports Completed");
Check(item.TotalBytes == payload.Length, "download total byte count is correct");

manager.Dispose();

// 5. Failed download reports a friendly failure state.
await using var failingServer = new TestHttpServer([], statusCode: 404);
var failingDefinition = new DownloadDefinition(
    2, "Missing archive", $"http://127.0.0.1:{failingServer.Port}/missing.rar", "missing.rar", "RAR", "测试", "404 case");
var failingItem = new DownloadItem(failingDefinition);
using var failingManager = new DownloadManager();
failingManager.Enqueue(failingItem, Path.Combine(downloadDirectory, "errors"), false);
await WaitUntilAsync(() => failingItem.State == DownloadState.Failed, TimeSpan.FromSeconds(15));
Check(failingItem.State == DownloadState.Failed, "HTTP 404 sets Failed state");
Check(failingItem.StatusDetailText.Contains("404"), "HTTP 404 failure text is friendly and explicit");

// 6. Game executable detection and single-instance launch guard.
foreach (var stale in Process.GetProcessesByName("LoveNestTestGame"))
{
    stale.Kill(entireProcessTree: true);
    stale.Dispose();
}

var gameService = new GameService();
var gameRoot = Path.Combine(artifactRoot, "game-detect");
var gameFolder = Path.Combine(gameRoot, "LoveNestDemo");
Directory.CreateDirectory(gameFolder);
var gameExecutable = Path.Combine(gameFolder, "LoveNestTestGame.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), gameExecutable);
var detectedGame = gameService.DetectExecutable(gameRoot);
Check(detectedGame == gameExecutable, "game executable is detected in an extracted folder");

var firstLaunch = gameService.Launch(gameExecutable);
Check(firstLaunch.Status == GameLaunchStatus.Launched, "game launch starts the executable");
await WaitUntilAsync(() => Process.GetProcessesByName("LoveNestTestGame").Length > 0, TimeSpan.FromSeconds(10));
var secondLaunch = gameService.Launch(gameExecutable);
Check(secondLaunch.Status == GameLaunchStatus.AlreadyRunning, "second game launch is blocked while running");
Check(Process.GetProcessesByName("LoveNestTestGame").Length == 1, "only one game process is running");

foreach (var gameProcess in Process.GetProcessesByName("LoveNestTestGame"))
{
    gameProcess.Kill(entireProcessTree: true);
    gameProcess.Dispose();
}

await WaitUntilAsync(() => Process.GetProcessesByName("LoveNestTestGame").Length == 0, TimeSpan.FromSeconds(10));
Check(!gameService.IsRunning(gameExecutable), "game process tracking clears after exit");

Console.WriteLine();
Console.WriteLine($"Checks: {checks}, failures: {failures.Count}");
if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }

    Environment.ExitCode = 1;
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var stopwatch = Stopwatch.StartNew();
    while (!condition())
    {
        if (stopwatch.Elapsed > timeout)
        {
            throw new TimeoutException("Timed out waiting for condition.");
        }

        await Task.Delay(50);
    }
}

sealed class TestHttpServer : IAsyncDisposable
{
    private readonly byte[] _payload;
    private readonly int _throttleMilliseconds;
    private readonly int _chunkSize;
    private readonly int _statusCode;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public TestHttpServer(byte[] payload, int throttleMilliseconds = 0, int chunkSize = 64 * 1024, int statusCode = 200)
    {
        _payload = payload;
        _throttleMilliseconds = throttleMilliseconds;
        _chunkSize = Math.Max(1024, chunkSize);
        _statusCode = statusCode;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            client.Dispose();
            return;
        }

        var parts = requestLine.Split(' ');
        var method = parts[0];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrWhiteSpace(line = await reader.ReadLineAsync()))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        if (_statusCode != 200)
        {
            var body = Encoding.UTF8.GetBytes("not found");
            await WriteHeaderAsync(stream, _statusCode, "Not Found", body.Length);
            if (method != "HEAD")
            {
                await stream.WriteAsync(body);
            }

            client.Dispose();
            return;
        }

        long start = 0;
        var isRange = false;
        if (headers.TryGetValue("Range", out var range)
            && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var raw = range[6..].Split('-')[0];
            if (long.TryParse(raw, out var parsed) && parsed >= 0 && parsed < _payload.Length)
            {
                start = parsed;
                isRange = true;
            }
        }

        var length = _payload.Length - (int)start;
        if (isRange)
        {
            await WriteHeaderAsync(
                stream,
                206,
                "Partial Content",
                length,
                $"bytes {start}-{_payload.Length - 1}/{_payload.Length}");
        }
        else
        {
            await WriteHeaderAsync(stream, 200, "OK", length);
        }

        if (method == "HEAD")
        {
            client.Dispose();
            return;
        }

        try
        {
            var offset = (int)start;
            while (offset < _payload.Length)
            {
                var count = Math.Min(_chunkSize, _payload.Length - offset);
                await stream.WriteAsync(_payload.AsMemory(offset, count), _cts.Token);
                await stream.FlushAsync(_cts.Token);
                offset += count;
                if (_throttleMilliseconds > 0)
                {
                    await Task.Delay(_throttleMilliseconds, _cts.Token);
                }
            }
        }
        catch (Exception)
        {
            // Client cancelled the download (pause test).
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task WriteHeaderAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        int contentLength,
        string? contentRange = null)
    {
        var builder = new StringBuilder();
        builder.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
        builder.Append("Server: LoveNestTest\r\n");
        builder.Append("Content-Type: application/octet-stream\r\n");
        builder.Append("Accept-Ranges: bytes\r\n");
        builder.Append($"Content-Length: {contentLength}\r\n");
        if (contentRange is not null)
        {
            builder.Append($"Content-Range: {contentRange}\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch
        {
            // Ignore shutdown errors.
        }

        _cts.Dispose();
    }
}
