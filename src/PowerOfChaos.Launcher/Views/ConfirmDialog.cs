using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PowerOfChaos.Launcher.Views;

public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string action)
    {
        Title = title; Width = 500; Height = 260; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        var proceed = new Button { Content = action, Classes = { "primary" } };
        cancel.Click += (_, _) => Close(false); proceed.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 20,
            Children =
            {
                new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, Classes = { "quiet" }, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, proceed } }
            }
        };
    }
}
