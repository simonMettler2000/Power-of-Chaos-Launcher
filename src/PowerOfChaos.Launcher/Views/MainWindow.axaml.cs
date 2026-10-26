using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Discovery;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;
using PowerOfChaos.Launcher.ViewModels;

namespace PowerOfChaos.Launcher.Views;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly GameDiscovery _discovery = new();
    private readonly GameSessionService _sessions = new();
    private readonly GamePortraitLoader _portraits = new();
    private readonly ObservableCollection<GameCardViewModel> _cards = [];
    private LauncherSettings _settings = new();
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _portraitCancellation;
    private bool _busy;
    private bool _closeWhenIdle;
    private bool _closed;
    private bool _pendingLaunch;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public MainWindow()
    {
        InitializeComponent();
        GameCards.ItemsSource = _cards;
        _sessions.SessionsChanged += SessionsChanged;
        Opened += async (_, _) => await InitializeAsync();
        Closing += (_, e) =>
        {
            if (_pendingLaunch || GameCatalog.All.Any(g => _sessions.IsRunning(g.Id)))
            {
                e.Cancel = true;
                _closeWhenIdle = true;
                WindowState = WindowState.Minimized;
                Notice("The launcher will close when the current game session ends.");
            }
        };
        Closed += async (_, _) =>
        {
            _closed = true;
            _scanCancellation?.Cancel();
            _portraitCancellation?.Cancel();
            _portraitCancellation?.Dispose();
            foreach (var card in _cards) card.Dispose();
            _sessions.SessionsChanged -= SessionsChanged;
            await _sessions.DisposeAsync();
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var loaded = await _store.LoadAsync();
            if (_closed) return;
            _settings = loaded;
            RenderLibrary();
            RenderLocations();
            await RefreshSessionsAsync();
            if (_closed) return;
            if (_store.LastWarning is { } settingsWarning) Notice(settingsWarning);
            else if (_sessions.LastWarning is { } playtimeWarning) Notice(playtimeWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Notice("Settings could not be loaded. You can add your game folder again.");
        }
    }

    private void RenderLibrary()
    {
        _portraitCancellation?.Cancel();
        _portraitCancellation?.Dispose();
        _portraitCancellation = new CancellationTokenSource();
        var retired = _cards.ToArray();
        _cards.Clear();
        foreach (var card in retired) card.Dispose();
        foreach (var game in GameCatalog.All)
        {
            var installed = _settings.Installations.FirstOrDefault(i => i.GameId == game.Id);
            if (installed is not null) _cards.Add(new GameCardViewModel(installed));
        }
        LibraryEmpty.IsVisible = _cards.Count == 0;
        InstalledLibrary.IsVisible = _cards.Count > 0;
        GameCount.Text = $"{_cards.Count} of 3 games installed";
        RescanMenu.IsEnabled = !_busy && _settings.SearchRoots.Count > 0;
        RescanButton.IsEnabled = RescanMenu.IsEnabled;
        foreach (var card in _cards) _ = LoadPortraitAsync(card, _portraitCancellation.Token);
    }

    private async Task LoadPortraitAsync(GameCardViewModel card, CancellationToken cancellationToken)
    {
        try
        {
            var pixels = await _portraits.LoadAsync(card.Installation, cancellationToken).ConfigureAwait(false);
            if (pixels is null || cancellationToken.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closed && !cancellationToken.IsCancellationRequested && _cards.Contains(card))
                    card.SetPortrait(pixels);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or NotSupportedException)
        {
            // Artwork is optional: retain a usable game row when it cannot be loaded.
        }
    }

    private void RenderLocations()
    {
        LocationRows.Children.Clear();
        if (_settings.SearchRoots.Count == 0)
        {
            LocationRows.Children.Add(new TextBlock { Text = "no game path added", Classes = { "quiet" }, Margin = new Avalonia.Thickness(0, 14) });
            return;
        }
        foreach (var root in _settings.SearchRoots)
        {
            var remove = new Button { Content = "Remove", Classes = { "secondary" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var path = new TextBlock { Text = root, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(path); grid.Children.Add(remove);
            LocationRows.Children.Add(new Border { Classes = { "panel" }, Padding = new Avalonia.Thickness(18), Child = grid });
            remove.Click += async (_, _) => await RemoveLocationAsync(root);
        }
    }

    private async Task RemoveLocationAsync(string root)
    {
        if (_busy || _pendingLaunch || _closed) return;
        if (GameCatalog.All.Any(g => _sessions.IsRunning(g.Id))) { Notice("Game locations can be changed after the current session ends."); return; }
        var confirm = new ConfirmDialog("Remove this game location?", "This removes the folder and its games from this launcher. Your installed files stay where they are.", "Remove location");
        var confirmed = await confirm.ShowDialog<bool>(this);
        if (_closed || !confirmed) return;
        _settings.SearchRoots.RemoveAll(p => string.Equals(p, root, PathComparison));
        _settings.Installations.RemoveAll(g => string.Equals(g.DiscoveryRoot, root, PathComparison));
        var saved = await SaveSettingsAsync();
        RenderLibrary(); RenderLocations(); await RefreshSessionsAsync();
        if (saved) Notice("Location removed from the launcher.");
    }

    private async void AddPath_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _pendingLaunch || _closed) return;
        var result = await new AddGamePathDialog().ShowDialog<AddPathResult?>(this);
        if (result is null || _closed) return;
        foreach (var group in result.Result.Games.GroupBy(i => i.GameId))
        {
            if (_sessions.IsRunning(group.Key)) continue;
            var installation = group.First();
            _settings.Installations.RemoveAll(i => i.GameId == group.Key);
            _settings.Installations.Add(installation);
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _settings.SearchRoots = _settings.SearchRoots.Append(result.Root)
            .Select(Path.TrimEndingDirectorySeparator).Distinct(comparer).ToList();
        var saved = await SaveSettingsAsync();
        RenderLibrary(); RenderLocations(); await RefreshSessionsAsync();
        ShowPage("library");
        var count = result.Result.Games.Select(i => i.GameId).Distinct().Count();
        Notice($"{count} game{(count == 1 ? "" : "s")} found." + ScanFootnote(result.Result) + (saved ? "" : " Settings could not be saved for next time."));
    }

    private async void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _pendingLaunch || _closed || _settings.SearchRoots.Count == 0) return;
        if (GameCatalog.All.Any(g => _sessions.IsRunning(g.Id))) { Notice("Rescan is available after the current game session ends."); return; }
        _busy = true;
        _scanCancellation = new CancellationTokenSource();
        CancelRescanButton.IsVisible = true;
        RescanMenu.IsEnabled = RescanButton.IsEnabled = false;
        Notice("Scanning your game folders…");
        try
        {
            var found = new List<GameInstallation>();
            var skipped = 0; var limited = false;
            foreach (var root in _settings.SearchRoots)
            {
                var result = await _discovery.ScanAsync(root, cancellationToken: _scanCancellation.Token);
                if (_closed) return;
                found.AddRange(result.Games); skipped += result.SkippedDirectories; limited |= result.LimitReached;
            }
            _settings.Installations = found.GroupBy(i => i.GameId).Select(g => g.First()).ToList();
            var saved = await SaveSettingsAsync();
            RenderLibrary(); RenderLocations(); await RefreshSessionsAsync();
            Notice((found.Count == 0 ? "game not found" : $"Rescan complete · {_cards.Count} game{(_cards.Count == 1 ? "" : "s")} available." + (limited ? " A search limit was reached; choose a folder closer to the games." : skipped > 0 ? " Some protected folders could not be read." : "")) + (saved ? "" : " Settings could not be saved for next time."));
        }
        catch (OperationCanceledException) { Notice("Scan cancelled. Your previous game locations were kept."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Notice("The folders could not be scanned. Check the locations and try again."); }
        finally
        {
            _busy = false; _scanCancellation?.Dispose(); _scanCancellation = null;
            if (!_closed)
            {
                CancelRescanButton.IsVisible = false;
                RescanMenu.IsEnabled = RescanButton.IsEnabled = _settings.SearchRoots.Count > 0;
            }
        }
    }

    private static string ScanFootnote(DiscoveryResult result) => result.LimitReached
        ? " A search limit was reached; add a closer folder if a game is missing."
        : result.SkippedDirectories > 0 ? " Some protected folders were skipped." : "";
    private void CancelRescan_Click(object? sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async void PlayGame_Click(object? sender, RoutedEventArgs e)
    {
        if (_pendingLaunch || _closed || sender is not Button { Tag: GameInstallation installation }) return;
        _pendingLaunch = true;
        foreach (var card in _cards) card.SetLaunchPending(true);
        try
        {
            var result = await _sessions.LaunchAsync(installation, _settings);
            if (!result.Success) { Notice(result.Error ?? "The game could not be started."); return; }
            await RefreshSessionsAsync();
            var hasWarning = ShowSessionWarning();
            if (!hasWarning) Notice($"{GameCatalog.Get(installation.GameId).Title} is running. Playtime tracking is active.");
            if (_settings.MinimizeOnLaunch && !hasWarning) WindowState = WindowState.Minimized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Notice("The game could not be started. Check its location and your Wine runner preference, if applicable.");
        }
        finally
        {
            _pendingLaunch = false;
            if (!_closed)
            {
                foreach (var card in _cards) card.SetLaunchPending(false);
                if (_closeWhenIdle && !GameCatalog.All.Any(g => _sessions.IsRunning(g.Id)) && !ShowSessionWarning()) Close();
            }
        }
    }

    private void SessionsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closed) return;
        try
        {
            await RefreshSessionsAsync();
            if (ShowSessionWarning()) return;
            if (_closeWhenIdle && !GameCatalog.All.Any(g => _sessions.IsRunning(g.Id))) Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice("Playtime could not be refreshed. Check that the launcher data folder is writable."); }
    });

    private bool ShowSessionWarning()
    {
        if (_closed || _sessions.LastWarning is not { } warning) return false;
        Notice(warning);
        _closeWhenIdle = false;
        if (!GameCatalog.All.Any(g => _sessions.IsRunning(g.Id)) && WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Activate();
        }
        return true;
    }

    private async Task RefreshSessionsAsync()
    {
        var totals = await _sessions.GetPlaytimeSecondsAsync();
        if (_closed) return;
        var anyRunning = GameCatalog.All.Any(g => _sessions.IsRunning(g.Id));
        foreach (var card in _cards)
        {
            var running = _sessions.IsRunning(card.Installation.GameId);
            card.Refresh(totals.GetValueOrDefault(card.Installation.GameId), running, anyRunning && !running);
            card.SetLaunchPending(_pendingLaunch);
        }
    }

    private async Task<bool> SaveSettingsAsync()
    {
        try { await _store.SaveAsync(_settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice("Your settings are available now, but could not be saved for next time."); return false; }
    }

    private async void Preferences_Click(object? sender, RoutedEventArgs e)
    {
        var result = await new PreferencesDialog(_settings).ShowDialog<PreferencesResult?>(this);
        if (result is null || _closed) return;
        _settings.MinimizeOnLaunch = result.MinimizeOnLaunch;
        _settings.WineExecutable = result.WineExecutable;
        if (await SaveSettingsAsync()) Notice("Preferences saved.");
    }

    private void Notice(string message) { if (_closed) return; StatusText.Text = message; StatusBanner.IsVisible = true; }
    private void Library_Click(object? sender, RoutedEventArgs e) => ShowPage("library");
    private void Mods_Click(object? sender, RoutedEventArgs e) => ShowPage("mods");
    private void Collection_Click(object? sender, RoutedEventArgs e) => ShowPage("collection");
    private void Locations_Click(object? sender, RoutedEventArgs e) { RenderLocations(); ShowPage("locations"); }
    private void About_Click(object? sender, RoutedEventArgs e) => ShowPage("about");

    private void ShowPage(string page)
    {
        LibraryView.IsVisible = page == "library"; ModsView.IsVisible = page == "mods";
        CollectionView.IsVisible = page == "collection"; LocationsView.IsVisible = page == "locations"; AboutView.IsVisible = page == "about";
        LibraryNav.Classes.Set("active", page == "library"); ModsNav.Classes.Set("active", page == "mods");
        CollectionNav.Classes.Set("active", page == "collection"); LocationsNav.Classes.Set("active", page == "locations");
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "mods" => ("Mods", "Planned options for your games."),
            "collection" => ("Collection", "Card collection support is in development."),
            "locations" => ("Game locations", "Manage the folders searched for games."),
            "about" => ("About", "Power of Chaos Launcher"),
            _ => ("Games", "Your installed Power of Chaos games.")
        };
    }
}
