using System.Runtime.CompilerServices;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;
using PowerOfChaos.Launcher.ViewModels;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class LocalizationViewModelChecks
{
    private sealed record Expected(string Language, string Play, string Starting, string Running,
        string Playtime, string OwnedHeading, string MissingHeading, string FallbackName, string Missing);

    private static readonly Expected[] Languages =
    [
        new("en", "Play", "Starting…", "Running", "10000h 59m", "Owned cards (1)", "Missing cards (1)", "Card ID 7", "Missing"),
        new("de", "Spielen", "Startet…", "Läuft", "10000 Std. 59 Min.", "Vorhandene Karten (1)", "Fehlende Karten (1)", "Karten-ID 7", "Fehlend"),
        new("fr", "Jouer", "Démarrage…", "En cours", "10000 h 59 min", "Cartes possédées (1)", "Cartes manquantes (1)", "Identifiant de carte 7", "Manquante"),
        new("it", "Gioca", "Avvio…", "In esecuzione", "10000 h 59 min", "Carte possedute (1)", "Carte mancanti (1)", "ID carta 7", "Mancante"),
        new("es", "Jugar", "Iniciando…", "En ejecución", "10000 h 59 min", "Cartas obtenidas (1)", "Cartas que faltan (1)", "ID de carta 7", "Falta"),
        new("ja", "プレイ", "起動中…", "実行中", "10000時間 59分", "所持カード（1）", "未所持カード（1）", "カードID 7", "未所持")
    ];

    // Call after Avalonia SetupWithoutStarting. All paths, names and pixels are authored fixtures.
    public static void RunAll(Action<string, Action> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        try
        {
            LauncherText.Current.SetLanguage("en");
            check("game states, playtime and accessibility text change live in all six languages", GameLabels);
            check("collection translation preserves card names, metadata, lists and bitmaps", CollectionLabels);
            check("collection status getters retain English source through refresh and language round trips", StatusRoundTrips);
            check("disposed view models reject late mutations and stop language notifications", Disposal);
            check("disposed view models release their global language subscriptions", SubscriptionLifetime);
        }
        finally { LauncherText.Current.SetLanguage("en"); }
    }

    private static void GameLabels()
    {
        LauncherText.Current.SetLanguage("en");
        using var game = new GameCardViewModel(Installation());
        Require(game.Playtime == "0h 00m", "Initial English playtime changed.");
        game.Refresh(-100, false);
        Require(game.Playtime == "0h 00m", "Negative playtime did not clamp to zero.");
        game.Refresh(10000L * 3600 + 59 * 60, false);
        game.SetCollection(Progress());
        game.SetPortrait(new PortraitThumbnail(1, 1, [23, 17, 11, 255]));
        var portrait = game.Portrait;
        var folder = game.Folder;
        var changes = new List<string?>();
        game.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        foreach (var expected in ChangedLanguageOrder())
        {
            changes.Clear();
            LauncherText.Current.SetLanguage(expected.Language);
            Require(game.PlayLabel == expected.Play && game.Playtime == expected.Playtime,
                $"Game label or long playtime differs in {expected.Language}.");
            RequireNames(changes, nameof(game.Playtime), nameof(game.CollectionMessage), nameof(game.PlayLabel),
                nameof(game.PlayAutomationName), nameof(game.PortraitAutomationName), nameof(game.CollectionAutomationName));
            Require(game.Title == "Joey the Passion" && game.Installation == Installation() && game.Folder == folder
                && ReferenceEquals(portrait, game.Portrait) && game.CollectionCount == "1/771" && game.CanPlay,
                "Language change altered a title, path, portrait, count or launch state.");
            Require(game.PlayAutomationName.Contains(game.Title, StringComparison.Ordinal)
                && game.PortraitAutomationName.Contains(game.Title, StringComparison.Ordinal)
                && game.CollectionAutomationName.Contains(game.Title, StringComparison.Ordinal),
                "Accessibility text changed the game title.");
            if (expected.Language != "en")
                Require(game.PlayAutomationName != "Play Joey the Passion"
                    && game.PortraitAutomationName != "Portrait of Joey the Passion"
                    && game.CollectionAutomationName != "Card collection for Joey the Passion"
                    && game.CollectionMessage != "Shared collection", "A game label remained in English.");
            changes.Clear();
            game.SetLaunchPending(true);
            Require(game.PlayLabel == expected.Starting && !game.CanPlay
                && changes.Contains(nameof(game.PlayLabel)), "Pending launch did not update its localized state.");
            game.Refresh(10000L * 3600 + 59 * 60, true);
            Require(game.PlayLabel == expected.Running && !game.CanPlay, "Running state has the wrong localized label.");
            game.SetLaunchPending(false);
            Require(game.PlayLabel == expected.Running, "Clearing pending launch hid the running state.");
            game.Refresh(10000L * 3600 + 59 * 60, false, true);
            Require(game.PlayLabel == expected.Play && !game.CanPlay, "Another running game changed this game's label.");
            game.Refresh(10000L * 3600 + 59 * 60, false);
            Require(game.CanPlay, "Idle game did not become playable again.");
        }
        changes.Clear();
        game.Refresh(10000L * 3600 + 59 * 60 + 59, false);
        Require(changes.Count == 0, "Second-only playtime refresh unnecessarily notified bindings.");
        foreach (var definition in GameCatalog.All)
        {
            using var other = new GameCardViewModel(Installation() with { GameId = definition.Id });
            Require(other.CollectionCount == $"—/{CollectionSnapshot.TotalFor(definition.Id)}",
                "A collection denominator changed its identity display.");
        }
    }

    private static void CollectionLabels()
    {
        LauncherText.Current.SetLanguage("en");
        var progress = Progress();
        using var game = new CollectionGameViewModel(progress);
        var cards = game.Cards;
        var ownedCards = game.OwnedCards;
        var missingCards = game.MissingCards;
        var owned = ownedCards[0];
        var missing = missingCards[0];
        const string nativeName = "Play {0} · 試験札";
        owned.SetName(nativeName);
        game.SetArtwork(Image(owned.Slot));
        game.SetArtwork(Image(missing.Slot));
        var artwork = owned.Artwork;
        var missingArtwork = missing.Artwork;
        var version = game.ArtworkVersion;
        var percentage = game.Percentage;
        var changes = new List<string?>();
        var cardChanges = new List<string?>();
        game.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        owned.PropertyChanged += (_, change) => cardChanges.Add(change.PropertyName);
        using var single = new CollectionCardViewModel(new(8, 121, 1, "Missing"));
        single.SetName("Missing");
        foreach (var expected in ChangedLanguageOrder())
        {
            changes.Clear();
            cardChanges.Clear();
            LauncherText.Current.SetLanguage(expected.Language);
            Require(game.OwnedHeading == expected.OwnedHeading && game.MissingHeading == expected.MissingHeading
                && missing.DisplayName == expected.FallbackName, $"Collection labels differ in {expected.Language}.");
            RequireNames(changes, nameof(game.Message), nameof(game.OwnedHeading), nameof(game.MissingHeading),
                nameof(game.GalleryStatus), nameof(game.AutomationName));
            RequireNames(cardChanges, nameof(owned.DisplayName), nameof(owned.Tooltip));
            Require(owned.CardName == nativeName && owned.DisplayName == nativeName
                && owned.Tooltip.StartsWith(nativeName, StringComparison.Ordinal)
                && owned.Tooltip.Contains("Yugi the Destiny", StringComparison.Ordinal)
                && missing.Tooltip.StartsWith(expected.FallbackName, StringComparison.Ordinal)
                && missing.Tooltip.Contains(expected.Missing, StringComparison.Ordinal)
                && missing.Tooltip.Contains("Kaiba the Revenge", StringComparison.Ordinal),
                "Tooltip changed a native name or game title, or lost translated ownership.");
            Require(single.CardName == "Missing" && single.DisplayName == "Missing"
                && single.Tooltip.StartsWith("Missing", StringComparison.Ordinal)
                && single.Tooltip.Split('\n')[^1].Contains("Missing", StringComparison.Ordinal),
                "A raw name or unknown origin matching a UI key was translated.");
            if (expected.Language != "en")
                Require(!owned.Tooltip.Contains("2 copies owned", StringComparison.Ordinal)
                    && !single.Tooltip.Contains("1 copy owned", StringComparison.Ordinal)
                    && !owned.Tooltip.Contains("First available in", StringComparison.Ordinal)
                    && game.AutomationName != "Collection progress for Joey the Passion",
                    "Ownership, origin or collection accessibility text remained in English.");
            Require(ReferenceEquals(cards, game.Cards) && ReferenceEquals(ownedCards, game.OwnedCards)
                && ReferenceEquals(missingCards, game.MissingCards) && ReferenceEquals(artwork, owned.Artwork)
                && ReferenceEquals(missingArtwork, missing.Artwork) && game.ArtworkVersion == version
                && game.Percentage == percentage && game.Count == "1/771" && game.Title == "Joey the Passion"
                && ReferenceEquals(game.Progress, progress)
                && owned.Quantity == 2 && owned.Entry == Progress().Cards[0],
                "Language change mutated collection data or replaced native resources.");
        }
        Require(single.Tooltip.Contains("1 copy owned", StringComparison.Ordinal)
            && owned.Tooltip.Contains("2 copies owned", StringComparison.Ordinal),
            "Returning to English lost singular or plural ownership.");
    }

    private static void StatusRoundTrips()
    {
        LauncherText.Current.SetLanguage("en");
        using var game = new CollectionGameViewModel(Progress() with { OwnedCards = null, Message = "Game not added" });
        using var card = new GameCardViewModel(Installation());
        foreach (var expected in ChangedLanguageOrder())
        {
            LauncherText.Current.SetLanguage(expected.Language);
            var rawProgress = Progress() with { OwnedCards = null, Message = "Card data unavailable or unsupported" };
            Require(!game.UpdateProgress(rawProgress), "Unknown collection refresh invented card metadata.");
            card.SetCollection(rawProgress);
            Require(game.Progress.Message == "Card data unavailable or unsupported" && game.Count == "—/771"
                && game.Cards.Count == 0, "Status translation changed raw Core data or unknown ownership.");
            if (expected.Language == "en")
                Require(game.Message == rawProgress.Message && game.GalleryStatus == rawProgress.Message
                    && card.CollectionMessage == rawProgress.Message, "English status round trip lost original text.");
            else
                Require(game.Message != rawProgress.Message && game.GalleryStatus != rawProgress.Message
                    && card.CollectionMessage != rawProgress.Message, "A Core collection status remained in English.");
            game.SetGalleryStatus("Loading original card images…");
            LauncherText.Current.SetLanguage("en");
            Require(game.GalleryStatus == "Loading original card images…", "Gallery retained already translated text.");
            LauncherText.Current.SetLanguage(expected.Language);
            const string external = @"C:\Missing\Play {0}\試験 path";
            game.SetGalleryStatus(external);
            Require(game.GalleryStatus == external, "Unrecognized external status data was rewritten.");
        }
        LauncherText.Current.SetLanguage("en");
        Require(game.Message == "Card data unavailable or unsupported", "Language round trip lost a raw Core status.");
    }

    private static void Disposal()
    {
        LauncherText.Current.SetLanguage("en");
        using var game = new GameCardViewModel(Installation());
        using var collection = new CollectionGameViewModel(Progress());
        var card = collection.OwnedCards[0];
        card.SetName("Retained name");
        game.Refresh(3600, false);
        game.SetCollection(Progress());
        collection.SetGalleryStatus("Before disposal");
        game.Dispose();
        collection.Dispose();
        var changes = new List<string?>();
        game.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        collection.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        card.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        var version = collection.ArtworkVersion;
        LauncherText.Current.SetLanguage("de");
        LauncherText.Current.SetLanguage("en");
        game.Refresh(7200, true);
        game.SetLaunchPending(true);
        game.SetCollection(Progress() with { OwnedCards = 2, Message = "Late collection" });
        game.SetPortrait(new PortraitThumbnail(1, 1, [1, 2, 3, 255]));
        collection.SetGalleryStatus("Late status");
        collection.SetNames(new Dictionary<int, string> { [card.Slot] = "Late name" });
        card.SetName("Late name");
        game.Dispose();
        collection.Dispose();
        Require(changes.Count == 0 && !game.IsRunning && game.PlayLabel == "Play" && game.CanPlay
            && game.Playtime == "1h 00m" && game.CollectionCount == "1/771" && game.Portrait is null
            && collection.GalleryStatus == "Before disposal" && card.CardName == "Retained name"
            && collection.ArtworkVersion == version, "Disposed model accepted a late state or language update.");
    }

    private static void SubscriptionLifetime()
    {
        var models = DisposedReferences();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(models.All(reference => !reference.IsAlive), "Global language subscription retained a disposed view model.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] DisposedReferences()
    {
        var game = new GameCardViewModel(Installation());
        var collection = new CollectionGameViewModel(Progress());
        var card = new CollectionCardViewModel(new(8, 121, 1, "yugi"));
        WeakReference[] references = [new(game), new(collection), new(card), new(collection.Cards[0])];
        game.Dispose();
        collection.Dispose();
        card.Dispose();
        return references;
    }

    private static IEnumerable<Expected> ChangedLanguageOrder() => Languages.Skip(1).Append(Languages[0]);

    private static GameInstallation Installation() => new("joey", @"C:\Play\Missing\joey_pc.exe",
        @"C:\Play\Missing\data.dat", @"C:\Play\Missing\Voice.dat", @"C:\Play\Missing");

    private static GameCollectionProgress Progress() => new("joey", 771, 1, "Shared collection")
    {
        Cards = [new(4, 120, 2, "yugi"), new(2, 7, 0, "kaiba")]
    };

    private static NativeCardImage Image(int slot)
    {
        var pixels = new byte[49 * 72 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 23;
            pixels[index + 1] = 17;
            pixels[index + 2] = 11;
            pixels[index + 3] = 255;
        }
        return new(slot, 49, 72, pixels);
    }

    private static void RequireNames(List<string?> actual, params string[] expected) =>
        Require(actual.Count == expected.Length && actual.ToHashSet().SetEquals(expected), "Text binding notifications are incomplete or changed non-text state.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
