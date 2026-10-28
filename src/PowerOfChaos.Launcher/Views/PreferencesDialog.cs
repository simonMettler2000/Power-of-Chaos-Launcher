using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Views;

public sealed record PreferencesResult(bool MinimizeOnLaunch, string WineExecutable);

public sealed class PreferencesDialog : Window
{
    public PreferencesDialog(LauncherSettings settings)
    {
        Localize(this, TitleProperty, "Launcher preferences");
        Width = 560; Height = 385; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var minimize = new CheckBox
        {
            Content = Localize(new TextBlock { TextWrapping = TextWrapping.Wrap }, TextBlock.TextProperty, "Minimize the launcher when a game starts"),
            IsChecked = settings.MinimizeOnLaunch
        };
        var wine = new TextBox { Text = settings.WineExecutable, Watermark = "wine", Name = "WineCommand" };
        var message = new TextBlock { Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var save = Localize(new Button { Classes = { "primary" } }, ContentControl.ContentProperty, "Save preferences");
        var cancel = Localize(new Button { Classes = { "secondary" } }, ContentControl.ContentProperty, "Cancel");
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(wine.Text)) { Localize(message, TextBlock.TextProperty, "Enter a Wine executable name or its full path."); return; }
            Close(new PreferencesResult(minimize.IsChecked == true, wine.Text.Trim().Trim('"')));
        };
        Content = new StackPanel
        {
            Margin = new Thickness(30), Spacing = 18,
            Children =
            {
                Localize(new TextBlock { Classes = { "sectionTitle" } }, TextBlock.TextProperty, "Preferences"),
                minimize,
                new StackPanel { Spacing = 8, Children =
                {
                    Localize(new TextBlock { FontWeight = FontWeight.SemiBold }, TextBlock.TextProperty, "Wine runner"),
                    wine,
                    Localize(new TextBlock { Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap }, TextBlock.TextProperty,
                        "Used on Linux and macOS. Enter the executable itself, without command-line arguments.")
                } },
                message,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, save } }
            }
        };
    }

    private static T Localize<T>(T target, AvaloniaProperty property, string english) where T : AvaloniaObject
    {
        target.Bind(property, new Binding(nameof(LocalizedTextEntry.Value)) { Source = LauncherText.Current.Entry(english), Mode = BindingMode.OneWay });
        return target;
    }
}
