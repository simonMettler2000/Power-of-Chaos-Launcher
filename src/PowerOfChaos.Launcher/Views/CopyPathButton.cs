using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Views;

public sealed class CopyPathButton : Button
{
    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<CopyPathButton, string?>(nameof(FilePath));

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public CopyPathButton()
    {
        Classes.Add("copyPath");
        IsEnabled = false;
        ContentTemplate = new FuncDataTemplate<string>((_, _) =>
            new TextBlock { [!TextBlock.TextProperty] = new Binding("."), TextTrimming = TextTrimming.CharacterEllipsis });

        var copy = new MenuItem();
        var copyLabel = LauncherText.Current.Entry("Copy path");
        copy.Bind(MenuItem.HeaderProperty, new Binding(nameof(LocalizedTextEntry.Value)) { Source = copyLabel });
        Bind(AutomationProperties.HelpTextProperty, new Binding(nameof(LocalizedTextEntry.Value)) { Source = copyLabel });
        copy.Click += Copy_Click;
        ContextMenu = new ContextMenu { Items = { copy } };
        Click += Copy_Click;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FilePathProperty) return;
        IsEnabled = !string.IsNullOrEmpty(FilePath);
        ToolTip.SetTip(this, FilePath);
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (FilePath is { Length: > 0 } path)
            await ClipboardText.TryCopyAsync(this, path);
    }
}
