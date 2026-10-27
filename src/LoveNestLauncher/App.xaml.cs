using System.Windows;
using System.Windows.Threading;
using LoveNestLauncher.Common;
using LoveNestLauncher.Services;

namespace LoveNestLauncher;

public partial class App : Application
{
    private bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Initialize(e.Args);
        LogService.Initialize();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                LogService.Error("未处理的后台异常", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("未观察到的任务异常", args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Info("Love Nest Launcher exited.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("界面线程异常", e.Exception);
        e.Handled = true;

        if (_showingError)
        {
            return;
        }

        _showingError = true;
        try
        {
            MessageBox.Show(
                $"启动器遇到了一个意外错误，但已经拦截，避免直接退出。\n\n{e.Exception.Message}\n\n详细信息已写入日志：\n{AppPaths.LogFile}",
                "Love Nest Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }
}
