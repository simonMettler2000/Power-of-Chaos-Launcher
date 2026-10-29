using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Views;

public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string action)
    {
        Localize(this, TitleProperty, title); Width = 500; Height = 260; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var cancel = Localize(new Button { Classes = { "secondary" } }, ContentControl.ContentProperty, "Cancel");
        var proceed = Localize(new Button { Classes = { "primary" } }, ContentControl.ContentProperty, action);
        cancel.Click += (_, _) => Close(false); proceed.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 20,
            Children =
            {
                Localize(new TextBlock { FontSize = 21, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, TextBlock.TextProperty, title),
                Localize(new TextBlock { Classes = { "quiet" }, TextWrapping = TextWrapping.Wrap }, TextBlock.TextProperty, message),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, proceed } }
            }
        };
    }

    private static T Localize<T>(T target, AvaloniaProperty property, string english) where T : AvaloniaObject
    {
        target.Bind(property, new Binding(nameof(LocalizedTextEntry.Value)) { Source = LauncherText.Current.Entry(english), Mode = BindingMode.OneWay });
        return target;
    }
}
