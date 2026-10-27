using System.IO;
using System.Windows.Media;
using LoveNestLauncher.Common;
using LoveNestLauncher.Models;

namespace LoveNestLauncher.ViewModels;

public sealed class DownloadItem : ObservableObject
{
    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    private DownloadState _state = DownloadState.Ready;
    private long _downloadedBytes;
    private long _totalBytes;
    private double _speedBytesPerSecond;
    private string? _localPath;
    private string? _errorMessage;
    private string? _extractDirectory;
    private bool _isExtracting;
    private double _extractProgress;
    private bool _hasPartialDownload;
    private int _queuePosition;

    public DownloadItem(DownloadDefinition definition)
    {
        Definition = definition;
    }

    public DownloadDefinition Definition { get; }

    public string Name => Definition.Name;
    public string Url => Definition.Url;
    public string FileName => Definition.FileName;
    public string FileType => Definition.FileType;
    public string Category => Definition.Category;
    public string Description => Definition.Description;
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "未知来源";
    public int Sequence => Definition.Index;

    public DownloadState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public int QueuePosition
    {
        get => _queuePosition;
        private set => SetProperty(ref _queuePosition, value);
    }

    public long DownloadedBytes => _downloadedBytes;
    public long TotalBytes => _totalBytes;
    public double Progress => _totalBytes > 0
        ? Math.Clamp(_downloadedBytes * 100d / _totalBytes, 0, 100)
        : 0;

    public bool IsReady => State == DownloadState.Ready;
    public bool IsQueued => State == DownloadState.Queued;
    public bool IsDownloading => State == DownloadState.Downloading;
    public bool IsPaused => State == DownloadState.Paused;
    public bool IsCompleted => State == DownloadState.Completed;
    public bool IsFailed => State == DownloadState.Failed;
    public bool IsBusy => IsDownloading || IsQueued;

    public bool IsProgressVisible => (IsDownloading || IsPaused || IsFailed) && (_downloadedBytes > 0 || IsDownloading);
    public bool IsCancelVisible => IsDownloading || IsQueued || IsPaused;
    public bool IsOpenFolderVisible => IsCompleted;
    public bool IsCopyVisible => true;
    public bool ShowStatusLine => IsExtracting || State != DownloadState.Ready;
    public bool IsArchive => FileType is "RAR" or "ZIP";
    public bool CanExtract => IsCompleted && IsArchive && !_isExtracting;
    public bool IsExtractVisible => IsCompleted && IsArchive;
    public bool IsExtracting
    {
        get => _isExtracting;
        private set
        {
            if (SetProperty(ref _isExtracting, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public double ExtractProgress
    {
        get => _extractProgress;
        private set => SetProperty(ref _extractProgress, value);
    }

    public bool IsExtractProgressVisible => _isExtracting;

    public string? LocalPath
    {
        get => _localPath;
        private set => SetProperty(ref _localPath, value);
    }

    public string? ExtractDirectory
    {
        get => _extractDirectory;
        private set
        {
            if (SetProperty(ref _extractDirectory, value))
            {
                OnPropertiesChanged(nameof(ExtractButtonText));
            }
        }
    }

    public string ExtractButtonText => _isExtracting ? "正在解压…" : ExtractDirectory is not null ? "打开解压目录" : "解压";

    public string StatusText => _isExtracting
        ? "正在解压"
        : State switch
        {
            DownloadState.Queued => $"排队中 · 第 {Math.Max(1, QueuePosition)} 位",
            DownloadState.Downloading => "正在下载",
            DownloadState.Paused => "已暂停",
            DownloadState.Completed => ExtractDirectory is not null ? "已解压" : "已下载",
            DownloadState.Failed => "下载失败",
            _ => _hasPartialDownload ? "可继续下载" : "等待下载"
        };

    public string StatusDetailText
    {
        get
        {
            if (_isExtracting)
            {
                return $"正在解压到本地 · {_extractProgress:0}%";
            }

            if (!string.IsNullOrWhiteSpace(_errorMessage) && State == DownloadState.Failed)
            {
                return _errorMessage;
            }

            if (State == DownloadState.Completed && ExtractDirectory is not null)
            {
                return "已解压到本地";
            }

            return State switch
            {
                DownloadState.Downloading => BuildDownloadingDetail(),
                DownloadState.Paused => $"{DownloadedAndTotal()} · 已暂停",
                DownloadState.Queued => "正在等待空闲下载任务",
                DownloadState.Completed => $"{DownloadedAndTotal()} · 已保存到下载目录",
                DownloadState.Failed => "请检查网络后重试",
                _ => _hasPartialDownload
                    ? $"{DownloadedAndTotal()} · 可断点续传"
                    : "支持断点续传"
            };
        }
    }

    public string PrimaryActionText => _isExtracting
        ? "正在解压…"
        : State switch
        {
            DownloadState.Completed => ExtractDirectory is not null ? "打开目录" : IsArchive ? "解压" : "打开文件",
            DownloadState.Downloading => "暂停下载",
            DownloadState.Queued => "取消排队",
            DownloadState.Paused => "继续下载",
            DownloadState.Failed => "重试下载",
            _ => _hasPartialDownload ? "继续下载" : "立即下载"
        };

    public string PrimaryActionGlyph => _isExtracting
        ? "\uE895"
        : State switch
        {
            DownloadState.Completed => ExtractDirectory is not null ? "\uE838" : IsArchive ? "\uE7B8" : "\uE8E5",
            DownloadState.Downloading => "\uE769",
            DownloadState.Queued => "\uE711",
            DownloadState.Paused or DownloadState.Failed => "\uE72C",
            _ => "\uE896"
        };

    public bool CanPrimaryAction => !_isExtracting;
    public string SizeText => _totalBytes > 0 ? Formatting.Bytes(_totalBytes) : "大小待获取";
    public string SpeedText => State == DownloadState.Downloading && _speedBytesPerSecond > 0
        ? Formatting.Speed(_speedBytesPerSecond)
        : string.Empty;

    public string EtaText => State == DownloadState.Downloading && _totalBytes > _downloadedBytes
        ? Formatting.Eta(_totalBytes - _downloadedBytes, _speedBytesPerSecond)
        : string.Empty;

    public string MetaText => $"{FileType} · {SizeText}";
    public string NumberLabel => $"#{Sequence:00}";

    public Brush StatusBrush => GetBrush(State switch
    {
        DownloadState.Downloading => "#F05087",
        DownloadState.Completed => "#2FA676",
        DownloadState.Failed => "#E04C63",
        DownloadState.Paused or DownloadState.Queued => "#D78B35",
        _ => "#A1939C"
    });

    public Brush TagBackground => Category switch
    {
        "二创" => GetBrush("#FFF1E4"),
        "测试" => GetBrush("#FFEFF2"),
        "展会" => GetBrush("#EBF5FE"),
        _ => GetBrush("#F1EDFF")
    };

    public Brush TagForeground => Category switch
    {
        "二创" => GetBrush("#A96422"),
        "测试" => GetBrush("#C43C56"),
        "展会" => GetBrush("#2877AD"),
        _ => GetBrush("#6B55BE")
    };

    public Brush FileTypeBackground => FileType switch
    {
        "ZIP" => GetBrush("#E8F8F3"),
        "RAR" => GetBrush("#FBEDF5"),
        _ => GetBrush("#F2F0F6")
    };

    public Brush FileTypeForeground => FileType switch
    {
        "ZIP" => GetBrush("#23856B"),
        "RAR" => GetBrush("#B33D84"),
        _ => GetBrush("#6F6577")
    };

    public void SetQueued(int position)
    {
        QueuePosition = position;
        _errorMessage = null;
        State = DownloadState.Queued;
    }

    public void BeginDownload()
    {
        _errorMessage = null;
        _speedBytesPerSecond = 0;
        State = DownloadState.Downloading;
        OnAllPropertiesChanged();
    }

    public void ApplyProgress(DownloadProgressInfo info)
    {
        _downloadedBytes = info.DownloadedBytes;
        if (info.TotalBytes > 0)
        {
            _totalBytes = info.TotalBytes;
        }

        _speedBytesPerSecond = info.SpeedBytesPerSecond;
        _hasPartialDownload = _downloadedBytes > 0;
        OnPropertiesChanged(
            nameof(DownloadedBytes),
            nameof(TotalBytes),
            nameof(Progress),
            nameof(StatusDetailText),
            nameof(SizeText),
            nameof(SpeedText),
            nameof(EtaText),
            nameof(MetaText),
            nameof(IsProgressVisible));
    }

    public void MarkPaused()
    {
        _speedBytesPerSecond = 0;
        State = DownloadState.Paused;
    }

    public void MarkReady(string message = "")
    {
        _speedBytesPerSecond = 0;
        _errorMessage = null;
        State = DownloadState.Ready;
    }

    public void MarkFailed(string message)
    {
        _speedBytesPerSecond = 0;
        _errorMessage = message;
        State = DownloadState.Failed;
    }

    public void MarkCompleted(string path)
    {
        LocalPath = path;
        _totalBytes = File.Exists(path) ? new FileInfo(path).Length : _totalBytes;
        _downloadedBytes = _totalBytes;
        _speedBytesPerSecond = 0;
        _errorMessage = null;
        _hasPartialDownload = false;
        State = DownloadState.Completed;
        OnPropertiesChanged(nameof(Progress), nameof(DownloadedBytes), nameof(TotalBytes), nameof(SizeText));
    }

    public void SetRemoteSize(long length)
    {
        if (length <= 0 || _totalBytes > 0)
        {
            return;
        }

        _totalBytes = length;
        OnPropertiesChanged(nameof(TotalBytes), nameof(SizeText), nameof(MetaText));
    }

    public void RefreshLocalState(string downloadDirectory)
    {
        LocalPath = Path.Combine(downloadDirectory, FileName);

        if (State is DownloadState.Downloading or DownloadState.Queued)
        {
            return;
        }

        if (File.Exists(LocalPath))
        {
            MarkCompleted(LocalPath);
            return;
        }

        var partialPath = LocalPath + ".lnlpart";
        if (File.Exists(partialPath))
        {
            var length = new FileInfo(partialPath).Length;
            _downloadedBytes = length;
            _hasPartialDownload = length > 0;
        }
        else
        {
            _downloadedBytes = 0;
            _hasPartialDownload = false;
        }

        if (State == DownloadState.Completed)
        {
            State = DownloadState.Ready;
        }
        else
        {
            OnAllPropertiesChanged();
        }
    }

    public void BeginExtract()
    {
        _isExtracting = true;
        _extractProgress = 0;
        OnAllPropertiesChanged();
    }

    public void ReportExtractProgress(double percent)
    {
        _extractProgress = Math.Clamp(percent, 0, 100);
        OnPropertiesChanged(nameof(ExtractProgress), nameof(StatusDetailText));
    }

    public void CompleteExtract(string directory)
    {
        _isExtracting = false;
        _extractProgress = 100;
        ExtractDirectory = directory;
        OnAllPropertiesChanged();
    }

    public void FailExtract()
    {
        _isExtracting = false;
        _extractProgress = 0;
        OnAllPropertiesChanged();
    }

    public void ClearLocalCompletion()
    {
        if (State == DownloadState.Completed && (LocalPath is null || !File.Exists(LocalPath)))
        {
            State = DownloadState.Ready;
        }
    }

    private string DownloadedAndTotal()
        => _totalBytes > 0
            ? $"{Formatting.Bytes(_downloadedBytes)} / {Formatting.Bytes(_totalBytes)}"
            : Formatting.Bytes(_downloadedBytes);

    private string BuildDownloadingDetail()
    {
        var speed = _speedBytesPerSecond > 0 ? Formatting.Speed(_speedBytesPerSecond) : "连接中…";
        if (_totalBytes <= 0)
        {
            return $"已下载 {Formatting.Bytes(_downloadedBytes)} · {speed}";
        }

        var eta = Formatting.Eta(_totalBytes - _downloadedBytes, _speedBytesPerSecond);
        var tail = string.IsNullOrEmpty(eta) ? string.Empty : $" · {eta}";
        return $"{DownloadedAndTotal()} · {speed}{tail}";
    }

    private static Brush GetBrush(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var brush))
        {
            return brush;
        }

        var converted = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        converted.Freeze();
        BrushCache[hex] = converted;
        return converted;
    }
}
