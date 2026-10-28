using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;
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
    private readonly CardCollectionReader _collections = new();
    private readonly NativeCardArtworkReader _cardArtwork = new();
    private readonly ObservableCollection<CollectionGameViewModel> _collectionRows = [];
    private readonly Dictionary<string, CancellationTokenSource> _galleryLoads = [];
    private readonly HashSet<string> _galleryAttempted = [];
    private readonly ObservableCollection<GameCardViewModel> _cards = [];
    private readonly Dictionary<GameCardViewModel, PortraitPixels> _portraitSources = [];
    private LauncherSettings _settings = new();
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _portraitCancellation;
    private CancellationTokenSource _portraitResizeCancellation = new();
    private CancellationTokenSource? _collectionCancellation;
    private bool _busy;
    private bool _closeWhenIdle;
    private bool _closed;
    private bool _pendingLaunch;
    private bool _settingsReady;
    private bool _savingMod;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public MainWindow()
    {
        InitializeComponent();
        GameCards.ItemsSource = _cards;
        foreach (var game in GameCatalog.All)
        {
            var row = new CollectionGameViewModel(new(game.Id, CollectionSnapshot.TotalFor(game.Id), null, "Game not added"));
            row.PropertyChanged += CollectionRowChanged;
            _collectionRows.Add(row);
        }
        CollectionGames.ItemsSource = _collectionRows;
        _sessions.SessionsChanged += SessionsChanged;
        Opened += async (_, _) => await InitializeAsync();
        ScalingChanged += (_, _) => RefreshPortraitThumbnails();
        Activated += async (_, _) => { if (_settingsReady && !_closed) await RefreshCollectionAsync(); };
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
            _portraitResizeCancellation.Cancel();
            _portraitResizeCancellation.Dispose();
            _portraitSources.Clear();
            _collectionCancellation?.Cancel();
            _collectionCancellation?.Dispose();
            ResetGalleryArtwork();
            foreach (var row in _collectionRows)
            {
                row.PropertyChanged -= CollectionRowChanged;
                row.Dispose();
            }
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
            CollectionSaveInput.Text = _settings.CollectionSavePath;
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
        ResetGalleryArtwork();
        _portraitCancellation?.Cancel();
        _portraitCancellation?.Dispose();
        _portraitCancellation = new CancellationTokenSource();
        _portraitResizeCancellation.Cancel();
        _portraitResizeCancellation.Dispose();
        _portraitResizeCancellation = new CancellationTokenSource();
        _portraitSources.Clear();
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
        _ = RefreshCollectionAsync();
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
                {
                    _portraitSources[card] = pixels;
                    _ = UpdatePortraitThumbnailAsync(card, pixels, _portraitResizeCancellation.Token);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or NotSupportedException)
        {
            // Artwork is optional: retain a usable game row when it cannot be loaded.
        }
    }

    private void RefreshPortraitThumbnails()
    {
        if (_closed) return;
        _portraitResizeCancellation.Cancel();
        _portraitResizeCancellation.Dispose();
        _portraitResizeCancellation = new CancellationTokenSource();
        foreach (var (card, pixels) in _portraitSources)
            _ = UpdatePortraitThumbnailAsync(card, pixels, _portraitResizeCancellation.Token);
    }

    private async Task UpdatePortraitThumbnailAsync(GameCardViewModel card, PortraitPixels pixels,
        CancellationToken cancellationToken)
    {
        // Prepare the actual display-pixel footprint off the UI thread, including fractional monitor scaling.
        var scale = double.IsFinite(RenderScaling) && RenderScaling > 0 ? Math.Min(RenderScaling, 8) : 1;
        var width = (int)Math.Ceiling(112 * scale);
        var height = (int)Math.Ceiling(104 * scale);
        try
        {
            var thumbnail = await Task.Run(() => PortraitThumbnail.Create(pixels, width, height, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closed && !cancellationToken.IsCancellationRequested && _cards.Contains(card)
                    && _portraitSources.TryGetValue(card, out var current) && ReferenceEquals(current, pixels))
                    card.SetPortrait(thumbnail);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Keep optional artwork failures from disabling a game row.
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
        Notice($"Starting {GameCatalog.Get(installation.GameId).Title}…");
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
            await RefreshCollectionAsync();
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
        foreach (var card in _cards) card.SetLaunchPending(!_settingsReady || _savingMod || _pendingLaunch);
    }

    private enum ModSelection { SkipSplashScreens, SkipCharacterInteractions, OriginalAspectRatio }

    private async void SkipSplashScreens_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.SkipSplashScreens);

    private async void SkipCharacterInteractions_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.SkipCharacterInteractions);

    private async void OriginalAspectRatio_Click(object? sender, RoutedEventArgs e) => await SaveModSelectionAsync(ModSelection.OriginalAspectRatio);

    private bool GetModSelection(ModSelection mod) => mod switch
    {
        ModSelection.SkipSplashScreens => _settings.SkipSplashScreens,
        ModSelection.SkipCharacterInteractions => _settings.SkipCharacterInteractions,
        ModSelection.OriginalAspectRatio => _settings.RestoreOriginalAspectRatio,
        _ => throw new ArgumentOutOfRangeException(nameof(mod))
    };

    private void SetModSelection(ModSelection mod, bool selected)
    {
        switch (mod)
        {
            case ModSelection.SkipSplashScreens: _settings.SkipSplashScreens = selected; break;
            case ModSelection.SkipCharacterInteractions: _settings.SkipCharacterInteractions = selected; break;
            case ModSelection.OriginalAspectRatio: _settings.RestoreOriginalAspectRatio = selected; break;
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
            _ => throw new ArgumentOutOfRangeException(nameof(mod))
        };
        var previous = GetModSelection(mod);
        var selected = checkbox.IsChecked == true;
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
        if (await SaveSettingsAsync()) Notice("Preferences saved.");
    }

    private void Notice(string message)
    {
        if (_closed) return;
        StatusText.Text = message;
        StatusText.ClearSelection();
        CopyStatusButton.Content = "Copy";
        StatusBanner.IsVisible = true;
    }

    private async void CopyStatus_Click(object? sender, RoutedEventArgs e)
    {
        var message = StatusText.Text;
        if (_closed || string.IsNullOrEmpty(message)) return;
        try
        {
            if (Clipboard is not { } clipboard) { CopyStatusButton.Content = "Select text"; return; }
            await clipboard.SetTextAsync(message);
            if (!_closed && StatusText.Text == message) CopyStatusButton.Content = "Copied";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            // Keep the diagnostic available even when another application owns the clipboard.
            if (!_closed && StatusText.Text == message) CopyStatusButton.Content = "Select text";
        }
    }
    private void Library_Click(object? sender, RoutedEventArgs e) => ShowPage("library");
    private void Mods_Click(object? sender, RoutedEventArgs e) => ShowPage("mods");
    private async void Collection_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage("collection");
        await RefreshCollectionAsync();
    }
    private async void RefreshCollection_Click(object? sender, RoutedEventArgs e)
    {
        ResetGalleryArtwork();
        await RefreshCollectionAsync();
    }

    private async Task RefreshCollectionAsync()
    {
        if (_closed) return;
        _collectionCancellation?.Cancel();
        _collectionCancellation?.Dispose();
        var cancellation = _collectionCancellation = new CancellationTokenSource();
        RefreshCollectionButton.IsEnabled = false;
        try
        {
            var snapshot = await _collections.ReadAsync(_settings.Installations.ToArray(),
                _settings.CollectionSavePath, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested) return;
            foreach (var row in _collectionRows)
            {
                if (row.UpdateProgress(snapshot.Games.Single(p => p.GameId == row.Progress.GameId)))
                {
                    CancelGalleryLoad(row.Progress.GameId);
                    _galleryAttempted.Remove(row.Progress.GameId);
                }
                if (row.IsExpanded && !_galleryAttempted.Contains(row.Progress.GameId)) StartGalleryLoad(row);
            }
            UniqueCardCount.Text = $"{snapshot.UniqueCards?.ToString() ?? "—"}/{CollectionSnapshot.MaximumUniqueCards}";
            UniqueCollectionProgress.IsVisible = snapshot.UniqueCards.HasValue;
            UniqueCollectionProgress.Value = snapshot.UniqueCards ?? 0;
            CollectionStatus.Text = snapshot.Message;
            CollectionSaveSource.Text = snapshot.SavePath is null ? "Automatic location" : Path.GetFileName(snapshot.SavePath);
            CollectionSaveSource.SetValue(ToolTip.TipProperty, snapshot.SavePath);
            foreach (var card in _cards)
                card.SetCollection(snapshot.Games.Single(p => p.GameId == card.Installation.GameId));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!_closed && ReferenceEquals(_collectionCancellation, cancellation))
                RefreshCollectionButton.IsEnabled = true;
        }
    }

    private void CollectionRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || e.PropertyName != nameof(CollectionGameViewModel.IsExpanded)
            || sender is not CollectionGameViewModel row) return;
        if (row.IsExpanded) StartGalleryLoad(row);
        else
        {
            CancelGalleryLoad(row.Progress.GameId);
            _galleryAttempted.Remove(row.Progress.GameId);
            row.ClearArtwork();
            row.SetGalleryStatus("Expand to view your original card images.");
        }
    }

    private void CancelGalleryLoad(string gameId)
    {
        if (!_galleryLoads.Remove(gameId, out var cancellation)) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ResetGalleryArtwork()
    {
        foreach (var gameId in _galleryLoads.Keys.ToArray()) CancelGalleryLoad(gameId);
        _galleryAttempted.Clear();
        foreach (var row in _collectionRows) row.ClearArtwork();
    }

    private void StartGalleryLoad(CollectionGameViewModel row)
    {
        CancelGalleryLoad(row.Progress.GameId);
        if (_closed || !row.IsExpanded) return;
        _galleryAttempted.Add(row.Progress.GameId);
        var installation = _settings.Installations.FirstOrDefault(i => i.GameId == row.Progress.GameId);
        if (installation is null || !row.HasCards)
        {
            row.SetGalleryStatus(row.HasCount ? "No cards are available for this game." : "Choose a readable collection save to see owned and missing cards.");
            return;
        }
        var cancellation = new CancellationTokenSource();
        _galleryLoads.Add(row.Progress.GameId, cancellation);
        row.SetGalleryStatus("Loading original card images…");
        _ = LoadGalleryAsync(row, installation, row.ArtworkVersion, cancellation);
    }

    private async Task LoadGalleryAsync(CollectionGameViewModel row, GameInstallation installation, int version,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool Current() => !_closed && !token.IsCancellationRequested && row.IsExpanded && row.ArtworkVersion == version
            && _galleryLoads.TryGetValue(installation.GameId, out var active) && ReferenceEquals(active, cancellation);
        try
        {
            var batch = await _cardArtwork.LoadAsync(installation, row.Cards.Select(card => card.Slot).ToArray(), token);
            for (var start = 0; start < batch.Images.Count; start += 32)
            {
                if (!Current()) return;
                foreach (var image in batch.Images.Skip(start).Take(32)) row.SetArtwork(image);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            if (Current()) row.SetGalleryStatus(batch.Images.Count == batch.RequestedCount
                ? "Original card size. Hover for the card's game; clicking keeps it small."
                : "Some original card images are unavailable. Ownership counts are still shown.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            if (Current()) row.SetGalleryStatus("Original card images could not be read. Check this game's location.");
        }
        finally
        {
            if (_galleryLoads.TryGetValue(installation.GameId, out var active) && ReferenceEquals(active, cancellation))
            {
                _galleryLoads.Remove(installation.GameId);
                cancellation.Dispose();
            }
        }
    }

    private async void BrowseCollectionSave_Click(object? sender, RoutedEventArgs e)
    {
        if (!_settingsReady || _savingMod || _pendingLaunch || _closed) return;
        try
        {
            if (!StorageProvider.CanOpen) { CollectionStatus.Text = "Paste the path to system.dat and select Use path."; return; }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose your collection save", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Power of Chaos save") { Patterns = ["system.dat", "*.dat"] }]
            });
            if (_closed || files.Count == 0) return;
            if (files[0].TryGetLocalPath() is { } path) await SelectCollectionSaveAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!_closed) CollectionStatus.Text = "The file picker could not open. Paste the path to system.dat instead.";
        }
    }

    private async void UseCollectionSave_Click(object? sender, RoutedEventArgs e)
        => await SelectCollectionSaveAsync(CollectionSaveInput.Text?.Trim().Trim('"'));
    private async void AutomaticCollectionSave_Click(object? sender, RoutedEventArgs e)
        => await SelectCollectionSaveAsync(null);

    private async Task SelectCollectionSaveAsync(string? path)
    {
        if (!_settingsReady || _savingMod || _busy || _pendingLaunch || _closed) return;
        if (!string.IsNullOrWhiteSpace(path) &&
            (path.Length > 32768 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)))
        {
            CollectionStatus.Text = "Enter the full path to system.dat.";
            return;
        }
        _savingMod = true;
        RefreshModControls();
        try
        {
            _settings.CollectionSavePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            CollectionSaveInput.Text = _settings.CollectionSavePath;
            await SaveSettingsAsync();
            if (!_closed) await RefreshCollectionAsync();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            if (!_closed) CollectionStatus.Text = "This save path is not valid.";
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
            "collection" => ("Collection", "Your cards across all three games."),
            "locations" => ("Game locations", "Manage the folders searched for games."),
            "about" => ("About", "Power of Chaos Launcher"),
            _ => ("Games", "Your installed Power of Chaos games.")
        };
        PageSubtitle.IsVisible = page != "mods";
    }
}
