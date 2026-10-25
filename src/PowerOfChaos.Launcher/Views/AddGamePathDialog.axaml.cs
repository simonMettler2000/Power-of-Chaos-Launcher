using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Discovery;

namespace PowerOfChaos.Launcher.Views;

public sealed record AddPathResult(string Root, DiscoveryResult Result);

public partial class AddGamePathDialog : Window
{
    private CancellationTokenSource? _cancellation;
    private bool _scanning;
    private bool _closed;

    public AddGamePathDialog()
    {
        InitializeComponent();
        PathInput.TextChanged += (_, _) => ScanButton.IsEnabled = !_scanning && !string.IsNullOrWhiteSpace(PathInput.Text);
        Closed += (_, _) => { _closed = true; _cancellation?.Cancel(); };
        Opened += (_, _) => PathInput.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            if (_scanning) _cancellation?.Cancel(); else Close(null);
            e.Handled = true;
        };
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        try
        {
            if (!StorageProvider.CanPickFolder) { ResultText.Text = "Paste a folder path to continue."; return; }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a Power of Chaos game folder", AllowMultiple = false });
            if (folders.Count > 0) PathInput.Text = folders[0].TryGetLocalPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ResultText.Text = "The folder picker could not open. Paste a folder path instead."; }
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        if (_scanning || string.IsNullOrWhiteSpace(PathInput.Text)) return;
        var root = PathInput.Text.Trim().Trim('"');
        _scanning = true; _cancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false; PathInput.IsEnabled = false; ScanProgress.IsVisible = true;
        CancelButton.Content = "Cancel scan"; ResultText.Text = "Looking for Yugi, Kaiba and Joey…";
        try
        {
            var result = await new GameDiscovery().ScanAsync(root, cancellationToken: _cancellation.Token);
            if (_closed) return;
            if (result.Games.Count > 0)
            {
                var normalized = result.Games[0].DiscoveryRoot;
                Close(new AddPathResult(normalized, result));
                return;
            }
            ResultText.Text = "game not found" + (result.LimitReached
                ? "\nThe search limit was reached. Choose a folder closer to your installation."
                : "\nChoose the game’s installation folder, or a parent folder containing it.");
        }
        catch (OperationCanceledException) { if (!_closed) ResultText.Text = "Scan cancelled. Choose a folder when you’re ready."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_closed) ResultText.Text = "game not found\nCheck that the folder exists and can be opened.";
        }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null; _scanning = false;
            if (!_closed)
            {
                PathInput.IsEnabled = true; ScanProgress.IsVisible = false;
                CancelButton.Content = "Cancel"; ScanButton.IsEnabled = !string.IsNullOrWhiteSpace(PathInput.Text);
            }
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_scanning) _cancellation?.Cancel();
        else Close(null);
    }
}
