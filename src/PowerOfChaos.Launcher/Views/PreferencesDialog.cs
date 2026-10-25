using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PowerOfChaos.Launcher.Core;

namespace PowerOfChaos.Launcher.Views;

public sealed record PreferencesResult(bool MinimizeOnLaunch, string WineExecutable);

public sealed class PreferencesDialog : Window
{
    public PreferencesDialog(LauncherSettings settings)
    {
        Title = "Launcher preferences";
        Width = 560; Height = 385; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var minimize = new CheckBox { Content = "Minimize the launcher when a game starts", IsChecked = settings.MinimizeOnLaunch };
        var wine = new TextBox { Text = settings.WineExecutable, Watermark = "wine", Name = "WineCommand" };
        var message = new TextBlock { Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Save preferences", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(wine.Text)) { message.Text = "Enter a Wine executable name or its full path."; return; }
            Close(new PreferencesResult(minimize.IsChecked == true, wine.Text.Trim().Trim('"')));
        };
        Content = new StackPanel
        {
            Margin = new Thickness(30), Spacing = 18,
            Children =
            {
                new TextBlock { Text = "Preferences", Classes = { "sectionTitle" } },
                minimize,
                new StackPanel { Spacing = 8, Children =
                {
                    new TextBlock { Text = "Wine runner", FontWeight = FontWeight.SemiBold },
                    wine,
                    new TextBlock { Text = "Used on Linux and macOS. Enter the executable itself, without command-line arguments.", Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                } },
                message,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, save } }
            }
        };
    }
}
