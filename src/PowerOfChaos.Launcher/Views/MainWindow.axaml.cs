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
    private bool _settingsReady;
    private bool _savingMod;
    private string? _hdUnavailableReason = "Checking local HD prerequisites...";
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public MainWindow()
    {
        InitializeComponent();
        GameCards.ItemsSource = _cards;
        _sessions.SessionsChanged += SessionsChanged;
        Opened += async (_, _) => await InitializeAsync();
        Closing += (_, e) =>
        {
            if (_pendingLaunch || _savingMod || GameCatalog.All.Any(g => _sessions.IsRunning(g.Id)))
            {
                e.Cancel = true;
                _closeWhenIdle = true;
                WindowState = WindowState.Minimized;
                Notice(_savingMod ? "The launcher will close after saving your settings." : "The launcher will close when the current game session ends.");
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
            await RefreshHdAvailabilityAsync();
            if (_closed) return;
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
        finally
        {
            _settingsReady = true;
            if (!_closed) RefreshModControls();
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
            if (installed is not null)
            {
                var card = new GameCardViewModel(installed);
                card.SetLaunchPending(!_settingsReady || _savingMod || _pendingLaunch);
                _cards.Add(card);
            }
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
        if (!_settingsReady || _savingMod || _busy || _pendingLaunch || _closed) return;
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
        if (!_settingsReady || _savingMod || _busy || _pendingLaunch || _closed) return;
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
        if (!_settingsReady || _savingMod || _busy || _pendingLaunch || _closed || _settings.SearchRoots.Count == 0) return;
        if (GameCatalog.All.Any(g => _sessions.IsRunning(g.Id))) { Notice("Rescan is available after the current game session ends."); return; }
        _busy = true;
        RefreshModControls();
        _scanCancellation = new CancellationTokenSource();
        CancelRescanButton.IsVisible = true;
        RescanMenu.IsEnabled = RescanButton.IsEnabled = false;
        Notice("Scanning your game folders...");
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
            Notice((found.Count == 0 ? "game not found" : $"Rescan complete - {_cards.Count} game{(_cards.Count == 1 ? "" : "s")} available." + (limited ? " A search limit was reached; choose a folder closer to the games." : skipped > 0 ? " Some protected folders could not be read." : "")) + (saved ? "" : " Settings could not be saved for next time."));
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
                RefreshModControls();
            }
        }
    }

    private static string ScanFootnote(DiscoveryResult result) => result.LimitReached
        ? " A search limit was reached; add a closer folder if a game is missing."
        : result.SkippedDirectories > 0 ? " Some protected folders were skipped." : "";
    private void CancelRescan_Click(object? sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async void PlayGame_Click(object? sender, RoutedEventArgs e)
    {
        if (!_settingsReady || _savingMod || _busy || _pendingLaunch || _closed || sender is not Button { Tag: GameInstallation installation }) return;
        _pendingLaunch = true;
        RefreshModControls();
        Notice($"Starting {GameCatalog.Get(installation.GameId).Title}...");
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
                RefreshModControls();
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
            card.SetLaunchPending(!_settingsReady || _savingMod || _pendingLaunch);
        }
    }

    private void RefreshModControls()
    {
        SkipSplashScreensCheckBox.IsChecked = _settings.SkipSplashScreens;
        SkipSplashScreensCheckBox.IsEnabled = _settingsReady && !_savingMod && !_pendingLaunch && !_busy;
        SkipCharacterInteractionsCheckBox.IsChecked = _settings.SkipCharacterInteractions;
        SkipCharacterInteractionsCheckBox.IsEnabled = SkipSplashScreensCheckBox.IsEnabled;
        OriginalAspectRatioCheckBox.IsChecked = _settings.RestoreOriginalAspectRatio;
        OriginalAspectRatioCheckBox.IsEnabled = SkipSplashScreensCheckBox.IsEnabled;
        HdCardArtworkCheckBox.IsChecked = _settings.HdCardArtwork;
        HdCardArtworkCheckBox.IsEnabled = SkipSplashScreensCheckBox.IsEnabled && (_hdUnavailableReason is null || _settings.HdCardArtwork);
        HdCardArtworkDescription.Text = _hdUnavailableReason is null
            ? "Replace fully visible preview cards with local HD masters in a larger 4:3 window. Applies to all three games on the next launch. In-game validation is pending."
            : "Unavailable: " + _hdUnavailableReason;
        foreach (var card in _cards) card.SetLaunchPending(!_settingsReady || _savingMod || _pendingLaunch);
    }

    private enum ModSelection { SkipSplashScreens, SkipCharacterInteractions, OriginalAspectRatio, HdCardArtwork }

    private async void SkipSplashScreens_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.SkipSplashScreens);

    private async void SkipCharacterInteractions_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.SkipCharacterInteractions);

    private async void OriginalAspectRatio_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.OriginalAspectRatio);

    private async void HdCardArtwork_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.HdCardArtwork);

    private bool GetModSelection(ModSelection mod) => mod switch
    {
        ModSelection.SkipSplashScreens => _settings.SkipSplashScreens,
        ModSelection.SkipCharacterInteractions => _settings.SkipCharacterInteractions,
        ModSelection.OriginalAspectRatio => _settings.RestoreOriginalAspectRatio,
        ModSelection.HdCardArtwork => _settings.HdCardArtwork,
        _ => throw new ArgumentOutOfRangeException(nameof(mod))
    };

    private void SetModSelection(ModSelection mod, bool selected)
    {
        switch (mod)
        {
            case ModSelection.SkipSplashScreens: _settings.SkipSplashScreens = selected; break;
            case ModSelection.SkipCharacterInteractions: _settings.SkipCharacterInteractions = selected; break;
            case ModSelection.OriginalAspectRatio: _settings.RestoreOriginalAspectRatio = selected; break;
            case ModSelection.HdCardArtwork: _settings.HdCardArtwork = selected; break;
            default: throw new ArgumentOutOfRangeException(nameof(mod));
        }
    }

    private async Task SaveModSelectionAsync(ModSelection mod)
    {
        if (!_settingsReady || _savingMod || _pendingLaunch || _busy || _closed)
        {
            if (!_closed) RefreshModControls();
            return;
        }
        var (checkbox, name) = mod switch
        {
            ModSelection.SkipSplashScreens => (SkipSplashScreensCheckBox, "Skip Splash Screens"),
            ModSelection.SkipCharacterInteractions => (SkipCharacterInteractionsCheckBox, "Skip character interactions"),
            ModSelection.OriginalAspectRatio => (OriginalAspectRatioCheckBox, "Original aspect ratio"),
            ModSelection.HdCardArtwork => (HdCardArtworkCheckBox, "HD card previews"),
            _ => throw new ArgumentOutOfRangeException(nameof(mod))
        };
        var previous = GetModSelection(mod);
        var selected = checkbox.IsChecked == true;
        if (mod == ModSelection.HdCardArtwork && selected && _hdUnavailableReason is not null)
        { RefreshModControls(); Notice(_hdUnavailableReason); return; }
        SetModSelection(mod, selected);
        _savingMod = true;
        RefreshModControls();
        try
        {
            if (await SaveSettingsAsync())
                Notice($"{name} {(selected ? "enabled" : "disabled")} for all three games on their next launch.");
            else
            {
                SetModSelection(mod, previous);
                Notice("The mod setting could not be saved. Your previous selection was kept.");
            }
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

    private async Task<bool> SaveSettingsAsync()
    {
        try { await _store.SaveAsync(_settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice("Your settings are available now, but could not be saved for next time."); return false; }
    }

    private async void Preferences_Click(object? sender, RoutedEventArgs e)
    {
        if (!_settingsReady || _savingMod || _busy || _pendingLaunch || _closed) return;
        var result = await new PreferencesDialog(_settings).ShowDialog<PreferencesResult?>(this);
        if (result is null || _closed) return;
        _settings.MinimizeOnLaunch = result.MinimizeOnLaunch;
        _settings.WineExecutable = result.WineExecutable;
        _settings.HdPackDirectory = result.HdPackDirectory;
        await RefreshHdAvailabilityAsync();
        if (_closed) return;
        RefreshModControls();
        if (await SaveSettingsAsync()) Notice("Preferences saved.");
    }

    private async Task RefreshHdAvailabilityAsync()
    {
        _hdUnavailableReason = await HdCardArtworkAvailability.GetUnavailableReasonAsync(_settings.HdPackDirectory);
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
            "mods" => ("Mods", ""),
            "collection" => ("Collection", "Card collection support is in development."),
            "locations" => ("Game locations", "Manage the folders searched for games."),
            "about" => ("About", "Power of Chaos Launcher"),
            _ => ("Games", "Your installed Power of Chaos games.")
        };
        PageSubtitle.IsVisible = page != "mods";
    }
}
