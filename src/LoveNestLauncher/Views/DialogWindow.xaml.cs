using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LoveNestLauncher.Common;

namespace LoveNestLauncher.Views;

public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string primaryText, string? secondaryText, bool isError)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "知道了" : primaryText;

        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondaryText;
        }

        if (isError)
        {
            IconGlyph.Text = "\uEA39";
            IconGlyph.Foreground = BrushHelper.FromHex("#E04C63");
            IconBox.Background = BrushHelper.FromHex("#FFF0F2");
        }

        Loaded += (_, _) =>
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, animation);
        };
        KeyDown += OnKeyDown;
    }

    public bool Confirmed { get; private set; }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Confirmed = false;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter)
        {
            Confirmed = true;
            DialogResult = true;
        }
    }
}
