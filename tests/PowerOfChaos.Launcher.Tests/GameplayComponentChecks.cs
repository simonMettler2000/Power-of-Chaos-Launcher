using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

/// <summary>New card rewards launch preparation with authored inert fixtures. No game or component is started.</summary>
internal static class GameplayComponentChecks
{
    private const string Library = "msacm32.dll";
    private const string Settings = "pocgp.ini";

    internal static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        await run("gameplay component pin matches the canonical component manifest", () =>
        {
            using var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gameplay-component-pins.json")));
            Require(pins.RootElement.GetProperty("profile").GetString() == GameplayComponents.DirectoryName,
                "Canonical gameplay component profile does not match the runtime bundle directory.");
            var library = pins.RootElement.GetProperty("components").EnumerateArray().Single();
            Require(library.GetProperty("name").GetString() == GameplayComponents.Library.FileName
                && library.GetProperty("length").GetInt32() == GameplayComponents.Library.Length
                && library.GetProperty("sha256").GetString() == GameplayComponents.Library.Sha256 && GameplayComponents.Library.IsDll,
                "Runtime gameplay component pin differs from the canonical manifest.");
            Require(GameplayComponents.Library.FileName == Library && GameplayComponents.SettingsFileName == Settings
                && OriginalAspectRatioComponents.Required.All(component => !component.FileName.Equals(Library, StringComparison.OrdinalIgnoreCase)),
                "The gameplay component must not share a file name with the display bundles.");
            return Task.CompletedTask;
        });

        await run("New card rewards alone launches an unchanged private executable beside the component and its settings", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "gp"));
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions()) == fixture.Game.ExecutablePath,
                "Without mods the installation's own executable must be used.");
            var options = new GameModOptions(NewCardRewards: true);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var directory = Path.GetDirectoryName(path)!;
            Require(path != fixture.Game.ExecutablePath && directory.StartsWith(Path.Combine(fixture.Data, "mod-cache"), StringComparison.Ordinal),
                "The rewards launch does not use a private copy.");
            Require(Hash(await File.ReadAllBytesAsync(path)) == Hash(fixture.Original), "The private executable is not the verified original.");
            Require(Hash(await File.ReadAllBytesAsync(Path.Combine(directory, Library))) == fixture.Library.Sha256,
                "The component beside the executable is not the pinned one.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, Settings)) == "new_card_rewards=1\n", "Wrong component settings.");
            var installation = Path.GetDirectoryName(fixture.Game.ExecutablePath)!;
            Require(Hash(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)) == Hash(fixture.Original)
                && !File.Exists(Path.Combine(installation, Library)) && !File.Exists(Path.Combine(installation, Settings)),
                "The installation was changed.");

            // The component's own log is left alone; edited or damaged files are put right on the next launch.
            await File.WriteAllTextAsync(Path.Combine(directory, GameplayComponents.LogFileName), "component log");
            await File.WriteAllTextAsync(Path.Combine(directory, Settings), "new_card_rewards=0\nopponent_deck=elsewhere.ydc\n");
            var damaged = await File.ReadAllBytesAsync(Path.Combine(directory, Library)); damaged[^1] ^= 0x55;
            await File.WriteAllBytesAsync(Path.Combine(directory, Library), damaged);
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path, "The same selection changed its private directory.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, Settings)) == "new_card_rewards=1\n"
                && Hash(await File.ReadAllBytesAsync(Path.Combine(directory, Library))) == fixture.Library.Sha256
                && File.Exists(Path.Combine(directory, GameplayComponents.LogFileName)), "Edited component files were not replaced.");
            Require(!Directory.EnumerateFiles(directory).Any(file => Path.GetFileName(file).StartsWith(".preparing-", StringComparison.Ordinal)),
                "A temporary file was left in the private directory.");
        });

        await run("New card rewards combines with the executable mods and never shares a directory with a launch without it", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "gb"));
            var plain = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(SkipSplashScreens: true));
            var rewards = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(SkipSplashScreens: true, NewCardRewards: true));
            var alone = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(NewCardRewards: true));
            Require(new[] { plain, rewards, alone }.Select(Path.GetDirectoryName).Distinct().Count() == 3, "Different selections share a private directory.");
            Require(!File.Exists(Path.Combine(Path.GetDirectoryName(plain)!, Library)) && !File.Exists(Path.Combine(Path.GetDirectoryName(plain)!, Settings)),
                "A launch without the mod has the component beside it.");
            var splash = fixture.Profiles.Single(profile => profile.Options == new GameModOptions(SkipSplashScreens: true).BinaryOptions).ModifiedHash;
            Require(Hash(await File.ReadAllBytesAsync(rewards)) == splash && Hash(await File.ReadAllBytesAsync(plain)) == splash
                && File.Exists(Path.Combine(Path.GetDirectoryName(rewards)!, Library)), "The rewards launch did not carry the selected executable mod.");
            var all = new GameModOptions(true, true, NewCardRewards: true);
            Require(Hash(await File.ReadAllBytesAsync(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, all)))
                == fixture.Profiles.Single(profile => profile.Options == all.BinaryOptions).ModifiedHash, "Both executable mods were not applied with rewards.");
        });

        await run("New card rewards rides along in the display caches of Original aspect ratio and HD card artwork", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "gd"));
            var aspectOnly = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(RestoreOriginalAspectRatio: true)))!;
            var options = new GameModOptions(RestoreOriginalAspectRatio: true, NewCardRewards: true);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var directory = Path.GetDirectoryName(path)!;
            Require(directory != aspectOnly && !File.Exists(Path.Combine(aspectOnly, Library)) && !File.Exists(Path.Combine(aspectOnly, Settings)),
                "The aspect cache without the mod has the component, or both selections share a cache.");
            Require(Hash(await File.ReadAllBytesAsync(Path.Combine(directory, Library))) == fixture.Library.Sha256
                && await File.ReadAllTextAsync(Path.Combine(directory, Settings)) == "new_card_rewards=1\n"
                && Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "ddraw.dll"))) == fixture.Components.Single(c => c.FileName == "ddraw.dll").Sha256
                && File.Exists(Path.Combine(directory, "winmm.dll")) && File.Exists(Path.Combine(directory, "ddraw.ini")),
                "The display cache does not hold the display bundle together with the gameplay component.");
            await File.WriteAllTextAsync(Path.Combine(directory, GameplayComponents.LogFileName), "component log");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                && File.Exists(Path.Combine(directory, GameplayComponents.LogFileName)), "The component log invalidated a valid display cache.");
            await File.WriteAllTextAsync(Path.Combine(directory, Settings), "new_card_rewards=0\n");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                && await File.ReadAllTextAsync(Path.Combine(directory, Settings)) == "new_card_rewards=1\n", "Edited component settings were not replaced.");
            // A cache without the mod does not tolerate the component's log either: it is not part of that cache.
            await File.WriteAllTextAsync(Path.Combine(aspectOnly, GameplayComponents.LogFileName), "stray");
            await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(RestoreOriginalAspectRatio: true));
            Require(!File.Exists(Path.Combine(aspectOnly, GameplayComponents.LogFileName)), "A stray component log survived in a cache without the mod.");

            var hdOnly = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game,
                new GameModOptions(HdCardArtwork: true, HdCardImageDirectory: fixture.Pictures)))!;
            var hd = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game,
                new GameModOptions(HdCardArtwork: true, HdCardImageDirectory: fixture.Pictures, NewCardRewards: true)))!;
            Require(hd != hdOnly && hd != directory && !File.Exists(Path.Combine(hdOnly, Library))
                && Hash(await File.ReadAllBytesAsync(Path.Combine(hd, Library))) == fixture.Library.Sha256
                && File.Exists(Path.Combine(hd, HdCardArtworkComponents.SettingsFileName)) && File.Exists(Path.Combine(hd, Settings))
                && Hash(await File.ReadAllBytesAsync(Path.Combine(hd, "ddraw.dll"))) == fixture.HdComponents.Single(c => c.FileName == "ddraw.dll").Sha256,
                "The HD cache does not hold the HD provider together with the gameplay component.");
        });

        await run("New card rewards refuses a missing or changed component before any cache exists", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "gm"));
            var component = Path.Combine(fixture.GameplayDirectory, Library);
            var saved = await File.ReadAllBytesAsync(component);
            Require(GameplaySupport.ComponentPresent(fixture.GameplayDirectory) && !GameplaySupport.ComponentPresent(Path.Combine(fixture.Root, "nowhere")),
                "Component presence is reported wrongly.");
            foreach (var options in new[] { new GameModOptions(NewCardRewards: true), new GameModOptions(true, false, true, NewCardRewards: true),
                new GameModOptions(HdCardArtwork: true, HdCardImageDirectory: fixture.Pictures, NewCardRewards: true) })
            {
                File.Delete(component);
                await ExpectAsync<FileNotFoundException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                var damaged = saved.ToArray(); damaged[^1] ^= 0x55;
                await File.WriteAllBytesAsync(component, damaged);
                await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                await File.WriteAllBytesAsync(component, saved.Concat(new byte[] { 0 }).ToArray());
                await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                await File.WriteAllBytesAsync(component, saved);
            }
            Require(!Directory.Exists(fixture.Data), "A refused rewards launch published a cache.");
            // The same selections work once the component is whole again, and without the mod it is never read.
            File.Delete(component);
            await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(SkipSplashScreens: true));
            await File.WriteAllBytesAsync(component, saved);
            await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(NewCardRewards: true));
        });

        await run("New card rewards is saved, captured at Play and decides whether a launch directory may hold the component", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "gs"));
            var directory = Path.Combine(fixture.Root, "settings");
            var settings = await new SettingsStore(directory).LoadAsync();
            Require(!settings.NewCardRewards, "New card rewards must be off by default.");
            settings.NewCardRewards = true;
            await new SettingsStore(directory).SaveAsync(settings);
            Require((await new SettingsStore(directory).LoadAsync()).NewCardRewards, "The selection was not saved.");
            Require(new GameModOptions(NewCardRewards: true).UsesGameplayComponent && new GameModOptions(NewCardRewards: true).AnyEnabled
                && !new GameModOptions(NewCardRewards: true).BinaryModsEnabled && !new GameModOptions(NewCardRewards: true).UsesDisplayComponent
                && !new GameModOptions(true, true, true, true, "x").UsesGameplayComponent, "The selection is classified wrongly.");

            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(NewCardRewards: true));
            Expect<InvalidDataException>(() => LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), path, isWindows: true));
            var plan = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { NewCardRewards = true }, path, isWindows: true);
            Require(plan.FileName == path && plan.WorkingDirectory == Path.GetDirectoryName(fixture.Game.ExecutablePath),
                "The rewards launch does not run the private executable from the installation directory.");
            var wine = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { NewCardRewards = true }, path, isWindows: false,
                new Dictionary<string, string?> { ["WINEDLLOVERRIDES"] = "msacm32=b;d3d9,MSACM32.dll=n;mscoree=d" });
            Require(wine.Environment["WINEDLLOVERRIDES"] == "d3d9=n;mscoree=d;msacm32=n,b", "The Wine launch does not load the component and the built-in library.");
            var both = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { NewCardRewards = true, RestoreOriginalAspectRatio = true },
                await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(RestoreOriginalAspectRatio: true, NewCardRewards: true)),
                isWindows: false, new Dictionary<string, string?>());
            Require(both.Environment["WINEDLLOVERRIDES"] == "winmm,ddraw,*winmm,*ddraw=n;msacm32=n,b", "Display and gameplay overrides do not combine.");
            Require(LaunchPlan.WithGameplayOverride(null) == "msacm32=n,b" && LaunchPlan.WithGameplayOverride("") == "msacm32=n,b",
                "An empty override list is handled wrongly.");

            // A library of that name in the installation is the user's own: the unmodded launch from there is not refused.
            await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(fixture.Game.ExecutablePath)!, Library), [1, 2, 3]);
            var own = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), fixture.Game.ExecutablePath, isWindows: true);
            Require(own.FileName == fixture.Game.ExecutablePath, "A user's own library in the installation blocked the ordinary launch.");
        });
    }

    private sealed record Fixture(string Root, string Data, string GameplayDirectory, string Pictures, GameInstallation Game, byte[] Original,
        SplashProfile[] Profiles, DisplayComponent[] Components, DisplayComponent[] HdComponents, DisplayComponent Library, GameLaunchPreparer Preparer)
    {
        internal static async Task<Fixture> Create(string root, string id = "kaiba")
        {
            var image = SplashChecks.SyntheticImage(); image[0x230] = 7;
            var splash = image.ToArray(); Write32(splash, 0x210, 476); Write32(splash, 0x220, 1);
            var character = image.ToArray(); character[0x230] = 9;
            var both = splash.ToArray(); both[0x230] = 9;
            ScalarChange[] splashFields = [new(0x1010, 0x210, 4294967292, 476), new(0x1020, 0x220, 0, 1)];
            ScalarChange[] characterFields = [new(0x1030, 0x230, 7, 9, 1)];
            SplashProfile[] profiles =
            [
                new(id, image.Length, Hash(image), Hash(splash), splashFields),
                new(id, image.Length, Hash(image), Hash(character), characterFields) { Options = new(false, true) },
                new(id, image.Length, Hash(image), Hash(both), splashFields.Concat(characterFields).ToArray()) { Options = new(true, true) }
            ];
            var game = await SplashChecks.Installation(root, id, image);
            // Authored inert DLL-shaped headers only; no entry point, executable section or machine instructions.
            var ddraw = SplashChecks.SyntheticImage(); Write16(ddraw, 0x96, 0x2002);
            var winmm = ddraw.ToArray(); winmm[^1] = 1;
            var hdProvider = ddraw.ToArray(); hdProvider[^1] = 2;
            var gameplay = ddraw.ToArray(); gameplay[^1] = 3;
            var notices = OriginalAspectRatioComponents.Required.Where(component => !component.IsDll).ToDictionary(component => component.FileName,
                component => Encoding.UTF8.GetBytes("Authored synthetic notice fixture: " + component.FileName + "\n" + new string('L', 256)));
            var components = await WriteBundle(Path.Combine(root, "c"), ddraw, winmm, notices);
            var hdComponents = await WriteBundle(Path.Combine(root, "h"), hdProvider, winmm, notices);
            var gameplayDirectory = Path.Combine(root, "g"); Directory.CreateDirectory(gameplayDirectory);
            await File.WriteAllBytesAsync(Path.Combine(gameplayDirectory, GameplayComponentChecks.Library), gameplay);
            var library = new DisplayComponent(GameplayComponentChecks.Library, gameplay.Length, Hash(gameplay), true);
            var pictures = Path.Combine(root, "pack", "images"); Directory.CreateDirectory(pictures);
            await File.WriteAllBytesAsync(Path.Combine(pictures, "0000 - First Sample.png"), [1, 2, 3]);
            var data = Path.Combine(root, "d");
            return new(root, data, gameplayDirectory, pictures, game, image, profiles, components, hdComponents, library,
                new GameLaunchPreparer(data, profiles, Path.Combine(root, "c"), components, null, Path.Combine(root, "h"), hdComponents,
                    gameplayDirectory, library));
        }

        private static async Task<DisplayComponent[]> WriteBundle(string directory, byte[] ddraw, byte[] winmm, Dictionary<string, byte[]> notices)
        {
            Directory.CreateDirectory(directory);
            var files = new Dictionary<string, byte[]>(notices) { ["ddraw.dll"] = ddraw, ["winmm.dll"] = winmm };
            foreach (var file in files) await File.WriteAllBytesAsync(Path.Combine(directory, file.Key), file.Value);
            return files.Select(pair => new DisplayComponent(pair.Key, pair.Value.Length, Hash(pair.Value),
                pair.Key.EndsWith(".dll", StringComparison.Ordinal))).ToArray();
        }
    }

    private static void Write16(byte[] image, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), value);
    private static void Write32(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
