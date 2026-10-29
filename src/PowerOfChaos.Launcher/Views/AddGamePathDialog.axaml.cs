using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Discovery;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Views;

public sealed record AddPathResult(string Root, DiscoveryResult Result);

public partial class AddGamePathDialog : Window
{
    private CancellationTokenSource? _cancellation;
    private bool _scanning;
    private bool _closed;
    private IDisposable? _resultBinding;
    private IDisposable? _cancelBinding;

    public AddGamePathDialog()
    {
        InitializeComponent();
        PathInput.TextChanged += (_, _) => ScanButton.IsEnabled = !_scanning && !string.IsNullOrWhiteSpace(PathInput.Text);
        Closed += (_, _) =>
        {
            _closed = true; _cancellation?.Cancel();
            _resultBinding?.Dispose(); _cancelBinding?.Dispose();
        };
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
            if (!StorageProvider.CanPickFolder) { ShowResult("Paste a folder path to continue."); return; }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = LauncherText.Current.Translate("Choose a Power of Chaos game folder"), AllowMultiple = false });
            if (folders.Count > 0) PathInput.Text = folders[0].TryGetLocalPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ShowResult("The folder picker could not open. Paste a folder path instead."); }
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        if (_scanning || string.IsNullOrWhiteSpace(PathInput.Text)) return;
        var root = PathInput.Text.Trim().Trim('"');
        _scanning = true; _cancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false; PathInput.IsEnabled = false; ScanProgress.IsVisible = true;
        SetCancelText("Cancel scan"); ShowResult("Looking for Yugi, Kaiba and Joey…");
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
            ShowResult(result.LimitReached
                ? "game not found\nThe search limit was reached. Choose a folder closer to your installation."
                : "game not found\nChoose the game’s installation folder, or a parent folder containing it.");
        }
        catch (OperationCanceledException) { if (!_closed) ShowResult("Scan cancelled. Choose a folder when you’re ready."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_closed) ShowResult("game not found\nCheck that the folder exists and can be opened.");
        }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null; _scanning = false;
            if (!_closed)
            {
                PathInput.IsEnabled = true; ScanProgress.IsVisible = false;
                SetCancelText("Cancel"); ScanButton.IsEnabled = !string.IsNullOrWhiteSpace(PathInput.Text);
            }
        }
    }

    private void ShowResult(string english)
    {
        _resultBinding?.Dispose();
        _resultBinding = ResultText.Bind(TextBlock.TextProperty,
            new Binding(nameof(LocalizedTextEntry.Value)) { Source = LauncherText.Current.Entry(english), Mode = BindingMode.OneWay });
    }

    private void SetCancelText(string english)
    {
        _cancelBinding?.Dispose();
        _cancelBinding = CancelButton.Bind(ContentControl.ContentProperty,
            new Binding(nameof(LocalizedTextEntry.Value)) { Source = LauncherText.Current.Entry(english), Mode = BindingMode.OneWay });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_scanning) _cancellation?.Cancel();
        else Close(null);
    }
}
