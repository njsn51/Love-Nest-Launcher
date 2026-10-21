using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LoveNestLauncher.Common;
using LoveNestLauncher.Services;
using LoveNestLauncher.ViewModels;
using LoveNestLauncher.Views;

namespace LoveNestLauncher;

public partial class MainWindow : Window
{
    private readonly DownloadManager _downloadManager;
    private readonly AcrylicService _acrylicService = new();
    private readonly MainViewModel _viewModel;
    private readonly string? _screenshotDirectory;
    private bool _loaded;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _isCapturing;

    public MainWindow(IReadOnlyList<string> args)
    {
        InitializeComponent();

        _screenshotDirectory = ResolveScreenshotDirectory(args);

        var requestedSize = ResolveWindowSize(args);
        if (requestedSize is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 40));
            Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 40));
        }

        var settingsService = new SettingsService();
        var sourceService = new SourceService();
        var gameService = new GameService();
        _downloadManager = new DownloadManager();
        var archiveService = new ArchiveService();
        _viewModel = new MainViewModel(settingsService, sourceService, _downloadManager, archiveService, gameService);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.AcrylicPreferenceChanged += enabled =>
        {
            if (string.IsNullOrWhiteSpace(_screenshotDirectory))
            {
                ApplyWindowMaterial(enabled);
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += MainWindow_SourceInitialized;

        WindowFrame.Opacity = 0;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowMessageHook);
        }
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.rcWork;
        var bounds = monitorInfo.rcMonitor;
        minMaxInfo.ptMaxPosition.X = work.Left - bounds.Left;
        minMaxInfo.ptMaxPosition.Y = work.Top - bounds.Top;
        minMaxInfo.ptMaxSize.X = work.Right - work.Left;
        minMaxInfo.ptMaxSize.Y = work.Bottom - work.Top;
        minMaxInfo.ptMaxTrackSize.X = work.Right - work.Left;
        minMaxInfo.ptMaxTrackSize.Y = work.Bottom - work.Top;
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        WindowFrame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            LogService.Error("启动器初始化失败", exception);
            AppDialog.ShowInfo("初始化失败", exception.Message, "知道了", isError: true);
        }

        ApplyWindowMaterial(
            string.IsNullOrWhiteSpace(_screenshotDirectory) && _viewModel.EnableAcrylic);

        if (!string.IsNullOrWhiteSpace(_screenshotDirectory))
        {
            _isCapturing = true;
            try
            {
                try
                {
                    await _viewModel.WaitForSizeProbeAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    LogService.Warn("等待版本大小信息超时，继续生成截图。");
                }

                await CaptureScreensAsync(_screenshotDirectory);
            }
            catch (Exception exception)
            {
                LogService.Error("生成界面截图失败", exception);
            }
            finally
            {
                _isCapturing = false;
                _allowClose = true;
                Close();
            }
        }
    }

    private void ApplyWindowMaterial(bool enableAcrylic)
    {
        var acrylicActive = enableAcrylic && _acrylicService.Enable(this);
        if (acrylicActive)
        {
            Background = Brushes.Transparent;
            WindowFrame.Background = BrushHelper.FromHex("#8CFFF9FB");
            LogService.Info("亚克力透明背景已启用。");
        }
        else
        {
            _acrylicService.Disable(this);
            Background = BrushHelper.FromHex("#FBF8FA");
            WindowFrame.Background = BrushHelper.FromHex("#FBF8FA");
            if (enableAcrylic)
            {
                LogService.Warn("当前系统未能启用亚克力透明背景，已回退为纯色背景。");
            }
            else
            {
                LogService.Info("亚克力透明背景已关闭。");
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPageTitle))
        {
            AnimatePageChange();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsToastVisible) && _viewModel.IsToastVisible)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            }, DispatcherPriority.Background);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"page-{_viewModel.PageIndex}-{DateTime.Now:HHmmss}.png");
            SaveScreenshot(path);
            File.WriteAllText(
                Path.Combine(directory, "last-state.txt"),
                $"PageIndex={_viewModel.PageIndex}{Environment.NewLine}" +
                $"PageTitle={_viewModel.CurrentPageTitle}{Environment.NewLine}" +
                $"Home={HomePageView.Visibility}{Environment.NewLine}" +
                $"Downloads={DownloadsPageView.Visibility}{Environment.NewLine}" +
                $"Settings={SettingsPageView.Visibility}{Environment.NewLine}" +
                $"About={AboutPageView.Visibility}{Environment.NewLine}" +
                $"More={MorePageView.Visibility}{Environment.NewLine}" +
                $"Captured={DateTime.Now:O}{Environment.NewLine}");
            _viewModel.SetDiagnosticStatus($"诊断截图已保存：{path}");
        }
        catch (Exception exception)
        {
            LogService.Error("诊断截图失败", exception);
        }

        e.Handled = true;
    }

    private void AnimatePageChange()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = easing
        });
        PageTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = easing
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can fail if the mouse button was released before it started.
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowFrame.CornerRadius = new CornerRadius(0);
            WindowFrame.BorderThickness = new Thickness(0);
            MaximizeGlyph.Text = "\uE923";
        }
        else
        {
            WindowFrame.CornerRadius = new CornerRadius(16);
            WindowFrame.BorderThickness = new Thickness(1);
            MaximizeGlyph.Text = "\uE922";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _isCapturing)
        {
            return;
        }

        e.Cancel = true;
        if (_closePromptOpen)
        {
            return;
        }

        _closePromptOpen = true;
        LogService.Info("收到关闭窗口请求，准备显示退出确认。");
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var prompt = _viewModel.BuildExitPrompt();
                LogService.Info($"退出确认内容长度：{prompt.Length}");
                if (!string.IsNullOrWhiteSpace(prompt)
                    && !AppDialog.Confirm("退出 Love Nest Launcher", prompt, "退出", "继续使用"))
                {
                    LogService.Info("用户取消退出。");
                    return;
                }

                _allowClose = true;
                LogService.Info("用户确认退出，关闭主窗口。");
                Close();
            }
            finally
            {
                _closePromptOpen = false;
            }
        }, DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        _downloadManager.Dispose();
        base.OnClosed(e);
    }

    private async Task CaptureScreensAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var pages = new (int Index, string FileName)[]
        {
            (0, "01-home.png"),
            (1, "02-downloads.png"),
            (2, "03-settings.png"),
            (3, "04-about.png"),
            (4, "05-more.png")
        };

        foreach (var page in pages)
        {
            _viewModel.NavigateCommand.Execute(page.Index.ToString());
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(320);
            UpdateLayout();
            WindowFrame.UpdateLayout();
            SaveScreenshot(Path.Combine(directory, page.FileName));
        }

        await File.WriteAllTextAsync(
            Path.Combine(directory, "capture.txt"),
            $"Love Nest Launcher {_viewModel.AppVersion}{Environment.NewLine}" +
            $"Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Window: {(int)ActualWidth}x{(int)ActualHeight}{Environment.NewLine}" +
            $"Source: {_viewModel.SourceStatusText}{Environment.NewLine}");
    }

    private void SaveScreenshot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Round(WindowFrame.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(WindowFrame.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(WindowFrame);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static (double Width, double Height)? ResolveWindowSize(IReadOnlyList<string> args)
    {
        var raw = args
            .FirstOrDefault(a => a.StartsWith("--window-size=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], out var width)
            && double.TryParse(parts[1], out var height)
            && width >= 960
            && height >= 600)
        {
            return (width, height);
        }

        return null;
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    private static string? ResolveScreenshotDirectory(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument.Split('=', 2)[1].Trim('"');
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (string.Equals(argument, "--screenshot", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Count)
            {
                return args[index + 1].Trim('"');
            }
        }

        return null;
    }
}
