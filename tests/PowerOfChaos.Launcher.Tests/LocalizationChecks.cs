using System.ComponentModel;
using System.Globalization;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Tests;

public static class LocalizationChecks
{
    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        var text = LauncherText.Current;
        try
        {
            await run("launcher text starts in English and covers all six catalogs", () =>
            {
                Require(text.Language == "en" && text.Translate("Games") == "Games", "English was not the default.");
                Require(text.Catalogs.Keys.ToHashSet().SetEquals(CardLanguages.All.Select(language => language.Id)), "A language catalog is missing.");
                Require(text.Catalogs["en"].Count >= 230, "The UI inventory is incomplete.");
                foreach (var catalog in text.Catalogs.Values)
                    Require(catalog.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(text.Catalogs["en"].Keys), "A catalog has missing keys.");
                return Task.CompletedTask;
            });
            await run("all launcher text keys resolve with matching placeholders in every language", () =>
            {
                foreach (var catalog in text.Catalogs)
                {
                    text.SetLanguage(catalog.Key);
                    foreach (var pair in text.Catalogs["en"])
                    {
                        Require(catalog.Value.TryGetValue(pair.Key, out var localized) && !string.IsNullOrWhiteSpace(localized), "A key fell back to English.");
                        Require(text.Translate(pair.Key) == localized, "Catalog lookup did not use the selected language.");
                        Require(LauncherText.Placeholders(pair.Key).SequenceEqual(LauncherText.Placeholders(localized!)), "A placeholder was lost or changed.");
                        var count = LauncherText.Placeholders(pair.Key).Count();
                        if (count > 0) text.Format(pair.Key, Enumerable.Repeat<object?>(1, count).ToArray());
                    }
                }
                return Task.CompletedTask;
            });
            await run("UI localization inventory includes controls, dialogs, accessibility and diagnostic states", () =>
            {
                string[] keys = ["Games", "Collection", "Mods", "Game locations", "About", "Settings", "Language",
                    "Language of the launcher and card names", "The language could not be saved. Your previous selection was kept.",
                    "Add Game path", "Copy launcher message", "Library empty state", "Unique cards across all games",
                    "Portrait of {0}", "Card collection for {0}", "Collection progress for {0}", "Play {0}",
                    "Choose a Power of Chaos game folder", "Paste a folder path to continue.", "Launcher preferences",
                    "Minimize the launcher when a game starts", "Wine runner", "Enter a Wine executable name or its full path.",
                    "Remove this game location?", "Remove location", "Starting…", "Running", "{0}h {1:00}m",
                    "Owned cards ({0})", "Missing cards ({0})", "Card ID {0}", "{0} copy owned", "{0} copies owned",
                    "{0} · {1}\nFirst available in {2}", "Collection unavailable", "Shared collection",
                    "Loading original card images…", "The private display cache failed verification. No game was started.",
                    "Could not start the game: {0}", "Saved launcher settings could not be read. Default settings were loaded."];
                foreach (var key in keys) Require(text.Catalogs["en"].ContainsKey(key), $"Missing UI text: {key}");
                return Task.CompletedTask;
            });
            await run("launcher language normalization matches the existing card preference", () =>
            {
                foreach (var language in CardLanguages.All)
                {
                    text.SetLanguage(" " + language.Id.ToUpperInvariant() + " ");
                    Require(text.Language == language.Id && text.Culture.TwoLetterISOLanguageName == language.Id, "Language normalization diverged.");
                }
                foreach (var invalid in new string?[] { null, "", "de-DE", "../../ja", "ja\nextra" })
                {
                    text.SetLanguage(invalid);
                    Require(text.Language == "en", "An unsupported preference did not fall back to English.");
                }
                return Task.CompletedTask;
            });
            await run("localized launcher entries notify once per effective language change", () =>
            {
                text.SetLanguage("en");
                var games = text.Entry("Games");
                Require(ReferenceEquals(games, text.Entry("Games")), "Authored entries were not reused.");
                var languageEvents = 0;
                var entryEvents = 0;
                EventHandler changed = (_, _) => languageEvents++;
                PropertyChangedEventHandler entryChanged = (_, e) => { if (e.PropertyName == "Value") entryEvents++; };
                text.LanguageChanged += changed;
                games.PropertyChanged += entryChanged;
                try
                {
                    text.SetLanguage("de");
                    Require(games.Value == "Spiele" && games.ToString() == "Spiele", "A live entry retained English.");
                    text.SetLanguage("DE");
                    Require(languageEvents == 1 && entryEvents == 1, "Idempotent language changes sent duplicate events.");
                    text.SetLanguage("ja");
                    Require(games.Value == "ゲーム" && languageEvents == 2 && entryEvents == 2, "Live switching failed.");
                }
                finally { text.LanguageChanged -= changed; games.PropertyChanged -= entryChanged; }
                return Task.CompletedTask;
            });
            await run("untrusted text cannot grow the global localization entry cache", () =>
            {
                var before = text.CachedEntryCount;
                for (var index = 0; index < 10000; index++)
                    Require(text.Entry("external diagnostic " + index).Value == "external diagnostic " + index, "External text was rewritten.");
                Require(text.CachedEntryCount == before && before <= text.Catalogs["en"].Count, "Unknown text grew the global entry cache.");
                return Task.CompletedTask;
            });
            await run("localized templates preserve game titles, Unicode names and raw paths", () =>
            {
                const string path = @"C:\Games\Missing · Games\日本語 {0}\system.dat";
                const string name = "Games Missing 日本語 {2}";
                foreach (var language in CardLanguages.All)
                {
                    text.SetLanguage(language.Id);
                    Require(text.Translate(path) == path && text.Translate(name) == name, "Raw data was translated.");
                    foreach (var game in GameCatalog.All)
                    {
                        Require(text.Translate(game.Title) == game.Title, "A game title was changed.");
                        Require(text.Format("Starting {0}…", game.Title).Contains(game.Title, StringComparison.Ordinal), "A formatted title changed.");
                        var diagnostic = text.Translate($"{game.Title}: the files in {path} are not a supported Power of Chaos installation.");
                        Require(diagnostic == text.Format("{0}: the files in {1} are not a supported Power of Chaos installation.", game.Title, path), "A known diagnostic template failed.");
                        Require(diagnostic.Contains(path, StringComparison.Ordinal) && diagnostic.Contains(game.Title, StringComparison.Ordinal), "A captured path or title changed.");
                    }
                    var tooltip = text.Format("{0} · {1}\nFirst available in {2}", name, new LocalizedTextArgument("Missing"), GameCatalog.All[0].Title);
                    Require(tooltip.Contains(name, StringComparison.Ordinal) && tooltip.Contains(GameCatalog.All[0].Title, StringComparison.Ordinal), "Tooltip data changed.");
                    Require(text.Translate("Power of Chaos") == "Power of Chaos" && text.Translate("Power of Chaos Launcher") == "Power of Chaos Launcher", "The launcher brand changed.");
                }
                return Task.CompletedTask;
            });
            await run("only complete known diagnostic templates translate and external details remain raw", () =>
            {
                text.SetLanguage("ja");
                const string detail = "OS detail: Missing Games C:\\Somewhere\\system.dat {0}";
                Require(text.Translate("Could not start the game: " + detail) == text.Format("Could not start the game: {0}", detail), "Unknown OS detail was changed.");
                const string authored = "The selected executable does not match this game.";
                Require(text.Translate("Could not start the game: " + authored)
                    == text.Format("Could not start the game: {0}", new LocalizedTextArgument(authored)), "Nested authored detail remained English.");
                const string message = "Could not completely inspect C:\\Games.";
                Require(text.Translate("prefix " + message) == "prefix " + message && text.Translate(message + " extra") == message + " extra", "A partial template matched.");
                var excessive = "Could not start the game: " + new string('x', 40000);
                Require(text.Translate(excessive) == excessive, "An oversized external diagnostic was scanned.");
                return Task.CompletedTask;
            });
            await run("formatting uses the selected culture and preserves units and Unicode", () =>
            {
                foreach (var language in CardLanguages.All)
                {
                    text.SetLanguage(language.Id);
                    Require(text.Format("{0}h {1:00}m", 27L, 5)
                        == string.Format(CultureInfo.GetCultureInfo(language.Id), text.Catalogs[language.Id]["{0}h {1:00}m"], 27L, 5), "Playtime culture or zero padding was lost.");
                    Require(text.Format("{0:N1}", 1234.5) == (1234.5).ToString("N1", text.Culture), "Selected culture was ignored.");
                }
                text.SetLanguage("fr");
                Require(text.Translate("Settings") == "Paramètres", "French Unicode was damaged.");
                text.SetLanguage("ja");
                Require(text.Translate("Language") == "言語", "Japanese Unicode was damaged.");
                return Task.CompletedTask;
            });
        }
        finally { text.SetLanguage("en"); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
