using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.Views;

public partial class MainWindow
{
    private readonly PlayerProfiles _profiles;
    private PlayerProfileSelection _profileSelection = new(PlayerProfiles.BaseId, [new(PlayerProfiles.BaseId, "Player")]);
    private bool _profilesReadable = true;
    private bool _renderingProfiles;
    private int _profileRefresh;
    private readonly List<Button> _profileResetButtons = [];

    private bool ProfileActionsAvailable =>
        _settingsReady && _profilesReadable && !_closed && !_busy && !_savingMod && !_pendingLaunch && !_sessions.AnyRunning;
    private string? DisplayedCollectionSave => _temporaryState.Enabled ? _temporaryState.SavePath : _settings.CollectionSavePath;

    private void ReadProfiles()
    {
        try
        {
            var state = _profiles.Read();
            _profilesReadable = true;
            if (_profileSelection.ActiveId == state.ActiveId && _profileSelection.Profiles.SequenceEqual(state.Profiles)
                && ProfileSelector.ItemsSource is not null)
                return;
            _profileSelection = state;
            _renderingProfiles = true;
            try
            {
                ProfileSelector.ItemsSource = state.Profiles;
                ProfileSelector.SelectedItem = state.Active;
                ProfileNameInput.Text = state.Active.Name;
            }
            finally { _renderingProfiles = false; }
            ActiveProfileName.Text = state.Active.Name;
            ToolTip.SetTip(ActiveProfileName, state.Active.Name);
            CollectionSaveControls.IsVisible = state.Active.IsBase;
            ChooseCollectionSaveButton.IsVisible = state.Active.IsBase;
        }
        catch (Exception error) when (IsProfileError(error))
        {
            _profilesReadable = false;
            Notice(error.Message);
        }
        RefreshProfileControls();
    }

    private void RefreshProfileControls()
    {
        var canEdit = ProfileActionsAvailable;
        var canSwitch = canEdit && OperatingSystem.IsWindows() && !_temporaryState.Enabled && !_temporaryState.RecoveryRequired;
        ProfileSelector.IsEnabled = canSwitch;
        ProfileNameInput.IsEnabled = canEdit;
        RenameProfileButton.IsEnabled = canEdit;
        CreateProfileButton.IsEnabled = canSwitch && _profileSelection.Profiles.Count < PlayerProfiles.MaximumProfiles;
        ToolTip.SetTip(CreateProfileButton, _profileSelection.Profiles.Count >= PlayerProfiles.MaximumProfiles
            ? Texts.Translate("You can have up to five profiles.") : null);
        DeleteProfileButton.IsEnabled = canSwitch && _profileSelection.Profiles.Count > 1;
        foreach (var reset in _profileResetButtons) reset.IsEnabled = canSwitch;
    }

    private async void Profile_Click(object? sender, RoutedEventArgs args)
    {
        ShowPage("profile");
        ReadProfiles();
        await RefreshProfileStatsAsync();
    }

    private async void ProfileSelector_SelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_renderingProfiles || !ProfileActionsAvailable || ProfileSelector.SelectedItem is not PlayerProfile selected
            || selected.Id == _profileSelection.ActiveId) return;
        await ChangeProfileAsync(() => { _profiles.Select(selected.Id); return Task.CompletedTask; });
    }

    private async void CreateProfile_Click(object? sender, RoutedEventArgs args)
        => await ChangeProfileAsync(() => { _profiles.Create(ProfileNameInput.Text ?? ""); return Task.CompletedTask; });

    private async void RenameProfile_Click(object? sender, RoutedEventArgs args)
        => await ChangeProfileAsync(() =>
        {
            _profiles.Rename(_profileSelection.ActiveId, ProfileNameInput.Text ?? "");
            return Task.CompletedTask;
        });

    private async void DeleteProfile_Click(object? sender, RoutedEventArgs args)
    {
        if (!ProfileActionsAvailable || _profileSelection.Profiles.Count < 2) return;
        var id = _profileSelection.ActiveId;
        var dialog = new ConfirmDialog("Delete this profile?",
            _profileSelection.Active.IsBase
                ? "Delete the original launcher profile and its playtime? This cannot be reversed. The games’ shared save is kept outside the launcher."
                : "Are you sure you want to delete this profile? Its card progression and playtime will be removed. This cannot be reversed.",
            "Delete profile");
        if (await dialog.ShowDialog<bool>(this))
            await ChangeProfileAsync(() => { _profiles.Delete(id); return Task.CompletedTask; });
    }

    private async Task ResetProfileCardsAsync(GameInstallation game)
    {
        if (!ProfileActionsAvailable) return;
        var id = _profileSelection.ActiveId;
        var dialog = new ConfirmDialog(Texts.Format("Reset cards for {0}?", GameCatalog.Get(game.GameId).Title),
            "Cards introduced by this game will return to starter quantities and the active deck will be replaced by its starter deck. Shared card counts may change. Playtime is kept.",
            "Reset cards");
        if (await dialog.ShowDialog<bool>(this))
            await ChangeProfileAsync(() => _profiles.ResetCardsAsync(id, game, _settings.Installations.ToArray(), _settings.CollectionSavePath));
    }

    private async Task ChangeProfileAsync(Func<Task> change)
    {
        if (!ProfileActionsAvailable) return;
        _savingMod = true;
        RefreshModControls();
        try
        {
            await change();
            if (_closed) return;
            ReadProfiles();
            await RefreshSessionsAsync();
            await RefreshCollectionAsync();
            await RefreshProfileStatsAsync();
        }
        catch (Exception error) when (IsProfileError(error))
        {
            if (_closed) return;
            ReadProfiles();
            Notice(error.Message);
            _renderingProfiles = true;
            try { ProfileSelector.SelectedItem = _profileSelection.Active; }
            finally { _renderingProfiles = false; }
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

    private async Task RefreshProfileStatsAsync()
    {
        if (_page != "profile" || _closed || !_profilesReadable) return;
        var version = ++_profileRefresh;
        var selection = _profileSelection;
        var games = _settings.Installations.ToArray();
        try
        {
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            IReadOnlyDictionary<string, long> active = new Dictionary<string, long>();
            foreach (var profile in selection.Profiles)
            {
                var time = await _sessions.GetPlaytimeSecondsAsync(profile.Id);
                foreach (var (game, seconds) in time)
                    totals[game] = totals.GetValueOrDefault(game) + seconds;
                if (profile.Id == selection.ActiveId) active = time;
            }
            var collection = await _profiles.ReadCollectionAsync(selection.ActiveId, games, DisplayedCollectionSave);
            if (_closed || _page != "profile" || version != _profileRefresh || !ReferenceEquals(selection, _profileSelection)) return;
            ProfileTotalPlaytime.Text = FormatProfileTime(active.Values.Sum());
            AllProfilesTotalPlaytime.Text = FormatProfileTime(totals.Values.Sum());
            ProfileUniqueCards.Text = FormatProfileCount(collection.UniqueCards, CollectionSnapshot.MaximumUniqueCards);
            ProfileGameRows.Children.Clear();
            _profileResetButtons.Clear();
            foreach (var game in GameCatalog.All)
            {
                var installed = games.FirstOrDefault(candidate => candidate.GameId == game.Id);
                var row = collection.Games.Single(candidate => candidate.GameId == game.Id);
                var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
                heading.Children.Add(new TextBlock { Text = game.Title, FontSize = 15, FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                if (installed is not null)
                {
                    var reset = new Button { Content = Texts.Translate("Reset cards"), Classes = { "secondary" } };
                    reset.Click += async (_, _) => await ResetProfileCardsAsync(installed);
                    _profileResetButtons.Add(reset);
                    Grid.SetColumn(reset, 1);
                    heading.Children.Add(reset);
                }
                var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
                AddProfileMetric(metrics, 0, "Cards unlocked", FormatProfileCount(row.OwnedCards, row.TotalCards));
                AddProfileMetric(metrics, 1, "Profile playtime", FormatProfileTime(active.GetValueOrDefault(game.Id)));
                AddProfileMetric(metrics, 2, "All profiles’ playtime", FormatProfileTime(totals.GetValueOrDefault(game.Id)));
                ProfileGameRows.Children.Add(new Border { Classes = { "panel" }, Padding = new Thickness(18),
                    Child = new StackPanel { Spacing = 12, Children = { heading, metrics } } });
            }
            RefreshProfileControls();
        }
        catch (Exception error) when (IsProfileError(error))
        {
            if (!_closed && _page == "profile" && version == _profileRefresh) Notice(error.Message);
        }
    }

    private static void AddProfileMetric(Grid row, int column, string label, string value)
    {
        var metric = new StackPanel { Spacing = 4, Children =
            {
                new TextBlock { Text = Texts.Translate(label), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = value, FontWeight = FontWeight.SemiBold }
            } };
        Grid.SetColumn(metric, column);
        row.Children.Add(metric);
    }

    private static string FormatProfileTime(long seconds) => Texts.Format("{0}h {1:00}m", seconds / 3600, seconds / 60 % 60);
    private static string FormatProfileCount(int? cards, int total) => $"{cards?.ToString() ?? "—"}/{total}";
    private static bool IsProfileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException;
}
