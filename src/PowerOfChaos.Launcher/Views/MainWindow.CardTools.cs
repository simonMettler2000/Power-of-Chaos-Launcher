using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PowerOfChaos.Launcher.Core.Launching;

namespace PowerOfChaos.Launcher.Views;

public partial class MainWindow
{
    private const string ToolsetRelease = "https://github.com/bitemydusto/YGO-PoC-Toolset/releases/tag/v1.3.2";

    private bool CardToolsAvailable => OperatingSystem.IsWindows() && _settingsReady && _profilesReadable
        && !_savingMod && !_pendingLaunch && !_busy && !_closed && !_sessions.AnyRunning;

    private void RefreshCardToolsControls()
    {
        var enabled = CardToolsAvailable;
        var editor = _settings.CardEditorPath;
        var joey = _settings.ModdedJoeyDirectory;
        var activeModded = _profileSelection.Active.IsModded;
        ChooseCardEditorButton.IsEnabled = enabled;
        OpenCardEditorButton.IsEnabled = enabled && editor is not null;
        ChooseModdedJoeyButton.IsEnabled = enabled;
        CardEditorPath.FilePath = editor;
        CardEditorPath.Content = editor;
        CardEditorPath.IsVisible = editor is not null;
        ModdedJoeyPath.FilePath = joey;
        ModdedJoeyPath.Content = joey;
        ModdedJoeyPath.IsVisible = joey is not null;
        ModdedJoeyGame.IsVisible = joey is not null || activeModded;
        SwitchFromModdedHint.IsVisible = activeModded;
        ModdedJoeyPlaytime.IsVisible = activeModded;
        PlayModdedJoeyButton.IsEnabled = enabled && joey is not null && !_temporaryState.Enabled && !_temporaryState.RecoveryRequired;
    }

    private async void ChooseCardEditor_Click(object? sender, RoutedEventArgs args)
    {
        if (!CardToolsAvailable) return;
        try
        {
            if (!StorageProvider.CanOpen) { Notice("The file picker could not open."); return; }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.Translate("Choose the card editor’s Windows executable."),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Texts.Translate("Windows application")) { Patterns = ["*.exe"] }]
            });
            if (!CardToolsAvailable || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            var plan = ExternalCardTools.EditorPlan(path);
            await SaveCardToolPathAsync(plan.FileName, editor: true);
        }
        catch (Exception error) when (IsProfileError(error)) { if (!_closed) Notice(error.Message); }
    }

    private void OpenCardEditor_Click(object? sender, RoutedEventArgs args)
    {
        if (!CardToolsAvailable) return;
        try
        {
            using var process = Process.Start(ExternalCardTools.EditorPlan(_settings.CardEditorPath));
            if (process is null) throw new InvalidOperationException("The card editor could not be opened.");
        }
        catch (Exception error) when (IsProfileError(error) || error is System.ComponentModel.Win32Exception)
        {
            NoticeFormat("The card editor could not be opened. {0}", error.Message);
        }
    }

    private async void ChooseModdedJoey_Click(object? sender, RoutedEventArgs args)
    {
        if (!CardToolsAvailable) return;
        try
        {
            if (!StorageProvider.CanPickFolder) { Notice("The folder picker is not available on this system."); return; }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Texts.Translate("Choose edited Joey folder"), AllowMultiple = false
            });
            if (!CardToolsAvailable || folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            var edited = await ExternalCardTools.InspectJoeyAsync(path, _settings.Installations.ToArray());
            if (!CardToolsAvailable) return;
            _profiles.CheckModdedFolder(edited.DiscoveryRoot);
            await SaveCardToolPathAsync(edited.DiscoveryRoot, editor: false);
        }
        catch (Exception error) when (IsProfileError(error)) { if (!_closed) Notice(error.Message); }
    }

    private async Task SaveCardToolPathAsync(string path, bool editor)
    {
        _savingMod = true;
        var previousPath = editor ? _settings.CardEditorPath : _settings.ModdedJoeyDirectory;
        if (editor) _settings.CardEditorPath = path;
        else _settings.ModdedJoeyDirectory = path;
        RefreshModControls();
        try
        {
            if (await SaveSettingsAsync())
                Notice(editor ? "Card editor path saved." : "Edited Joey folder saved. Use Play Modded Joey in Games.");
            else if (editor) _settings.CardEditorPath = previousPath;
            else _settings.ModdedJoeyDirectory = previousPath;
        }
        finally
        {
            _savingMod = false;
            if (!_closed)
            {
                RefreshModControls();
                if (_closeWhenIdle && !_sessions.AnyRunning && !_pendingLaunch) Close();
            }
        }
    }

    private async void CardToolsHelp_Click(object? sender, RoutedEventArgs args)
    {
        var help = new Window
        {
            Title = Texts.Translate("Card editor and Modded Joey help"),
            Width = 540, Height = 530, MinWidth = 400, MinHeight = 320,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var release = new Button { Content = Texts.Translate("Toolset release"), Classes = { "secondary" } };
        var close = new Button { Content = Texts.Translate("Close"), Classes = { "primary" } };
        release.Click += (_, _) =>
        {
            try { using var process = Process.Start(new ProcessStartInfo(ToolsetRelease) { UseShellExecute = true }); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            { Notice("The Toolset release page could not be opened."); }
        };
        close.Click += (_, _) => help.Close();
        help.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = Texts.Translate("Card editor and Modded Joey (experimental)"), FontSize = 21,
                        FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    HelpText("Choose your own editor executable in Mods. Open Card Editor starts it. Use the editor’s folder picker to open and save your unpacked card library."),
                    HelpText("Prepare a separate copy of the supported Joey the Passion game with your Toolset loader and edited data. Keep the original card slots in order and append new cards. Choose that folder in Mods."),
                    HelpText("Open the edited game’s unpacked data folder in the editor and mark new cards as available in Joey before saving."),
                    HelpText("In Games, use Play Modded Joey. The first launch creates a Modded profile for collection progress and playtime. Close the game before switching profiles. Play a stock game once first to set up the shared save."),
                    HelpText("The Toolset manages custom cards. Saving quantities for newly added cards is experimental. Built-in launcher mods apply to the original games."),
                    new WrapPanel { Children = { release, close } }
                }
            }
        };
        await help.ShowDialog(this);
    }

    private static TextBlock HelpText(string message) => new()
    {
        Text = Texts.Translate(message), TextWrapping = TextWrapping.Wrap, Classes = { "quiet" }, LineHeight = 22
    };
}
