using System.Windows;

namespace LoveNestLauncher.Views;

public static class AppDialog
{
    public static void ShowInfo(string title, string message, string primaryText = "知道了", bool isError = false)
        => Show(title, message, primaryText, null, isError);

    public static bool Confirm(
        string title,
        string message,
        string primaryText = "确定",
        string secondaryText = "取消")
        => Show(title, message, primaryText, secondaryText, false);

    private static bool Show(
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        bool isError)
    {
        Services.LogService.Info($"显示对话框：{title}");
        var dialog = new DialogWindow(title, message, primaryText, secondaryText, isError);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Confirmed;
    }
}
