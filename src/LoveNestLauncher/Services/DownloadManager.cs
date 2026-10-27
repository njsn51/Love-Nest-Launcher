using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LoveNestLauncher.Common;
using LoveNestLauncher.Models;
using LoveNestLauncher.ViewModels;

namespace LoveNestLauncher.Services;

public sealed class DownloadManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Queue<DownloadJob> _queue = new();
    private readonly Dictionary<DownloadItem, DownloadJob> _active = new();
    private readonly object _syncRoot = new();
    private bool _disposed;
    private int _maxConcurrent = 2;

    public DownloadManager()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"LoveNestLauncher/{AppInfo.Version}");
    }

    public event EventHandler? StateChanged;
    public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;

    public int MaxConcurrent
    {
        get
        {
            lock (_syncRoot)
            {
                return _maxConcurrent;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                _maxConcurrent = Math.Clamp(value, 1, 3);
            }

            Pump();
        }
    }

    public int BusyCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _queue.Count + _active.Count;
            }
        }
    }

    public bool IsBusy(DownloadItem item)
    {
        lock (_syncRoot)
        {
            return _active.ContainsKey(item) || _queue.Any(job => ReferenceEquals(job.Item, item));
        }
    }

    public void Enqueue(DownloadItem item, string downloadDirectory, bool openFolderAfterDownload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            if (_active.ContainsKey(item) || _queue.Any(job => ReferenceEquals(job.Item, item)))
            {
                return;
            }
        }

        Directory.CreateDirectory(downloadDirectory);
        var job = new DownloadJob(item, downloadDirectory, openFolderAfterDownload);
        item.SetQueued(BusyCount + 1);
        LogService.Info($"加入下载队列：{item.Name} -> {downloadDirectory}");

        lock (_syncRoot)
        {
            _queue.Enqueue(job);
        }

        RaiseStateChanged();
        Pump();
    }

    public void Pause(DownloadItem item)
    {
        DownloadJob? job;
        var wasActive = false;
        lock (_syncRoot)
        {
            wasActive = _active.TryGetValue(item, out job);
            if (!wasActive)
            {
                job = RemoveQueuedJob(item);
            }
        }

        if (job is null)
        {
            return;
        }

        if (wasActive)
        {
            job.StopMode = DownloadStopMode.Pause;
            job.Cancellation.Cancel();
            LogService.Info($"暂停下载：{item.Name}");
        }
        else
        {
            job.Cancellation.Dispose();
            item.MarkPaused();
            LogService.Info($"取消排队并暂停：{item.Name}");
            RaiseStateChanged();
            Pump();
        }
    }

    public void Cancel(DownloadItem item)
    {
        DownloadJob? job;
        var wasActive = false;
        lock (_syncRoot)
        {
            wasActive = _active.TryGetValue(item, out job);
            if (!wasActive)
            {
                job = RemoveQueuedJob(item);
            }
        }

        if (job is null)
        {
            return;
        }

        if (wasActive)
        {
            job.StopMode = DownloadStopMode.Cancel;
            job.Cancellation.Cancel();
            LogService.Info($"取消下载：{item.Name}");
        }
        else
        {
            job.Cancellation.Dispose();
            DeletePartialFile(item, job.DownloadDirectory);
            item.MarkReady("下载已取消");
            LogService.Info($"取消排队：{item.Name}");
            RaiseStateChanged();
            Pump();
        }
    }

    public async Task ProbeSizesAsync(IEnumerable<DownloadItem> items, CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await ProbeSizeAsync(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    public async Task ProbeSizeAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        if (item.TotalBytes > 0)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Head, item.Url);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0 and var length)
            {
                item.SetRemoteSize(length);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Size detection is best-effort; the GET request will provide it later.
        }
        catch (Exception exception)
        {
            LogService.Warn($"获取文件大小失败（{item.Name}）：{exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<DownloadJob> jobs;
        lock (_syncRoot)
        {
            jobs = _active.Values.Concat(_queue).ToList();
            _active.Clear();
            _queue.Clear();
        }

        foreach (var job in jobs)
        {
            job.StopMode = DownloadStopMode.Pause;
            job.Cancellation.Cancel();
        }

        _httpClient.Dispose();
    }

    private void Pump()
    {
        List<DownloadJob> toStart = [];
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            while (_active.Count < _maxConcurrent && _queue.Count > 0)
            {
                var job = _queue.Dequeue();
                if (job.StopMode != DownloadStopMode.None)
                {
                    continue;
                }

                _active[job.Item] = job;
                toStart.Add(job);
            }

            var position = 1;
            foreach (var job in _queue)
            {
                job.Item.SetQueued(position++);
            }
        }

        foreach (var job in toStart)
        {
            job.Item.BeginDownload();
            _ = RunJobAsync(job);
        }

        if (toStart.Count > 0)
        {
            RaiseStateChanged();
        }
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        var item = job.Item;
        var finalPath = Path.Combine(job.DownloadDirectory, item.FileName);
        var partialPath = finalPath + ".lnlpart";
        IProgress<DownloadProgressInfo> progress = new Progress<DownloadProgressInfo>(item.ApplyProgress);

        try
        {
            Directory.CreateDirectory(job.DownloadDirectory);

            if (File.Exists(finalPath))
            {
                ReleaseActiveItem(item);
                item.MarkCompleted(finalPath);
                NotifyCompleted(item, finalPath, job);
                return;
            }

            var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            LogService.Info($"开始下载：{item.Name}，已有 {Formatting.Bytes(existingBytes)}，地址 {item.Url}");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                job.Cancellation.Token);

            if (existingBytes > 0
                && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                && item.TotalBytes > 0
                && existingBytes >= item.TotalBytes)
            {
                File.Move(partialPath, finalPath, overwrite: true);
                ReleaseActiveItem(item);
                item.MarkCompleted(finalPath);
                LogService.Info($"断点文件已完整，直接完成：{item.Name} -> {finalPath}");
                NotifyCompleted(item, finalPath, job);
                return;
            }

            if (existingBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                existingBytes = 0;
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }

            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            var totalBytes = contentLength is > 0 and var length ? existingBytes + length : item.TotalBytes;
            if (response.StatusCode == HttpStatusCode.OK && contentLength is > 0 and var fullLength)
            {
                totalBytes = fullLength;
            }

            progress.Report(new DownloadProgressInfo(existingBytes, totalBytes, 0));
            await using var source = await response.Content.ReadAsStreamAsync(job.Cancellation.Token);
            await using (var destination = new FileStream(
                             partialPath,
                             existingBytes > 0 ? FileMode.Append : FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             256 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    var downloadedBytes = existingBytes;
                    var stopwatch = Stopwatch.StartNew();
                    var lastReport = TimeSpan.Zero;
                    var lastBytes = existingBytes;
                    var smoothedSpeed = 0d;

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), job.Cancellation.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), job.Cancellation.Token);
                        downloadedBytes += read;

                        var now = stopwatch.Elapsed;
                        if ((now - lastReport).TotalMilliseconds >= 180)
                        {
                            var elapsed = Math.Max(0.001, (now - lastReport).TotalSeconds);
                            var currentSpeed = (downloadedBytes - lastBytes) / elapsed;
                            smoothedSpeed = smoothedSpeed <= 0
                                ? currentSpeed
                                : smoothedSpeed * 0.65 + currentSpeed * 0.35;
                            lastBytes = downloadedBytes;
                            lastReport = now;
                            progress.Report(new DownloadProgressInfo(downloadedBytes, totalBytes, smoothedSpeed));
                        }
                    }

                    await destination.FlushAsync(job.Cancellation.Token);
                    progress.Report(new DownloadProgressInfo(downloadedBytes, totalBytes, 0));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
                }
            }

            job.Cancellation.Token.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath, overwrite: true);
            ReleaseActiveItem(item);
            item.MarkCompleted(finalPath);
            LogService.Info($"下载完成：{item.Name} -> {finalPath}");
            NotifyCompleted(item, finalPath, job);
        }
        catch (OperationCanceledException)
        {
            ReleaseActiveItem(item);
            if (job.StopMode == DownloadStopMode.Cancel)
            {
                DeletePartialFile(item, job.DownloadDirectory);
                item.MarkReady("下载已取消");
                LogService.Info($"下载已取消并清理临时文件：{item.Name}");
            }
            else
            {
                item.MarkPaused();
                LogService.Info($"下载已暂停：{item.Name}");
            }
        }
        catch (Exception exception)
        {
            ReleaseActiveItem(item);
            if (job.StopMode == DownloadStopMode.Cancel)
            {
                DeletePartialFile(item, job.DownloadDirectory);
                item.MarkReady("下载已取消");
            }
            else
            {
                item.MarkFailed(BuildFriendlyError(exception));
                LogService.Error($"下载失败：{item.Name}", exception);
            }
        }
        finally
        {
            ReleaseActiveItem(item);
            job.Cancellation.Dispose();
            RaiseStateChanged();
            Pump();
        }
    }

    private void NotifyCompleted(DownloadItem item, string path, DownloadJob job)
    {
        DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(item, path));

        if (job.OpenFolderAfterDownload)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                LogService.Error("打开下载目录失败", exception);
            }
        }
    }

    private DownloadJob? RemoveQueuedJob(DownloadItem item)
    {
        // Caller holds _syncRoot.
        if (_queue.Count == 0)
        {
            return null;
        }

        DownloadJob? found = null;
        var remaining = new Queue<DownloadJob>();
        while (_queue.Count > 0)
        {
            var job = _queue.Dequeue();
            if (found is null && ReferenceEquals(job.Item, item))
            {
                found = job;
            }
            else
            {
                remaining.Enqueue(job);
            }
        }

        while (remaining.Count > 0)
        {
            _queue.Enqueue(remaining.Dequeue());
        }

        return found;
    }

    private static void DeletePartialFile(DownloadItem item, string directory)
    {
        try
        {
            var partialPath = Path.Combine(directory, item.FileName) + ".lnlpart";
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
        catch (Exception exception)
        {
            LogService.Error("删除未完成文件失败", exception);
        }
    }

    private static string BuildFriendlyError(Exception exception) => exception switch
    {
        HttpRequestException httpRequestException
            when httpRequestException.StatusCode is HttpStatusCode.NotFound
            => "下载源返回 404，文件可能已被移除。",
        HttpRequestException httpRequestException
            when httpRequestException.StatusCode is HttpStatusCode.Forbidden
            => "下载源拒绝了请求（403）。",
        HttpRequestException => $"网络请求失败：{exception.Message}",
        IOException => "写入文件失败，请检查下载目录权限和磁盘空间。",
        UnauthorizedAccessException => "没有权限写入下载目录，请在设置中更换路径。",
        _ => $"下载失败：{exception.Message}"
    };

    private void ReleaseActiveItem(DownloadItem item)
    {
        lock (_syncRoot)
        {
            _active.Remove(item);
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private enum DownloadStopMode
    {
        None,
        Pause,
        Cancel
    }

    private sealed class DownloadJob(
        DownloadItem item,
        string downloadDirectory,
        bool openFolderAfterDownload)
    {
        public DownloadItem Item { get; } = item;
        public string DownloadDirectory { get; } = downloadDirectory;
        public bool OpenFolderAfterDownload { get; } = openFolderAfterDownload;
        public CancellationTokenSource Cancellation { get; } = new();
        public DownloadStopMode StopMode { get; set; }
    }
}

public sealed class DownloadCompletedEventArgs(DownloadItem item, string filePath) : EventArgs
{
    public DownloadItem Item { get; } = item;
    public string FilePath { get; } = filePath;
}
