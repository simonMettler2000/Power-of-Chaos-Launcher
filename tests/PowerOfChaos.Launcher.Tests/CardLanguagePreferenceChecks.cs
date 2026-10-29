using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

internal static class CardLanguagePreferenceChecks
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> run)
    {
        var directory = Path.Combine(root, "card-language-preferences");
        var store = new SettingsStore(directory);
        await run("legacy launcher preferences default card names to English", async () =>
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "launcher-settings.json"),
                "{\"SchemaVersion\":1,\"MinimizeOnLaunch\":false,\"SkipSplashScreens\":true,\"WineExecutable\":\"/synthetic runner/wine\"}");
            var settings = await store.LoadAsync();
            Require(settings.CardLanguage == "en" && !settings.MinimizeOnLaunch && settings.SkipSplashScreens
                && settings.WineExecutable == "/synthetic runner/wine", "Legacy preferences were not preserved.");
        });
        await run("all six card languages persist without changing game and mod preferences", async () =>
        {
            var save = Path.Combine(directory, "system.dat");
            foreach (var language in new[] { "en", "de", "fr", "it", "es", "ja" })
            {
                var settings = new LauncherSettings
                {
                    CardLanguage = language, CollectionSavePath = save, MinimizeOnLaunch = false,
                    SkipSplashScreens = true, SkipCharacterInteractions = true, RestoreOriginalAspectRatio = true,
                    WineExecutable = "/synthetic runner/wine", SearchRoots = [directory]
                };
                await store.SaveAsync(settings);
                var actual = await new SettingsStore(directory).LoadAsync();
                Require(actual.CardLanguage == language && actual.CollectionSavePath == save
                    && !actual.MinimizeOnLaunch && actual.SkipSplashScreens && actual.SkipCharacterInteractions
                    && actual.RestoreOriginalAspectRatio && actual.WineExecutable == settings.WineExecutable
                    && actual.SearchRoots.SequenceEqual(settings.SearchRoots), "Language save changed another preference.");
            }
        });
        await run("unsupported and null card languages normalize safely when loading or saving", async () =>
        {
            foreach (var language in new string?[] { null, "", "unknown", "../../ger", "ja\nextra" })
            {
                await store.SaveAsync(new LauncherSettings { CardLanguage = language! });
                Require((await store.LoadAsync()).CardLanguage == "en", "An invalid language became an archive resource key.");
            }
            await store.SaveAsync(new LauncherSettings { CardLanguage = " DE " });
            Require((await store.LoadAsync()).CardLanguage == "de", "A recognized language was not normalized.");
            await File.WriteAllTextAsync(Path.Combine(directory, "launcher-settings.json"), "{\"CardLanguage\":null}");
            Require((await store.LoadAsync()).CardLanguage == "en", "A null JSON language bypassed validation.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
