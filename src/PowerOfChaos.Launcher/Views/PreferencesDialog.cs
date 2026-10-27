using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PowerOfChaos.Launcher.Core;

namespace PowerOfChaos.Launcher.Views;

public sealed record PreferencesResult(bool MinimizeOnLaunch, string WineExecutable, string HdPackDirectory);

public sealed class PreferencesDialog : Window
{
    public PreferencesDialog(LauncherSettings settings)
    {
        Title = "Launcher preferences";
        Width = 560; Height = 545; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var minimize = new CheckBox { Content = "Minimize the launcher when a game starts", IsChecked = settings.MinimizeOnLaunch };
        var wine = new TextBox { Text = settings.WineExecutable, Watermark = "wine", Name = "WineCommand" };
        var pack = new TextBox { Text = settings.HdPackDirectory, Watermark = "Choose the generated local HD pack folder", Name = "HdPackDirectory" };
        var browse = new Button { Content = "Choose folder...", Classes = { "secondary" } };
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose local HD card pack", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) pack.Text = path;
        };
        var message = new TextBlock { Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Save preferences", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(wine.Text)) { message.Text = "Enter a Wine executable name or its full path."; return; }
            Close(new PreferencesResult(minimize.IsChecked == true, wine.Text.Trim().Trim('"'), pack.Text?.Trim().Trim('"') ?? ""));
        };
        Content = new ScrollViewer { Content = new StackPanel
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
                new StackPanel { Spacing = 8, Children =
                {
                    new TextBlock { Text = "HD card pack folder", FontWeight = FontWeight.SemiBold },
                    pack, browse,
                    new TextBlock { Text = "Local generated pack containing yugi-565.tsv, kaiba-565.tsv and joey-565.tsv. Changes apply on the next launch.", Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                } },
                message,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, save } }
            }
        } };
    }
}
