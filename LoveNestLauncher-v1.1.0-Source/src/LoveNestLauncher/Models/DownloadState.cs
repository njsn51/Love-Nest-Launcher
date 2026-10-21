namespace LoveNestLauncher.Models;

public enum DownloadState
{
    Ready,
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed
}

public readonly record struct DownloadProgressInfo(long DownloadedBytes, long TotalBytes, double SpeedBytesPerSecond);
