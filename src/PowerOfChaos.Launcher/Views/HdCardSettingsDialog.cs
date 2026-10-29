using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Views;

public sealed record HdCardSettingsResult(string ImageDirectory, string PackDirectory);

/// <summary>Edits a pending selection only. Cancel and closing never change saved preferences.</summary>
public sealed class HdCardSettingsDialog : Window
{
    private readonly CancellationTokenSource _closing = new();
    private bool _closed;

    public HdCardSettingsDialog(LauncherSettings settings, Func<string, Task<string?>>? chooseFolder = null,
        Func<string, string, CancellationToken, Task>? validateSelection = null)
    {
        Localize(this, TitleProperty, "HD card images and help");
        Width = 660; Height = 680; MinWidth = 540; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var images = new TextBox { Name = "HdCardImageDirectory", Text = settings.HdCardImageDirectory };
        Localize(images, TextBox.WatermarkProperty, "Use image paths from the prepared pack");
        Localize(images, AutomationProperties.NameProperty, "HD card image folder");
        var pack = new TextBox { Name = "HdPreparedPackDirectory", Text = settings.HdPackDirectory };
        Localize(pack, TextBox.WatermarkProperty, "Automatic: HdCards beside the launcher or in launcher data");
        Localize(pack, AutomationProperties.NameProperty, "Prepared HD pack folder (advanced)");
        var browseImages = Button("HdBrowseImages", "Choose image folder...");
        var browsePack = Button("HdBrowsePack", "Choose prepared pack...");
        var reset = Button("HdResetImages", "Use pack image paths");
        reset.Click += (_, _) => images.Text = "";
        var automatic = Button("HdAutomaticPack", "Detect prepared pack automatically");
        automatic.Click += (_, _) => pack.Text = "";
        async Task Browse(TextBox target, string title)
        {
            var translated = LauncherText.Current.Translate(title);
            var path = chooseFolder is not null ? await chooseFolder(translated) : await PickFolderAsync(translated);
            if (!_closed && path is not null) target.Text = path;
        }
        browseImages.Click += async (_, _) => await Browse(images, "Choose the folder containing HD card PNG images");
        browsePack.Click += async (_, _) => await Browse(pack, "Choose local HD card pack");
        var message = new TextBlock { Name = "HdSettingsMessage", IsVisible = false, Classes = { "quiet" }, TextWrapping = TextWrapping.Wrap };
        string? failure = null;
        void RenderFailure() { message.IsVisible = failure is not null; message.Text = failure is null ? "" : LauncherText.Current.Translate(failure); }
        EventHandler languageChanged = (_, _) => RenderFailure();
        LauncherText.Current.LanguageChanged += languageChanged;
        var save = Button("HdSavePaths", "Save HD folders", "primary");
        var cancel = Button("HdCancelPaths", "Cancel");
        cancel.Click += (_, _) => Close(null);
        save.Click += async (_, _) =>
        {
            // Capture both edits before I/O; only these validated values may leave this dialog.
            var selectedImages = images.Text?.Trim().Trim('"') ?? "";
            var selectedPack = pack.Text?.Trim().Trim('"') ?? "";
            save.IsEnabled = false;
            try
            {
                if (validateSelection is null) await HdCardPackLocation.ValidateSelectionAsync(selectedPack, selectedImages, _closing.Token);
                else await validateSelection(selectedPack, selectedImages, _closing.Token);
                if (!_closed) Close(new HdCardSettingsResult(selectedImages, selectedPack));
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { if (!_closed) { failure = exception.Message; RenderFailure(); } }
            finally { if (!_closed) save.IsEnabled = true; }
        };
        Closed += (_, _) => { _closed = true; LauncherText.Current.LanguageChanged -= languageChanged; _closing.Cancel(); _closing.Dispose(); };
        var body = new ScrollViewer { Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 15, Children =
            {
                Label("HD card images and help", "sectionTitle"),
                Label("Extract any ZIP first. Choose the folder directly containing your HD card PNG images, not the ZIP or its parent folder."),
                Label("HD card image folder"), images,
                Row(browseImages, reset),
                Label("Keep the filenames expected by your prepared pack. Every required image must be an 800 × 1160 RGB8 non-interlaced PNG. Missing or unreadable images, wrong dimensions or unsupported PNG modes stop HD launch; older images are never substituted.", "quiet"),
                Label("Export PNGs without colour profiles or extra metadata. The launcher checks only PNG headers before launch. Unsupported or damaged image data can still show the original card in-game.", "quiet"),
                new Expander { Name = "HdAdvancedPack", HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = Label("Prepared HD pack folder (advanced)"), Content = new StackPanel
                    { Spacing = 12, Children =
                    {
                        pack, Row(browsePack, automatic),
                        Label("This folder contains yugi-565.tsv, kaiba-565.tsv and joey-565.tsv. Keep their referenced reference, cursor and background banks available. Leave blank for automatic HdCards detection.", "quiet")
                    } } },
                Label("PNG images alone cannot create a first-time HD pack. A matching prepared pack and reference banks from your own games are still required. An existing local pack is detected automatically.", "quiet"),
                Label("Save the folders, then enable HD card previews in Mods. Changes apply to all three games on the next launch. Keep both folders available. No artwork is changed or downloaded.", "quiet"),
                Label("Experimental: may break or display incorrectly.", "quiet")
            }
        } };
        var footer = new StackPanel { Margin = new Thickness(28, 12, 28, 22), Spacing = 12, Children =
        {
            message,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, save } }
        } };
        Grid.SetRow(footer, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { body, footer } };
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 8, 6); row.Children.Add(control); }
        return row;
    }
    private static TextBlock Label(string text, string? style = null)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        if (style is not null) label.Classes.Add(style);
        return Localize(label, TextBlock.TextProperty, text);
    }
    private static Button Button(string name, string text, string style = "secondary")
        => Localize(new Button { Name = name, Classes = { style } }, ContentControl.ContentProperty, text);
    private static T Localize<T>(T target, AvaloniaProperty property, string english) where T : AvaloniaObject
    {
        target.Bind(property, new Binding(nameof(LocalizedTextEntry.Value)) { Source = LauncherText.Current.Entry(english), Mode = BindingMode.OneWay });
        return target;
    }
}
