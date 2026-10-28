using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;
using PowerOfChaos.Launcher.ViewModels;
using PowerOfChaos.Launcher.Views;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class LauncherLocalizationUiChecks
{
    private sealed record Expected(string Id, string Games, string Collection, string Settings, string Language);
    private static readonly Expected[] Languages =
    [
        new("de", "Spiele", "Sammlung", "Einstellungen", "Sprache"),
        new("fr", "Jeux", "Collection", "Paramètres", "Langue"),
        new("it", "Giochi", "Collezione", "Impostazioni", "Lingua"),
        new("es", "Juegos", "Colección", "Ajustes", "Idioma"),
        new("ja", "ゲーム", "コレクション", "設定", "言語"),
        new("en", "Games", "Collection", "Settings", "Language")
    ];

    public static void RunAll(MainWindow window, string data, Action<string, Action> check,
        Action<Window, string> capture, Action<TimeSpan> pump, Action<Func<bool>, string> wait)
    {
        var selector = Find<ComboBox>(window, "CardLanguageSelector");
        var settings = Find<Button>(window, "SettingsButton");
        var menu = (MenuFlyout)settings.Flyout!;
        using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
        var preserved = previous.RootElement.Clone();
        var text = LauncherText.Current;
        const string rawDetail = @"C:\Synthetic Games\Power of Chaos\Yugi the Destiny\Games\game.exe";
        const string sourceNotice = "Could not start the game: " + rawDetail;
        typeof(MainWindow).GetMethod("Notice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [sourceNotice]);
        try
        {
            foreach (var expected in Languages)
            {
                check("whole launcher switches live to " + expected.Id + " while preserving game names and inputs", () =>
                {
                    selector.SelectedItem = CardLanguages.Get(expected.Id);
                    wait(() => selector.IsEnabled && text.Language == expected.Id, "Interface language did not follow the saved dropdown.");
                    Require(Find<Button>(window, "LibraryNav").Content?.ToString() == expected.Games
                        && Find<Button>(window, "CollectionNav").Content?.ToString() == expected.Collection
                        && AutomationProperties.GetName(settings) == expected.Settings,
                        "Navigation or Settings stayed in the previous language.");
                    Require(window.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Text == expected.Language),
                        "Language label stayed in the previous language.");
                    Require(window.Title == "Power of Chaos" && window.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Text == "Power of Chaos"),
                        "The Power of Chaos name was translated.");
                    var status = Find<SelectableTextBlock>(window, "StatusText").Text!;
                    Require(status == text.Format("Could not start the game: {0}", rawDetail) && status.Contains(rawDetail, StringComparison.Ordinal),
                        "An existing diagnostic did not retranslate, or its path/game title was changed.");
                    Require(Find<Button>(window, "CopyStatusButton").Content?.ToString() == text.Translate("Copy"), "Copy caption did not retranslate.");
                    Find<Border>(window, "StatusBanner").IsVisible = false;

                    Click(Find<Button>(window, "LibraryNav"));
                    Require(Find<TextBlock>(window, "PageTitle").Text == expected.Games
                        && Find<TextBlock>(window, "GameCount").Text == text.Format("{0} of 3 games installed", 3), "Games page heading/count stayed stale.");
                    var cards = Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>().ToArray();
                    Require(cards.Select(card => card.Title).SequenceEqual(GameCatalog.All.Select(game => game.Title)), "Game titles were translated.");
                    Require(cards.All(card => card.PlayLabel == text.Translate("Play")), "Play buttons stayed in the previous language.");
                    foreach (var card in cards) card.Refresh(10000L * 3600 + 59 * 60, false);
                    window.Width = 880; window.Height = 600;
                    pump(TimeSpan.FromMilliseconds(100));
                    var locationLabel = Find<Button>(window, "LocationsNav").GetVisualDescendants().OfType<TextBlock>()
                        .Single(control => control.Text == text.Translate("Game locations"));
                    var wrapped = new TextBlock { Text = locationLabel.Text, FontFamily = locationLabel.FontFamily,
                        FontSize = locationLabel.FontSize, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                    wrapped.Measure(new Size(locationLabel.Bounds.Width, double.PositiveInfinity));
                    Require(wrapped.DesiredSize.Height <= locationLabel.Bounds.Height + 1,
                        "A translated navigation label was clipped instead of wrapping.");
                    foreach (var label in Find<ItemsControl>(window, "GameCards").GetVisualDescendants().OfType<TextBlock>()
                        .Where(control => cards.Any(card => control.Text == card.Playtime)))
                    {
                        var measured = new TextBlock { Text = label.Text, FontFamily = label.FontFamily,
                            FontSize = label.FontSize, FontWeight = label.FontWeight };
                        measured.Measure(Size.Infinity);
                        Require(measured.DesiredSize.Width <= label.Bounds.Width + 1,
                            $"A translated long playtime was clipped in {expected.Id}: {label.Text}; required {measured.DesiredSize.Width}, available {label.Bounds.Width}.");
                    }
                    capture(window, "language-" + expected.Id + "-games-minimum.png");
                    foreach (var card in cards) card.Refresh(0, false);

                    Click(Find<Button>(window, "ModsNav"));
                    Require(Find<TextBlock>(window, "PageTitle").Text == text.Translate("Mods")
                        && Find<TextBlock>(window, "SkipSplashScreensTitle").Text == text.Translate("Skip Splash Screens")
                        && Find<TextBlock>(window, "OriginalAspectRatioDescription").Text == text.Translate(
                            "Start windowed and keep the original 4:3 picture when resizing, maximizing or using fullscreen, with black bars as needed. Retains normal game speed. Applies on the next launch."),
                        "Mod labels or descriptions stayed stale.");
                    capture(window, "language-" + expected.Id + "-mods-minimum.png");

                    Click(Find<Button>(window, "CollectionNav"));
                    wait(() => Find<Button>(window, "RefreshCollectionButton").IsEnabled, "Collection refresh did not finish.");
                    Require(Find<TextBlock>(window, "PageTitle").Text == expected.Collection
                        && Find<Button>(window, "RefreshCollectionButton").Content?.ToString() == text.Translate("Refresh"),
                        "Collection page stayed in the previous language.");
                    var row = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().First();
                    row.IsExpanded = true;
                    wait(() => row.Cards.All(card => card.CardName is not null && card.HasArtwork), "Collection names/artwork did not load after language change.");
                    Require(row.OwnedHeading == text.Format("Owned cards ({0})", row.OwnedCards.Count)
                        && row.Title == "Yugi the Destiny", "Collection labels or the game title are incorrect.");
                    capture(window, "language-" + expected.Id + "-collection-minimum.png");
                    row.IsExpanded = false;

                    Require(menu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == text.Translate("Preferences")), "Settings menu did not retranslate.");
                    var preferencesItem = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == text.Translate("Preferences"));
                    preferencesItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    wait(() => window.OwnedWindows.OfType<PreferencesDialog>().Any(), "Localized preferences did not open.");
                    var dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
                    Require(dialog.Title == text.Translate("Launcher preferences")
                        && dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Content?.ToString() == text.Translate("Save preferences")),
                        "Preferences title/actions were not localized.");
                    capture(dialog, "language-" + expected.Id + "-preferences.png");
                    dialog.Close();
                    pump(TimeSpan.FromMilliseconds(50));

                    using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
                    foreach (var property in new[] { "Installations", "SearchRoots", "MinimizeOnLaunch", "WineExecutable", "CollectionSavePath",
                        "SkipSplashScreens", "SkipCharacterInteractions", "RestoreOriginalAspectRatio" })
                        Require(saved.RootElement.GetProperty(property).GetRawText() == preserved.GetProperty(property).GetRawText(),
                            "Changing interface language modified " + property + ".");
                });
            }
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            selector.SelectedItem = CardLanguages.Get("en");
            wait(() => selector.IsEnabled && text.Language == "en", "English was not restored after localization checks.");
            window.Width = 1100; window.Height = 720;
            Find<Border>(window, "StatusBanner").IsVisible = false;
            Click(Find<Button>(window, "CollectionNav"));
        }
    }

    private static T Find<T>(Control scope, string name) where T : Control => scope.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing localized control: " + name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
