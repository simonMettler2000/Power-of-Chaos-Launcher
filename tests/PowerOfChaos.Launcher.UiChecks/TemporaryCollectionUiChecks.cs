using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;
using PowerOfChaos.Launcher.Core.Storage;
using PowerOfChaos.Launcher.TestFixtures;
using PowerOfChaos.Launcher.ViewModels;
using PowerOfChaos.Launcher.Views;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class TemporaryCollectionUiChecks
{
    private const string OnMessage = "Temporary all-cards mod is active. Turning it off restores your original system.dat.";
    private const string OffMessage = "Temporary all-cards mod is off.";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll(string runRoot, Action<string, Action> check, Action<TimeSpan> pump,
        Action<Func<bool>, string> wait, Action<Window, string> capture)
    {
        var previousData = Environment.GetEnvironmentVariable("POC_LAUNCHER_DATA_DIR");
        var previousLanguage = LauncherText.Current.Language;
        var root = Path.Combine(runRoot, "temporary-collection-ui");
        var data = Path.Combine(root, "launcher-data");
        var vault = Path.Combine(root, "private-vault");
        var shared = Path.Combine(root, "shared", "system.dat");
        var inspected = Path.Combine(root, "inspected-only", "system.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inspected)!);
        var original = CollectionFixtures.Save(
            [(CollectionFixtures.Slot(0), (byte)1), (CollectionFixtures.Slot(200), (byte)2), (CollectionFixtures.Slot(700), (byte)1)],
            [17, 29, 43, 59, 61, 79, 83, 97], deckFlags: [CollectionFixtures.Slot(0), CollectionFixtures.Slot(700)]);
        var inspectionOriginal = CollectionFixtures.Save([(CollectionFixtures.Slot(1), (byte)2)]);
        File.WriteAllBytes(shared, original);
        File.WriteAllBytes(inspected, inspectionOriginal);
        var gameRoot = Path.Combine(root, "games");
        var installations = GameCatalog.All.Select(game => WriteGame(Path.Combine(gameRoot, game.Id), game)
            with { DiscoveryRoot = gameRoot }).ToList();
        var settings = new LauncherSettings
        {
            Installations = installations, SearchRoots = [Path.Combine(root, "games")],
            MinimizeOnLaunch = false, WineExecutable = "synthetic runner path", CollectionSavePath = inspected,
            SkipCharacterInteractions = true, CardLanguage = "en"
        };
        new SettingsStore(data).SaveAsync(settings).GetAwaiter().GetResult();
        var settingsPath = Path.Combine(data, "launcher-settings.json");
        var settingsOriginal = File.ReadAllBytes(settingsPath);
        using var settingsDocument = JsonDocument.Parse(settingsOriginal);
        var settingsFields = settingsDocument.RootElement.Clone();
        using var poolEntered = new ManualResetEventSlim();
        using var allowPool = new ManualResetEventSlim(initialState: true);
        var running = false;
        var blockPool = false;
        string? failAt = null;
        var automatic = new CardCollectionReader(_ =>
        {
            if (blockPool)
            {
                poolEntered.Set();
                if (!allowPool.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Synthetic card-pool barrier timed out.");
            }
            return [shared];
        });
        TemporaryCollectionService Profiles() => new(vault, automatic, () => running,
            checkpoint: phase => { if (phase == failAt) throw new IOException("Synthetic interrupted transaction."); });
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("POC_LAUNCHER_DATA_DIR", data);
            window = Open(Profiles(), automatic, pump, wait);
            OpenMods(window, pump);
            check("temporary collection UI starts unchecked with platform status and a copyable backup location", () =>
            {
                Require(Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == false, "Temporary cards unexpectedly defaulted to on.");
                var expectedStatus = LauncherText.Current.Translate(OperatingSystem.IsWindows()
                    ? OffMessage : "Temporary all cards is available on Windows only.");
                Require(Find<SelectableTextBlock>(window, "TemporaryCollectionStatus").Text == expectedStatus, "Initial mod status is incorrect.");
                var backups = Find<SelectableTextBlock>(window, "TemporaryCollectionBackups");
                Require(backups.Text == LauncherText.Current.Format("Backups: {0}", vault), "The private backup location is missing or points outside the fixture.");
                backups.SelectAll();
                Require(backups.SelectedText == backups.Text, "Backup path cannot be selected in full.");
                backups.ClearSelection();
                Require(!Find<Button>(window, "RestoreCollectionButton").IsVisible, "Restore recovery action appears before activation.");
                Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(original), "Opening the launcher changed the game save.");
                Require(File.ReadAllBytes(settingsPath).AsSpan().SequenceEqual(settingsOriginal), "Opening the mod changed launcher preferences.");
            });

            if (!OperatingSystem.IsWindows())
            {
                check("temporary collection mod is unavailable in an unverified Wine environment", () =>
                    Require(!Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsEnabled,
                        "The temporary mod is offered without a supported native process guard."));
                return;
            }

            check("temporary collection activation blocks Play and competing changes until the save transaction finishes", () =>
            {
                blockPool = true;
                allowPool.Reset();
                Toggle(window, true);
                wait(() => poolEntered.IsSet, "Activation did not enter the synthetic card-pool read.");
                try
                {
                    Require(!Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsEnabled,
                        "A second temporary-profile change can start during activation.");
                    Require(!Find<CheckBox>(window, "SkipSplashScreensCheckBox").IsEnabled
                        && !Find<ComboBox>(window, "CardLanguageSelector").IsEnabled,
                        "Competing settings remain enabled during activation.");
                    Require(Cards(window).All(card => !card.CanPlay), "A game can start while its collection save is changing.");
                    Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(original),
                        "The game save was changed before its card pool was established.");
                }
                finally { blockPool = false; allowPool.Set(); }
                WaitIdle(window, wait);
                wait(() => Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == true
                    && Find<TextBlock>(window, "UniqueCardCount").Text == "771/771", "Activation did not unlock the complete shared pool.");
            });

            check("temporary collection uses the actual shared save and shows accurate totals for all three games", () =>
            {
                var cards = Cards(window);
                Require(cards.Count == 3 && cards.Select(card => card.CollectionCount).SequenceEqual(["155/155", "466/466", "771/771"]),
                    "Games counters do not show the complete cumulative card pools.");
                var rows = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().ToArray();
                Require(rows.Select(row => row.Count).SequenceEqual(["155/155", "466/466", "771/771"]),
                    "Collection counters differ from Games while temporary cards are active.");
                Require(Find<SelectableTextBlock>(window, "TemporaryCollectionStatus").Text == OnMessage,
                    "Active mod status is incorrect.");
                Require(File.ReadAllBytes(inspected).AsSpan().SequenceEqual(inspectionOriginal),
                    "The separately selected inspection save was modified by the mod.");
                Require(File.ReadAllBytes(settingsPath).AsSpan().SequenceEqual(settingsOriginal),
                    "Activation changed unrelated launcher preferences or created a second mod authority.");
                var backups = Directory.GetFiles(vault, "original.system.dat", SearchOption.AllDirectories);
                Require(backups.Length == 1 && File.ReadAllBytes(backups[0]).AsSpan().SequenceEqual(original),
                    "The original complete save was not preserved exactly before activation.");
            });

            window.Close();
            pump(TimeSpan.FromMilliseconds(100));
            window = Open(Profiles(), automatic, pump, wait);
            OpenMods(window, pump);
            check("temporary collection activation survives a launcher restart using its durable journal", () =>
            {
                wait(() => Find<TextBlock>(window, "UniqueCardCount").Text == "771/771", "Restarted launcher lost temporary collection totals.");
                Require(Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == true,
                    "Restarted launcher did not load the authoritative active state.");
                Require(!File.ReadAllBytes(shared).AsSpan().SequenceEqual(original),
                    "Closing or opening the launcher silently restored the save.");
            });

            check("an externally running game prevents unticking and leaves the temporary profile intact", () =>
            {
                var temporary = File.ReadAllBytes(shared);
                running = true;
                try
                {
                    Toggle(window, false);
                    WaitIdle(window, wait);
                    Require(Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == true,
                        "A rejected switch displayed the mod as inactive.");
                    Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(temporary),
                        "A running game did not prevent save restoration.");
                    Require(Find<SelectableTextBlock>(window, "StatusText").Text?.Contains("Close all three games", StringComparison.Ordinal) == true,
                        "The blocked switch does not explain that the games must be closed.");
                }
                finally { running = false; }
            });

            foreach (var language in new[] { "de", "fr", "it", "es", "ja", "en" })
            {
                check("temporary collection labels and active status switch live and fit at minimum width in " + language, () =>
                {
                    var selector = Find<ComboBox>(window, "CardLanguageSelector");
                    selector.SelectedItem = CardLanguages.Get(language);
                    wait(() => selector.IsEnabled && LauncherText.Current.Language == language,
                        "Temporary mod UI language did not follow the saved selection.");
                    var text = LauncherText.Current;
                    var row = Find<Grid>(window, "TemporaryAllCardsRow");
                    Require(row.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Text == text.Translate("Temporary all cards")),
                        "Temporary mod title did not translate live.");
                    var status = Find<SelectableTextBlock>(window, "TemporaryCollectionStatus");
                    Require(status.Text == text.Translate(OnMessage), "Active mod status remained in the previous language.");
                    Require(language == "en" || status.Text != OnMessage, "The active status has no translation in " + language + ".");
                    Require(Find<SelectableTextBlock>(window, "TemporaryCollectionBackups").Text == text.Format("Backups: {0}", vault),
                        "Backup label did not translate, or its private path was translated.");
                    window.Width = 880; window.Height = 600;
                    row.BringIntoView();
                    pump(TimeSpan.FromMilliseconds(100));
                    Require(row.Bounds.Width > 0, "Temporary mod row has no layout width.");
                    foreach (var label in row.GetVisualDescendants().OfType<TextBlock>().Where(control => control.Bounds.Width > 0 && !string.IsNullOrEmpty(control.Text)))
                        RequireUnclipped(row, label, language);
                    capture(window, "temporary-collection-" + language + "-minimum.png");
                    CheckPreferences(settingsPath, settingsFields);
                });
            }

            check("unticking restores every original save byte and archives the temporary result", () =>
            {
                var temporary = File.ReadAllBytes(shared);
                Toggle(window, false);
                WaitIdle(window, wait);
                Require(Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == false,
                    "The mod did not display its restored inactive state.");
                Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(original),
                    "Unticking did not restore the complete original save byte for byte.");
                Require(Directory.GetFiles(vault, "temporary-*.system.dat", SearchOption.AllDirectories)
                    .Any(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(temporary)),
                    "Temporary progress was not archived before restoring the original.");
                Require(Directory.GetFiles(vault, "original.system.dat", SearchOption.AllDirectories)
                    .All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(original)), "An immutable original backup was changed.");
                Require(File.ReadAllBytes(inspected).AsSpan().SequenceEqual(inspectionOriginal), "The inspection save changed during restoration.");
                CheckPreferences(settingsPath, settingsFields);
            });

            check("interrupted activation exposes recovery, blocks every Play button and restores through the visible action", () =>
            {
                failAt = "enable-journal";
                Toggle(window, true);
                WaitNotSaving(window, wait);
                failAt = null;
                var restore = Find<Button>(window, "RestoreCollectionButton");
                Require(restore.IsVisible && restore.IsEnabled, "Interrupted activation has no usable restore action.");
                Require(Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == true
                    && !Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsEnabled,
                    "Interrupted state is displayed as a normal inactive toggle.");
                Require(Cards(window).All(card => !card.CanPlay), "A game can launch while save recovery is pending.");
                Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(original), "Fault checkpoint wrote the target before the planned swap.");
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitIdle(window, wait);
                Require(!restore.IsVisible && Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == false,
                    "Successful recovery did not return the mod to inactive.");
                Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(original), "Recovery did not restore the exact original save.");
                Require(Cards(window).All(card => card.CanPlay), "Play controls stayed blocked after successful recovery.");
                CheckPreferences(settingsPath, settingsFields);
                capture(window, "temporary-collection-restored.png");
            });

            check("a pending removal confirmation preserves locations activated by another launcher while the dialog is open", () =>
            {
                var remove = Call(window, "RemoveLocationAsync", gameRoot);
                wait(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "The remove-location confirmation did not open while the mod was off.");
                Profiles().SetEnabledAsync(true, installations).GetAwaiter().GetResult();
                var refresh = Call(window, "RefreshTemporaryCollectionAsync");
                wait(() => refresh.IsCompleted, "The externally activated profile did not refresh while confirmation was open.");
                refresh.GetAwaiter().GetResult();
                Require(Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == true,
                    "The confirmation regression did not establish an active profile before its final response.");
                window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
                wait(() => remove.IsCompleted, "The remove-location continuation did not complete after confirmation.");
                remove.GetAwaiter().GetResult();
                var current = Field<LauncherSettings>(window, "_settings");
                Require(current.Installations.SequenceEqual(installations)
                    && current.SearchRoots.SequenceEqual([gameRoot]),
                    "The pending confirmation removed a location or installation bound to an active temporary profile.");
                Require(Cards(window).Count == 3, "The blocked confirmation retired one of the three game rows.");
                var state = Profiles().ReadStateAsync().GetAwaiter().GetResult();
                Require(state.Enabled && !state.RecoveryRequired, "The blocked confirmation changed the active profile's journal.");
                Require(File.ReadAllBytes(inspected).AsSpan().SequenceEqual(inspectionOriginal), "The blocked confirmation changed the inspection save.");
                CheckPreferences(settingsPath, settingsFields);
            });

            check("session completion clears a transient recovery block and Mods navigation refreshes external state without reactivation", () =>
            {
                using (var writer = new FileStream(shared, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var refresh = Call(window, "RefreshTemporaryCollectionAsync");
                    wait(() => refresh.IsCompleted, "The locked-save state refresh did not complete.");
                    refresh.GetAwaiter().GetResult();
                    Require(Field<TemporaryCollectionState>(window, "_temporaryState").RecoveryRequired,
                        "The save lock did not establish a transient recovery state for this regression.");
                    Require(Cards(window).All(card => !card.CanPlay) && Find<Button>(window, "RestoreCollectionButton").IsVisible,
                        "A transient unreadable active save did not block Play and expose recovery.");
                }
                // Run the real event handler used after a tracked process exits; do not reactivate the window.
                typeof(MainWindow).GetMethod("SessionsChanged", Private)!.Invoke(window, [null, EventArgs.Empty]);
                wait(() => Field<TemporaryCollectionState>(window, "_temporaryState") is { Enabled: true, RecoveryRequired: false }
                    && Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsEnabled
                    && Cards(window).All(card => card.CanPlay)
                    && Cards(window).Select(card => card.CollectionCount).SequenceEqual(["155/155", "466/466", "771/771"])
                    && Find<TextBlock>(window, "UniqueCardCount").Text == "771/771",
                    "Session completion retained an obsolete recovery state or stale Play/card counters after the save was unlocked.");
                Require(!Find<Button>(window, "RestoreCollectionButton").IsVisible
                    && Find<SelectableTextBlock>(window, "TemporaryCollectionStatus").Text == OnMessage,
                    "Recovered session controls still show a recovery action or stale status.");
                Profiles().SetEnabledAsync(false, installations).GetAwaiter().GetResult();
                Find<Button>(window, "ModsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                wait(() => Field<TemporaryCollectionState>(window, "_temporaryState") is { Enabled: false, RecoveryRequired: false }
                    && Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsChecked == false,
                    "Entering Mods retained another launcher's old active state.");
                Require(Find<SelectableTextBlock>(window, "TemporaryCollectionStatus").Text == OffMessage,
                    "Entering Mods did not render the fresh inactive journal state.");
                Require(File.ReadAllBytes(shared).AsSpan().SequenceEqual(original), "External fixture cleanup failed to restore the exact baseline.");
                CheckPreferences(settingsPath, settingsFields);
                capture(window, "temporary-collection-session-recovered.png");
            });
        }
        finally
        {
            blockPool = false;
            allowPool.Set();
            window?.Close();
            pump(TimeSpan.FromMilliseconds(100));
            Environment.SetEnvironmentVariable("POC_LAUNCHER_DATA_DIR", previousData);
            LauncherText.Current.SetLanguage(previousLanguage);
        }
    }

    private static MainWindow Open(TemporaryCollectionService profiles, CardCollectionReader reader,
        Action<TimeSpan> pump, Action<Func<bool>, string> wait)
    {
        var window = new MainWindow(profiles);
        // Keep even ordinary Collection refreshes inside these authored fixtures, independent of registry CommonDir.
        typeof(MainWindow).GetField("_collections", Private)!.SetValue(window, reader);
        window.Show();
        pump(TimeSpan.FromMilliseconds(100));
        wait(() => Find<ComboBox>(window, "CardLanguageSelector").IsEnabled, "Temporary collection fixture window did not initialize.");
        return window;
    }

    private static void OpenMods(MainWindow window, Action<TimeSpan> pump)
    {
        Find<Button>(window, "ModsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<Grid>(window, "TemporaryAllCardsRow").BringIntoView();
        pump(TimeSpan.FromMilliseconds(80));
    }

    private static void Toggle(MainWindow window, bool enabled)
    {
        var toggle = Find<CheckBox>(window, "TemporaryAllCardsCheckBox");
        Require(toggle.IsEnabled, "The temporary collection toggle is disabled before the test action.");
        toggle.IsChecked = enabled;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void WaitIdle(MainWindow window, Action<Func<bool>, string> wait)
    {
        try
        {
            wait(() => Find<CheckBox>(window, "TemporaryAllCardsCheckBox").IsEnabled
                && Find<ComboBox>(window, "CardLanguageSelector").IsEnabled,
                "Temporary collection transaction did not finish or returned an unexpected recovery state.");
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("Temporary collection did not become idle. Mod status: "
                + Find<SelectableTextBlock>(window, "TemporaryCollectionStatus").Text + "; notice: "
                + Find<SelectableTextBlock>(window, "StatusText").Text, exception);
        }
    }

    private static void WaitNotSaving(MainWindow window, Action<Func<bool>, string> wait)
        => wait(() => !(bool)typeof(MainWindow).GetField("_savingMod", Private)!.GetValue(window)!,
            "Temporary collection transaction did not leave its saving state.");

    private static List<GameCardViewModel> Cards(MainWindow window)
        => Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>().ToList();

    private static Task Call(MainWindow window, string method, params object?[] arguments)
        => (Task)typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments)!;

    private static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    private static void CheckPreferences(string settingsPath, JsonElement expected)
    {
        using var actual = JsonDocument.Parse(File.ReadAllText(settingsPath));
        foreach (var field in expected.EnumerateObject().Where(field => field.Name != "CardLanguage"))
            Require(actual.RootElement.GetProperty(field.Name).GetRawText() == field.Value.GetRawText(),
                "Temporary collection UI changed unrelated preference " + field.Name + ".");
        Require(actual.RootElement.EnumerateObject().Select(property => property.Name)
            .SequenceEqual(expected.EnumerateObject().Select(property => property.Name)), "The UI persisted a second temporary-mod authority in preferences.");
    }

    private static void RequireUnclipped(Grid row, TextBlock label, string language)
    {
        var position = label.TranslatePoint(default, row) ?? throw new InvalidOperationException("Cannot locate a temporary mod label.");
        Require(position.X >= -1 && position.X + label.Bounds.Width <= row.Bounds.Width + 1,
            "A translated temporary mod label extends outside its row in " + language + ".");
        var expected = new TextBlock
        {
            Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight,
            TextWrapping = label.TextWrapping, LineHeight = label.LineHeight
        };
        expected.Measure(new Size(label.Bounds.Width, double.PositiveInfinity));
        Require(expected.DesiredSize.Height <= label.Bounds.Height + 1,
            "A translated temporary mod label is vertically clipped in " + language + ".");
    }

    private static GameInstallation WriteGame(string root, GameDefinition game)
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, game.ExecutableName);
        var data = Path.Combine(root, "data.dat");
        var voice = Path.Combine(root, "Voice.dat");
        File.WriteAllText(executable, "Launcher-authored inert UI fixture. Never execute.");
        CollectionFixtures.WriteArchive(data);
        CollectionFixtures.WriteArchive(voice, []);
        return new(game.Id, executable, data, voice, root);
    }

    private static T Find<T>(Control parent, string name) where T : Control => parent.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing temporary collection control: " + name);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
