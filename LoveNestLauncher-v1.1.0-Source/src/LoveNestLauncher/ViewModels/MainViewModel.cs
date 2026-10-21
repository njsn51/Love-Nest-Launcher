using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LoveNestLauncher.Common;
using LoveNestLauncher.Models;
using LoveNestLauncher.Services;
using LoveNestLauncher.Views;
using Microsoft.Win32;

namespace LoveNestLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int PageHome = 0;
    private const int PageDownloads = 1;
    private const int PageSettings = 2;
    private const int PageAbout = 3;
    private const int PageMore = 4;

    private readonly SettingsService _settingsService;
    private readonly SourceService _sourceService;
    private readonly DownloadManager _downloadManager;
    private readonly ArchiveService _archiveService;
    private readonly GameService _gameService;
    private readonly ListCollectionView _versionsView;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _statusResetTimer;
    private readonly DispatcherTimer _gameStatusTimer;

    private Task? _initializationTask;
    private Task _sizeProbeTask = Task.CompletedTask;
    private AppSettings _settings = AppSettings.CreateDefault();
    private int _pageIndex;
    private string _searchText = string.Empty;
    private string _selectedFilter = "全部";
    private bool _isLoadingVersions;
    private string _statusText = "正在准备启动器…";
    private string _toastMessage = string.Empty;
    private bool _toastIsError;
    private bool _isToastVisible;
    private int _activeDownloadCount;
    private int _downloadedCount;
    private string _downloadDirectory = string.Empty;
    private int _maxConcurrentDownloads = 2;
    private bool _autoExtract = true;
    private bool _enableAcrylic = true;
    private bool _suppressSettingsDirty;
    private bool _isInitialized;
    private bool _isGameRunning;
    private bool _isLaunchingGame;

    public MainViewModel(
        SettingsService settingsService,
        SourceService sourceService,
        DownloadManager downloadManager,
        ArchiveService archiveService,
        GameService gameService)
    {
        _settingsService = settingsService;
        _sourceService = sourceService;
        _downloadManager = downloadManager;
        _archiveService = archiveService;
        _gameService = gameService;

        NavItems =
        [
            new NavItem { Index = PageHome, Title = "主页", Glyph = "\uE80F" },
            new NavItem { Index = PageDownloads, Title = "下载中心", Glyph = "\uE896" },
            new NavItem { Index = PageSettings, Title = "设置", Glyph = "\uE713" },
            new NavItem { Index = PageAbout, Title = "关于", Glyph = "\uE946" },
            new NavItem { Index = PageMore, Title = "其他", Glyph = "\uE712" }
        ];
        NavItems[PageHome].IsSelected = true;

        Filters = ["全部", "Demo", "展会", "二创", "测试"];
        Versions = [];
        FeaturedItems = [];
        _versionsView = new ListCollectionView(Versions) { Filter = FilterVersion };

        NavigateCommand = new RelayCommand(Navigate);
        BrowseDownloadFolderCommand = new RelayCommand(BrowseDownloadFolder);
        OpenDownloadFolderCommand = new RelayCommand(() => OpenDownloadFolder());
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        GamePrimaryActionCommand = new RelayCommand(GamePrimaryAction);
        SelectGameExecutableCommand = new RelayCommand(SelectGameExecutable);
        AutoDetectGameCommand = new RelayCommand(AutoDetectGameExecutable);
        OpenGameFolderCommand = new RelayCommand(OpenGameFolder);
        PrimaryActionCommand = new RelayCommand(parameter => PrimaryAction(parameter as DownloadItem));
        CancelDownloadCommand = new RelayCommand(parameter => CancelDownload(parameter as DownloadItem));
        OpenItemFolderCommand = new RelayCommand(parameter => OpenItemFolder(parameter as DownloadItem));
        CopyLinkCommand = new RelayCommand(parameter => CopyLink(parameter as DownloadItem));
        ExtractArchiveCommand = new RelayCommand(parameter => _ = ExtractArchiveAsync(parameter as DownloadItem, automatic: false));
        ImportSourceCommand = new RelayCommand(() => _ = ImportSourceAsync());
        ResetSourceCommand = new RelayCommand(() => _ = ResetSourceAsync());
        ReloadSourceCommand = new RelayCommand(() => _ = ReloadSourceAsync());
        RefreshSizesCommand = new RelayCommand(() => _ = RefreshSizesAsync());
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenLogCommand = new RelayCommand(OpenLog);
        OpenSourceFileCommand = new RelayCommand(() => _ = OpenSourceFileAsync());
        ClearTempCommand = new RelayCommand(ClearTemporaryFiles);
        SetConcurrencyCommand = new RelayCommand(SetConcurrentDownloads);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        _statusResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusResetTimer.Tick += (_, _) =>
        {
            _statusResetTimer.Stop();
            StatusText = "就绪";
        };

        _gameStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameStatusTimer.Tick += (_, _) => RefreshGameRunningState();

        _downloadManager.StateChanged += (_, _) => Dispatch(HandleManagerStateChanged);
        _downloadManager.DownloadCompleted += (_, args) => Dispatch(() => HandleDownloadCompleted(args));
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public ObservableCollection<DownloadItem> Versions { get; }
    public ObservableCollection<DownloadItem> FeaturedItems { get; }
    public ObservableCollection<string> Filters { get; }
    public ICollectionView VersionsView => _versionsView;

    public ICommand NavigateCommand { get; }
    public ICommand BrowseDownloadFolderCommand { get; }
    public ICommand OpenDownloadFolderCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand GamePrimaryActionCommand { get; }
    public ICommand SelectGameExecutableCommand { get; }
    public ICommand AutoDetectGameCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand PrimaryActionCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenItemFolderCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand ExtractArchiveCommand { get; }
    public ICommand ImportSourceCommand { get; }
    public ICommand ResetSourceCommand { get; }
    public ICommand ReloadSourceCommand { get; }
    public ICommand RefreshSizesCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenSourceFileCommand { get; }
    public ICommand ClearTempCommand { get; }
    public ICommand SetConcurrencyCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    public string AppName => AppInfo.Name;
    public string AppChineseName => AppInfo.ChineseName;
    public string AppVersion => AppInfo.VersionLabel;
    public string AppBuildDate => AppInfo.BuildDate;
    public string AppSlogan => AppInfo.Slogan;
    public string Author => AppInfo.Author;
    public string Designer => AppInfo.Designer;
    public string SourceProvider => AppInfo.SourceProvider;
    public string Copyright => AppInfo.Copyright;
    public string DataDirectoryDisplay => AppPaths.DataDirectory;
    public string ExtractionToolName => _archiveService.ExtractionToolName;
    public bool HasRarExtractor => _archiveService.HasRarExtractor;
    public string ExtractionSupportText => _archiveService.HasRarExtractor
        ? $"RAR / ZIP 可直接解压 · 已检测到 {ExtractionToolName}"
        : "ZIP 可直接解压 · RAR 需要安装 7-Zip 或 WinRAR";

    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (!SetProperty(ref _pageIndex, value))
            {
                return;
            }

            foreach (var navItem in NavItems)
            {
                navItem.IsSelected = navItem.Index == value;
            }

            LogService.Info($"页面切换：{value} / {CurrentPageTitle}");
            OnPropertiesChanged(
                nameof(CurrentPageTitle),
                nameof(IsHomePage),
                nameof(IsDownloadsPage),
                nameof(IsSettingsPage),
                nameof(IsAboutPage),
                nameof(IsMorePage));
        }
    }

    public string CurrentPageTitle => PageIndex switch
    {
        PageHome => "主页",
        PageDownloads => "下载中心",
        PageSettings => "设置",
        PageAbout => "关于",
        _ => "其他"
    };

    public bool IsHomePage => PageIndex == PageHome;
    public bool IsDownloadsPage => PageIndex == PageDownloads;
    public bool IsSettingsPage => PageIndex == PageSettings;
    public bool IsAboutPage => PageIndex == PageAbout;
    public bool IsMorePage => PageIndex == PageMore;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilter();
            }
        }
    }

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _selectedFilter, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set
        {
            if (SetProperty(ref _isLoadingVersions, value))
            {
                OnPropertiesChanged(
                    nameof(ShowNoResults),
                    nameof(NoResultsText),
                    nameof(SourceStatusText),
                    nameof(SourceStatusBrush));
            }
        }
    }

    public string VersionsCountText => $"{Versions.Count} 个版本";
    public string FilteredCountText => $"{_versionsView.Cast<object>().Count()} 个版本";
    public bool ShowNoResults => !IsLoadingVersions && _versionsView.Cast<object>().Count() == 0;
    public string NoResultsText => Versions.Count == 0 ? "下载源中暂时没有可用版本" : "没有找到匹配的版本，换个关键词试试";

    public int ActiveDownloadCount
    {
        get => _activeDownloadCount;
        private set
        {
            if (!SetProperty(ref _activeDownloadCount, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(ActiveDownloadSummary), nameof(HasActiveDownloads));
        }
    }

    public string ActiveDownloadSummary => ActiveDownloadCount > 0
        ? $"{ActiveDownloadCount} 个任务进行中"
        : "暂无下载任务";

    public bool HasActiveDownloads => ActiveDownloadCount > 0;

    public int DownloadedCount
    {
        get => _downloadedCount;
        private set
        {
            if (SetProperty(ref _downloadedCount, value))
            {
                OnPropertiesChanged(nameof(DownloadedSummary));
            }
        }
    }

    public string DownloadedSummary => $"已下载 {DownloadedCount} 个版本";

    public bool HasUnfinishedDownloads => Versions.Any(item =>
        item.IsDownloading || item.IsQueued || item.IsPaused || item.IsExtracting);

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set
        {
            if (SetProperty(ref _downloadDirectory, value))
            {
                OnPropertiesChanged(nameof(DownloadDirectoryDisplay), nameof(HasUnsavedSettings));
            }
        }
    }

    public string DownloadDirectoryDisplay => Formatting.ShortPath(DownloadDirectory, 54);
    public string SavedDownloadDirectory => _settings.DownloadDirectory;
    public string SavedDownloadDirectoryDisplay => Formatting.ShortPath(_settings.DownloadDirectory, 54);

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set
        {
            var clamped = Math.Clamp(value, 1, 3);
            if (!SetProperty(ref _maxConcurrentDownloads, clamped))
            {
                return;
            }

            OnPropertiesChanged(
                nameof(IsOneDownload),
                nameof(IsTwoDownloads),
                nameof(IsThreeDownloads),
                nameof(HasUnsavedSettings));
        }
    }

    public bool IsOneDownload
    {
        get => MaxConcurrentDownloads == 1;
        set
        {
            if (value)
            {
                MaxConcurrentDownloads = 1;
            }
        }
    }

    public bool IsTwoDownloads
    {
        get => MaxConcurrentDownloads == 2;
        set
        {
            if (value)
            {
                MaxConcurrentDownloads = 2;
            }
        }
    }

    public bool IsThreeDownloads
    {
        get => MaxConcurrentDownloads == 3;
        set
        {
            if (value)
            {
                MaxConcurrentDownloads = 3;
            }
        }
    }

    public bool AutoExtract
    {
        get => _autoExtract;
        set
        {
            if (SetProperty(ref _autoExtract, value))
            {
                OnPropertiesChanged(nameof(HasUnsavedSettings), nameof(AutoExtractHint));
            }
        }
    }

    public string AutoExtractHint => AutoExtract
        ? "下载完成后会自动解压"
        : "下载完成后需要手动解压";

    public bool EnableAcrylic
    {
        get => _enableAcrylic;
        set
        {
            if (!SetProperty(ref _enableAcrylic, value))
            {
                return;
            }

            _settings.EnableAcrylic = value;
            AcrylicPreferenceChanged?.Invoke(value);
            try
            {
                _settingsService.Save(_settings);
            }
            catch (Exception exception)
            {
                LogService.Error("保存亚克力外观设置失败", exception);
            }
        }
    }

    public event Action<bool>? AcrylicPreferenceChanged;

    public string GameExecutablePath => _settings.GameExecutablePath;
    public string GameExecutableDisplay => string.IsNullOrWhiteSpace(GameExecutablePath)
        ? "选择爱之巢的 EXE 文件"
        : Formatting.ShortPath(GameExecutablePath, 58);
    public string GameExecutableTooltip => string.IsNullOrWhiteSpace(GameExecutablePath)
        ? "还没有选择游戏程序"
        : GameExecutablePath;
    public bool HasGamePath => !string.IsNullOrWhiteSpace(GameExecutablePath) && File.Exists(GameExecutablePath);
    public bool IsGameRunning => _isGameRunning;
    public bool IsLaunchingGame => _isLaunchingGame;

    public string GameStatusText
    {
        get
        {
            if (_isLaunchingGame)
            {
                return "正在启动…";
            }

            if (string.IsNullOrWhiteSpace(GameExecutablePath))
            {
                return "尚未选择游戏程序";
            }

            if (!HasGamePath)
            {
                return "游戏程序不存在";
            }

            return _isGameRunning ? "爱之巢正在运行" : "游戏已就绪";
        }
    }

    public string GameButtonText
    {
        get
        {
            if (_isLaunchingGame)
            {
                return "启动中…";
            }

            if (string.IsNullOrWhiteSpace(GameExecutablePath))
            {
                return "选择游戏";
            }

            return _isGameRunning ? "返回游戏" : "启动游戏";
        }
    }

    public System.Windows.Media.Brush GameStatusBrush
    {
        get
        {
            if (_isLaunchingGame)
            {
                return BrushHelper.FromHex("#C08A36");
            }

            if (string.IsNullOrWhiteSpace(GameExecutablePath) || !HasGamePath)
            {
                return BrushHelper.FromHex("#A5969E");
            }

            return _isGameRunning ? BrushHelper.FromHex("#2FA676") : BrushHelper.FromHex("#F06092");
        }
    }

    public bool HasUnsavedSettings
    {
        get
        {
            if (_suppressSettingsDirty)
            {
                return false;
            }

            static string NormalizePath(string? value)
                => (value ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return !string.Equals(NormalizePath(DownloadDirectory), NormalizePath(_settings.DownloadDirectory), StringComparison.OrdinalIgnoreCase)
                   || MaxConcurrentDownloads != _settings.MaxConcurrentDownloads
                   || AutoExtract != _settings.AutoExtract;
        }
    }

    public string SourceDisplayName { get; private set; } = "内置下载源";
    public string SourceBadgeText => IsCustomSource ? "自定义" : "内置";
    public string SourcePathDisplay { get; private set; } = "随启动器打包";
    public bool IsCustomSource { get; private set; }
    public string SourceStatusText => IsLoadingVersions
        ? "正在读取下载源…"
        : $"{SourceDisplayName} · {Versions.Count} 个版本";

    public System.Windows.Media.Brush SourceStatusBrush => IsLoadingVersions
        ? BrushHelper.FromHex("#D78B35")
        : Versions.Count > 0
            ? BrushHelper.FromHex("#2FA676")
            : BrushHelper.FromHex("#E04C63");

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool ToastIsError
    {
        get => _toastIsError;
        private set => SetProperty(ref _toastIsError, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public bool IsInitialized => _isInitialized;

    public void SetDiagnosticStatus(string message) => SetStatus(message);

    public Task WaitForSizeProbeAsync() => _sizeProbeTask;

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    public string BuildExitPrompt()
    {
        if (!HasActiveDownloads && !HasUnfinishedDownloads && !HasUnsavedSettings)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        if (HasActiveDownloads)
        {
            lines.Add($"还有 {ActiveDownloadCount} 个下载任务正在进行。退出会中断任务，未完成进度会保留。");
        }
        else if (HasUnfinishedDownloads)
        {
            lines.Add("还有暂停中的下载或解压任务。退出会中断任务。");
        }

        if (HasUnsavedSettings)
        {
            lines.Add("下载设置有尚未保存的更改。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task InitializeCoreAsync()
    {
        _settings = _settingsService.Load();
        _downloadManager.MaxConcurrent = _settings.MaxConcurrentDownloads;
        ApplySettingsToUi(_settings);
        RefreshGameRunningState();
        _gameStatusTimer.Start();

        StatusText = "正在读取下载源…";
        await LoadVersionsAsync(showErrors: false);
        _isInitialized = true;
        OnPropertyChanged(nameof(IsInitialized));

        if (Versions.Count > 0)
        {
            StatusText = "就绪";
        }
        else
        {
            StatusText = "未能载入下载源，请前往「其他」检查";
        }
    }

    private async Task LoadVersionsAsync(bool showErrors)
    {
        IsLoadingVersions = true;
        OnPropertyChanged(nameof(SourceStatusText));

        try
        {
            var result = await _sourceService.LoadAsync();
            if (result.Items.Count == 0)
            {
                throw new InvalidDataException("下载源为空，或文件格式无法识别。");
            }

            Versions.Clear();
            FeaturedItems.Clear();
            foreach (var definition in result.Items)
            {
                var item = new DownloadItem(definition);
                item.RefreshLocalState(_settings.DownloadDirectory);
                Versions.Add(item);
            }

            foreach (var item in Versions.Reverse().Take(3))
            {
                FeaturedItems.Add(item);
            }

            IsCustomSource = result.IsCustom;
            SourceDisplayName = result.SourceLabel;
            SourcePathDisplay = result.SourcePath;
            OnPropertiesChanged(nameof(IsCustomSource), nameof(SourceBadgeText), nameof(SourceDisplayName), nameof(SourcePathDisplay));

            RefreshCounts();
            RefreshFilter();
            OnPropertiesChanged(nameof(VersionsCountText), nameof(SourceStatusText), nameof(SourceStatusBrush), nameof(NoResultsText));

            if (Versions.Count > 0)
            {
                StatusText = $"下载源已就绪 · {Versions.Count} 个版本";
                ResetStatusLater();
                _sizeProbeTask = ProbeSizesSafelyAsync(Versions.ToList());
            }
        }
        catch (Exception exception)
        {
            LogService.Error("载入下载源失败", exception);
            Versions.Clear();
            FeaturedItems.Clear();
            IsCustomSource = false;
            SourceDisplayName = "下载源载入失败";
            SourcePathDisplay = "请检查下载源文件";
            OnPropertiesChanged(nameof(SourceBadgeText), nameof(SourceDisplayName), nameof(SourcePathDisplay));
            RefreshCounts();
            RefreshFilter();
            OnPropertiesChanged(nameof(VersionsCountText), nameof(SourceStatusText), nameof(SourceStatusBrush), nameof(NoResultsText));
            StatusText = "下载源载入失败";

            if (showErrors)
            {
                AppDialog.ShowInfo(
                    "载入下载源失败",
                    $"启动器无法读取下载源。\n\n{exception.Message}\n\n你可以前往「其他」页面恢复内置下载源。",
                    "知道了",
                    isError: true);
            }
        }
        finally
        {
            IsLoadingVersions = false;
            OnPropertiesChanged(nameof(SourceStatusText), nameof(SourceStatusBrush), nameof(ShowNoResults), nameof(NoResultsText));
        }
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        _suppressSettingsDirty = true;
        try
        {
            DownloadDirectory = settings.DownloadDirectory;
            MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
            AutoExtract = settings.AutoExtract;
            _enableAcrylic = settings.EnableAcrylic;
        }
        finally
        {
            _suppressSettingsDirty = false;
        }

        OnPropertiesChanged(
            nameof(HasUnsavedSettings),
            nameof(SavedDownloadDirectory),
            nameof(SavedDownloadDirectoryDisplay),
            nameof(AutoExtractHint),
            nameof(EnableAcrylic),
            nameof(GameExecutablePath),
            nameof(GameExecutableDisplay),
            nameof(GameExecutableTooltip),
            nameof(HasGamePath),
            nameof(GameStatusText),
            nameof(GameButtonText),
            nameof(GameStatusBrush));
    }

    private void Navigate(object? parameter)
    {
        var index = parameter switch
        {
            NavItem navItem => navItem.Index,
            int value => value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => -1
        };

        if (index is >= PageHome and <= PageMore)
        {
            PageIndex = index;
        }
    }

    private void RefreshFilter()
    {
        _versionsView.Refresh();
        OnPropertiesChanged(nameof(FilteredCountText), nameof(ShowNoResults), nameof(NoResultsText));
    }

    private bool FilterVersion(object candidate)
    {
        if (candidate is not DownloadItem item)
        {
            return false;
        }

        if (!string.Equals(SelectedFilter, "全部", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.Category, SelectedFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.FileType.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void PrimaryAction(DownloadItem? item)
    {
        if (item is null)
        {
            return;
        }

        switch (item.State)
        {
            case DownloadState.Completed:
                if (item.ExtractDirectory is { } directory && Directory.Exists(directory))
                {
                    OpenPath(directory);
                }
                else if (item.IsArchive)
                {
                    _ = ExtractArchiveAsync(item, automatic: false);
                }
                else
                {
                    OpenItemFile(item);
                }

                break;
            case DownloadState.Downloading:
            case DownloadState.Queued:
                _downloadManager.Pause(item);
                SetStatus($"已暂停：{item.Name}");
                break;
            case DownloadState.Paused:
            case DownloadState.Ready:
            case DownloadState.Failed:
                StartDownload(item);
                break;
        }
    }

    private void StartDownload(DownloadItem item)
    {
        try
        {
            item.RefreshLocalState(_settings.DownloadDirectory);
            if (item.IsCompleted)
            {
                OpenItemFile(item);
                return;
            }

            Directory.CreateDirectory(_settings.DownloadDirectory);
            _downloadManager.Enqueue(item, _settings.DownloadDirectory, openFolderAfterDownload: false);
            SetStatus($"已加入下载队列：{item.Name}");
        }
        catch (Exception exception)
        {
            LogService.Error("启动下载失败", exception);
            AppDialog.ShowInfo("无法开始下载", exception.Message, "知道了", isError: true);
        }
    }

    private void CancelDownload(DownloadItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.State is DownloadState.Downloading || item.State == DownloadState.Paused)
        {
            var confirmed = AppDialog.Confirm(
                "取消下载",
                $"确定要取消「{item.Name}」吗？\n\n已下载的临时文件会被删除，此操作无法撤销。",
                "取消下载",
                "继续下载");
            if (!confirmed)
            {
                return;
            }
        }

        _downloadManager.Cancel(item);
        SetStatus($"已取消：{item.Name}");
    }

    private void OpenItemFile(DownloadItem? item)
    {
        if (item is null)
        {
            return;
        }

        var path = item.LocalPath ?? Path.Combine(_settings.DownloadDirectory, item.FileName);
        if (!File.Exists(path))
        {
            item.RefreshLocalState(_settings.DownloadDirectory);
            ShowToast("文件不存在，可能已被移动或删除", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            LogService.Error("打开文件失败", exception);
            AppDialog.ShowInfo("无法打开文件", exception.Message, "知道了", isError: true);
        }
    }

    private void OpenItemFolder(DownloadItem? item)
    {
        if (item is null)
        {
            return;
        }

        var path = item.LocalPath ?? Path.Combine(_settings.DownloadDirectory, item.FileName);
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                OpenPath(_settings.DownloadDirectory);
            }
        }
        catch (Exception exception)
        {
            LogService.Error("打开下载目录失败", exception);
            AppDialog.ShowInfo("无法打开目录", exception.Message, "知道了", isError: true);
        }
    }

    private void CopyLink(DownloadItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Url);
            ShowToast("下载链接已复制");
        }
        catch (Exception exception)
        {
            LogService.Error("复制下载链接失败", exception);
            ShowToast("复制失败，请稍后再试", isError: true);
        }
    }

    private async Task ExtractArchiveAsync(DownloadItem? item, bool automatic)
    {
        if (item is null || item.IsExtracting)
        {
            return;
        }

        if (item.ExtractDirectory is { } existingDirectory && Directory.Exists(existingDirectory))
        {
            OpenPath(existingDirectory);
            return;
        }

        var archivePath = item.LocalPath ?? Path.Combine(_settings.DownloadDirectory, item.FileName);
        if (!File.Exists(archivePath))
        {
            item.RefreshLocalState(_settings.DownloadDirectory);
            ShowToast("压缩包不存在，无法解压", isError: true);
            return;
        }

        if (item.FileType == "RAR" && !_archiveService.HasRarExtractor)
        {
            if (automatic)
            {
                ShowToast("下载完成，但未安装 7-Zip / WinRAR，无法自动解压", isError: true);
            }
            else
            {
                AppDialog.ShowInfo(
                    "缺少解压工具",
                    "解压 RAR 需要安装 7-Zip 或 WinRAR。安装后可以再次点击「解压」。",
                    "知道了");
            }

            return;
        }

        item.BeginExtract();
        SetStatus($"正在解压：{item.Name}");
        try
        {
            var progress = new Progress<double>(item.ReportExtractProgress);
            var destination = await _archiveService.ExtractAsync(archivePath, progress);
            item.CompleteExtract(destination);
            var detectedGame = DetectAndApplyGameExecutable(destination);
            if (automatic)
            {
                LogService.Info($"自动解压完成：{item.Name} -> {destination}，游戏程序：{detectedGame ?? "未识别"}");
                ShowToast(detectedGame is null
                    ? "下载完成，已自动解压"
                    : "下载完成，已自动解压并识别到游戏");
                SetStatus($"已自动解压：{item.Name}");
            }
            else
            {
                ShowToast($"解压完成：{item.Name}");
                SetStatus("解压完成");
                OpenPath(destination);
            }
        }
        catch (OperationCanceledException)
        {
            item.FailExtract();
            SetStatus("解压已取消");
        }
        catch (Exception exception)
        {
            item.FailExtract();
            LogService.Error($"{(automatic ? "自动" : "手动")}解压失败", exception);
            if (automatic)
            {
                ShowToast($"自动解压失败：{exception.Message}", isError: true);
            }
            else
            {
                AppDialog.ShowInfo("解压失败", exception.Message, "知道了", isError: true);
            }
        }
    }

    private void BrowseDownloadFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择爱之巢的下载保存位置",
            Multiselect = false
        };

        try
        {
            var initial = DownloadDirectory;
            if (Directory.Exists(initial))
            {
                dialog.InitialDirectory = initial;
            }
        }
        catch
        {
            // Ignore an invalid manually typed path and let the picker choose a default.
        }

        if (dialog.ShowDialog() == true)
        {
            DownloadDirectory = dialog.FolderName;
            SetStatus("已选择新的下载位置，点击保存设置后生效");
        }
    }

    private void SaveSettings()
    {
        var normalizedPath = DownloadDirectory.Trim();
        if (normalizedPath.Length == 0 || !Path.IsPathFullyQualified(normalizedPath))
        {
            AppDialog.ShowInfo("路径无效", "请选择一个完整的绝对路径作为下载目录。", "知道了", isError: true);
            return;
        }

        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch (Exception exception)
        {
            AppDialog.ShowInfo("路径无效", exception.Message, "知道了", isError: true);
            return;
        }

        var pathChanged = !string.Equals(
            normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            _settings.DownloadDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        if (pathChanged && HasUnfinishedDownloads)
        {
            AppDialog.ShowInfo(
                "下载任务尚未结束",
                "有正在下载、暂停或解压中的任务。请先完成或取消这些任务，再修改下载目录。",
                "知道了");
            return;
        }

        var newSettings = new AppSettings
        {
            DownloadDirectory = normalizedPath,
            MaxConcurrentDownloads = MaxConcurrentDownloads,
            AutoExtract = AutoExtract,
            EnableAcrylic = _settings.EnableAcrylic,
            GameExecutablePath = _settings.GameExecutablePath
        }.Normalize();

        try
        {
            _settingsService.Save(newSettings);
            _settings = newSettings;
            _downloadManager.MaxConcurrent = newSettings.MaxConcurrentDownloads;
            ApplySettingsToUi(newSettings);
            foreach (var item in Versions)
            {
                item.RefreshLocalState(newSettings.DownloadDirectory);
            }

            RefreshCounts();
            ShowToast("设置已保存");
            SetStatus("设置已保存");
        }
        catch (Exception exception)
        {
            LogService.Error("保存设置失败", exception);
            AppDialog.ShowInfo("保存设置失败", exception.Message, "知道了", isError: true);
        }
    }

    private void ResetSettings()
    {
        if (!AppDialog.Confirm(
                "恢复默认设置",
                "将下载位置、并发数和自动解压恢复为默认值。游戏路径与外观设置会保留。",
                "恢复默认",
                "取消"))
        {
            return;
        }

        if (HasUnfinishedDownloads)
        {
            AppDialog.ShowInfo(
                "下载任务尚未结束",
                "请先完成或取消当前下载任务，再恢复默认设置。",
                "知道了");
            return;
        }

        try
        {
            var defaults = AppSettings.CreateDefault().Normalize();
            defaults.GameExecutablePath = _settings.GameExecutablePath;
            defaults.EnableAcrylic = _settings.EnableAcrylic;
            _settingsService.Save(defaults);
            _settings = defaults;
            _downloadManager.MaxConcurrent = defaults.MaxConcurrentDownloads;
            ApplySettingsToUi(defaults);
            foreach (var item in Versions)
            {
                item.RefreshLocalState(defaults.DownloadDirectory);
            }

            RefreshCounts();
            ShowToast("已恢复默认设置");
            SetStatus("已恢复默认设置");
        }
        catch (Exception exception)
        {
            LogService.Error("恢复默认设置失败", exception);
            AppDialog.ShowInfo("操作失败", exception.Message, "知道了", isError: true);
        }
    }

    private void SetConcurrentDownloads(object? parameter)
    {
        if (parameter is int value)
        {
            MaxConcurrentDownloads = value;
        }
        else if (parameter is string text && int.TryParse(text, out var parsed))
        {
            MaxConcurrentDownloads = parsed;
        }
    }

    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedFilter = "全部";
    }

    private async Task ImportSourceAsync()
    {
        if (HasUnfinishedDownloads)
        {
            AppDialog.ShowInfo("暂时无法导入", "请先完成或取消当前所有下载任务，再导入新的下载源。", "知道了");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入下载源",
            Filter = "下载源文本 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var result = await _sourceService.ImportAsync(dialog.FileName);
            await LoadVersionsAsync(showErrors: true);
            ShowToast($"已导入 {result.Count} 个版本");
            SetStatus($"下载源已更新 · {result.Count} 个版本");
        }
        catch (Exception exception)
        {
            LogService.Error("导入下载源失败", exception);
            AppDialog.ShowInfo(
                "导入失败",
                $"无法导入这个下载源文件。\n\n{exception.Message}\n\n请确认每行格式为：名称    下载地址",
                "知道了",
                isError: true);
        }
    }

    private async Task ResetSourceAsync()
    {
        if (HasUnfinishedDownloads)
        {
            AppDialog.ShowInfo("暂时无法恢复", "请先完成或取消当前所有下载任务，再恢复内置下载源。", "知道了");
            return;
        }

        if (!_sourceService.HasCustomSource)
        {
            ShowToast("当前已经在使用内置下载源");
            return;
        }

        if (!AppDialog.Confirm("恢复内置下载源", "当前自定义下载源将被移除，下载列表会恢复为启动器内置版本。", "恢复内置", "取消"))
        {
            return;
        }

        try
        {
            _sourceService.ResetToBuiltIn();
            await LoadVersionsAsync(showErrors: true);
            ShowToast("已恢复内置下载源");
            SetStatus("已恢复内置下载源");
        }
        catch (Exception exception)
        {
            LogService.Error("恢复内置下载源失败", exception);
            AppDialog.ShowInfo("操作失败", exception.Message, "知道了", isError: true);
        }
    }

    private async Task ReloadSourceAsync()
    {
        if (HasUnfinishedDownloads)
        {
            AppDialog.ShowInfo("暂时无法刷新", "请先完成或取消当前所有下载任务，再刷新下载源。", "知道了");
            return;
        }

        await LoadVersionsAsync(showErrors: true);
    }

    private async Task RefreshSizesAsync()
    {
        if (Versions.Count == 0)
        {
            return;
        }

        SetStatus("正在刷新文件大小…");
        try
        {
            _sizeProbeTask = ProbeSizesSafelyAsync(Versions.ToList());
            await _sizeProbeTask;
            ShowToast("文件大小已刷新");
            SetStatus("文件大小已刷新");
        }
        catch (Exception exception)
        {
            LogService.Error("刷新文件大小失败", exception);
            ShowToast("刷新失败，请检查网络", isError: true);
        }
    }

    private async Task ProbeSizesSafelyAsync(IReadOnlyList<DownloadItem> items)
    {
        try
        {
            await _downloadManager.ProbeSizesAsync(items);
        }
        catch (Exception exception)
        {
            LogService.Warn($"获取版本文件大小失败：{exception.Message}");
        }
    }

    private void ClearTemporaryFiles()
    {
        if (_downloadManager.BusyCount > 0)
        {
            AppDialog.ShowInfo("暂时无法清理", "有下载任务正在进行或排队，请先暂停或取消这些任务。", "知道了");
            return;
        }

        var files = Versions
            .Select(item => Path.Combine(_settings.DownloadDirectory, item.FileName) + ".lnlpart")
            .Where(File.Exists)
            .ToList();

        if (files.Count == 0)
        {
            ShowToast("没有需要清理的临时文件");
            return;
        }

        if (!AppDialog.Confirm(
                "清理临时文件",
                $"将删除 {files.Count} 个未完成的下载文件，其中包含已暂停任务的断点进度。\n\n删除后无法恢复，确定继续吗？",
                "清理",
                "取消"))
        {
            return;
        }

        var deleted = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception exception)
            {
                LogService.Error($"删除临时文件失败：{file}", exception);
            }
        }

        foreach (var item in Versions)
        {
            item.RefreshLocalState(_settings.DownloadDirectory);
        }

        ShowToast($"已清理 {deleted} 个临时文件");
        SetStatus("临时文件已清理");
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);
            OpenPath(_settings.DownloadDirectory);
        }
        catch (Exception exception)
        {
            LogService.Error("打开下载目录失败", exception);
            AppDialog.ShowInfo("无法打开下载目录", exception.Message, "知道了", isError: true);
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            OpenPath(AppPaths.DataDirectory);
        }
        catch (Exception exception)
        {
            LogService.Error("打开数据目录失败", exception);
            AppDialog.ShowInfo("无法打开数据目录", exception.Message, "知道了", isError: true);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
            {
                ShowToast("暂时还没有运行日志");
                return;
            }

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.LogFile}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            LogService.Error("打开日志失败", exception);
            AppDialog.ShowInfo("无法打开日志", exception.Message, "知道了", isError: true);
        }
    }

    private async Task OpenSourceFileAsync()
    {
        try
        {
            var text = await _sourceService.ReadCurrentSourceTextAsync();
            var path = Path.Combine(AppPaths.TempDirectory, "当前下载源.txt");
            await File.WriteAllTextAsync(path, text);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            LogService.Error("查看下载源失败", exception);
            AppDialog.ShowInfo("无法查看下载源", exception.Message, "知道了", isError: true);
        }
    }

    private void GamePrimaryAction()
    {
        if (_isLaunchingGame)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(GameExecutablePath) || !HasGamePath)
        {
            LogService.Info($"游戏按钮：游戏路径不可用，打开选择窗口。路径={GameExecutablePath}");
            if (!string.IsNullOrWhiteSpace(GameExecutablePath))
            {
                ShowToast("游戏程序不存在，请重新选择", isError: true);
            }

            SelectGameExecutable();
            return;
        }

        if (_isGameRunning)
        {
            LogService.Info("游戏按钮：游戏已经在运行，尝试切换到游戏窗口。");
            if (_gameService.Activate(GameExecutablePath))
            {
                ShowToast("已切换到游戏窗口");
            }
            else
            {
                ShowToast("游戏正在运行，但没有找到它的窗口", isError: true);
            }

            RefreshGameRunningState();
            return;
        }

        SetLaunchingGame(true);
        try
        {
            LogService.Info($"游戏按钮：尝试启动 {GameExecutablePath}");
            var result = _gameService.Launch(GameExecutablePath);
            LogService.Info($"游戏启动结果：{result.Status}，{result.Message}");
            switch (result.Status)
            {
                case GameLaunchStatus.Launched:
                    _isGameRunning = true;
                    ShowToast("爱之巢已启动");
                    SetStatus("爱之巢已启动");
                    break;
                case GameLaunchStatus.AlreadyRunning:
                    _isGameRunning = true;
                    ShowToast(result.Message);
                    break;
                default:
                    ShowToast(result.Message, isError: true);
                    break;
            }
        }
        catch (Exception exception)
        {
            LogService.Error("启动游戏失败", exception);
            ShowToast($"启动失败：{exception.Message}", isError: true);
        }
        finally
        {
            SetLaunchingGame(false);
            RefreshGameRunningState();
        }
    }

    private void SetLaunchingGame(bool value)
    {
        if (_isLaunchingGame == value)
        {
            return;
        }

        _isLaunchingGame = value;
        OnPropertiesChanged(
            nameof(IsLaunchingGame),
            nameof(GameStatusText),
            nameof(GameButtonText),
            nameof(GameStatusBrush));
    }

    private void RefreshGameRunningState()
    {
        var running = HasGamePath && _gameService.IsRunning(GameExecutablePath);
        if (running == _isGameRunning)
        {
            return;
        }

        _isGameRunning = running;
        OnPropertiesChanged(
            nameof(IsGameRunning),
            nameof(GameStatusText),
            nameof(GameButtonText),
            nameof(GameStatusBrush));
    }

    private void SelectGameExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择爱之巢游戏程序",
            Filter = "游戏程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        try
        {
            var initial = HasGamePath
                ? Path.GetDirectoryName(GameExecutablePath)
                : _settings.DownloadDirectory;
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            {
                dialog.InitialDirectory = initial;
            }
        }
        catch
        {
            // Let Windows choose the initial folder.
        }

        if (dialog.ShowDialog() == true)
        {
            ApplyGameExecutable(dialog.FileName, showToast: true);
        }
    }

    private void AutoDetectGameExecutable()
    {
        if (_isGameRunning)
        {
            ShowToast("游戏运行中，暂时不能更换路径", isError: true);
            return;
        }

        var detected = _gameService.DetectExecutable(_settings.DownloadDirectory);
        if (detected is null)
        {
            ShowToast("下载目录中没有找到游戏，请手动选择", isError: true);
            return;
        }

        ApplyGameExecutable(detected, showToast: true);
    }

    private string? DetectAndApplyGameExecutable(string directory)
    {
        if (HasGamePath)
        {
            return null;
        }

        var detected = _gameService.DetectExecutable(directory);
        if (detected is null)
        {
            return null;
        }

        return ApplyGameExecutable(detected, showToast: false) ? detected : null;
    }

    private bool ApplyGameExecutable(string path, bool showToast)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("请选择有效的 EXE 游戏程序", isError: true);
                return false;
            }

            if (_isGameRunning
                && !string.Equals(fullPath, GameExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("请先退出正在运行的游戏", isError: true);
                return false;
            }

            _settings.GameExecutablePath = fullPath;
            try
            {
                _settingsService.Save(_settings);
            }
            catch (Exception exception)
            {
                LogService.Error("保存游戏路径失败", exception);
            }

            OnPropertiesChanged(
                nameof(GameExecutablePath),
                nameof(GameExecutableDisplay),
                nameof(GameExecutableTooltip),
                nameof(HasGamePath),
                nameof(GameStatusText),
                nameof(GameButtonText),
                nameof(GameStatusBrush));
            RefreshGameRunningState();
            if (showToast)
            {
                ShowToast("游戏路径已设置");
            }

            return true;
        }
        catch (Exception exception)
        {
            LogService.Error("设置游戏路径失败", exception);
            ShowToast($"游戏路径无效：{exception.Message}", isError: true);
            return false;
        }
    }

    private void OpenGameFolder()
    {
        if (!HasGamePath)
        {
            OpenDownloadFolder();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(
                "explorer.exe",
                $"/select,\"{GameExecutablePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            LogService.Error("打开游戏目录失败", exception);
            ShowToast("无法打开游戏目录", isError: true);
        }
    }

    private void HandleManagerStateChanged()
    {
        ActiveDownloadCount = _downloadManager.BusyCount;
        RefreshCounts();
    }

    private void HandleDownloadCompleted(DownloadCompletedEventArgs args)
    {
        RefreshCounts();
        var willAutoExtract = AutoExtract && args.Item.IsArchive;
        ShowToast(willAutoExtract ? $"下载完成，正在自动解压：{args.Item.Name}" : $"下载完成：{args.Item.Name}");
        SetStatus(willAutoExtract ? $"正在自动解压：{args.Item.Name}" : $"下载完成：{args.Item.Name}");

        if (willAutoExtract)
        {
            _ = ExtractArchiveAsync(args.Item, automatic: true);
        }
    }

    private void RefreshCounts()
    {
        DownloadedCount = Versions.Count(item => item.IsCompleted);
        OnPropertiesChanged(
            nameof(VersionsCountText),
            nameof(FilteredCountText),
            nameof(ShowNoResults),
            nameof(HasUnfinishedDownloads));
    }

    private void SetStatus(string message)
    {
        StatusText = message;
        ResetStatusLater();
    }

    private void ResetStatusLater()
    {
        _statusResetTimer.Stop();
        _statusResetTimer.Start();
    }

    private void ShowToast(string message, bool isError = false)
    {
        ToastMessage = message;
        ToastIsError = isError;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
