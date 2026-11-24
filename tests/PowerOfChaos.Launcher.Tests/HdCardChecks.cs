using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

/// <summary>HD card artwork launch preparation with authored inert fixtures. No game or provider is started.</summary>
internal static class HdCardChecks
{
    internal static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        await run("HD provider pin matches the canonical HD component manifest and reuses the aspect bootstrap and notices", () =>
        {
            using var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hd-component-pins.json")));
            Require(pins.RootElement.GetProperty("profile").GetString() == HdCardArtworkComponents.DirectoryName,
                "Canonical HD component profile does not match the runtime bundle directory.");
            var provider = pins.RootElement.GetProperty("components").EnumerateArray().Single();
            Require(provider.GetProperty("name").GetString() == HdCardArtworkComponents.Provider.FileName
                && provider.GetProperty("length").GetInt32() == HdCardArtworkComponents.Provider.Length
                && provider.GetProperty("sha256").GetString() == HdCardArtworkComponents.Provider.Sha256,
                "Runtime HD provider pin differs from the canonical manifest.");
            var shared = OriginalAspectRatioComponents.Required.Where(component => component.FileName != "ddraw.dll").ToArray();
            Require(HdCardArtworkComponents.Required.Count == shared.Length + 1
                && shared.All(component => HdCardArtworkComponents.Required.Contains(component))
                && HdCardArtworkComponents.Required.Count(component => component.IsDll) == 2,
                "The HD bundle must be the HD provider plus the unchanged bootstrap and all attribution notices.");
            Require(HdCardArtworkComponents.Provider != OriginalAspectRatioComponents.Required.Single(component => component.FileName == "ddraw.dll"),
                "The HD provider must be a different build than the aspect provider.");
            return Task.CompletedTask;
        });

        await run("HD card picture folders are found directly or through their images child and nothing else is accepted", () =>
        {
            var root = Path.Combine(fixtures, "hl");
            var pictures = Path.Combine(root, "pack", "images");
            Directory.CreateDirectory(pictures);
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            File.WriteAllBytes(Path.Combine(pictures, "0000 - First Sample.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(pictures, "0034 - Second Sample.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(pictures, "card-back-shared.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(pictures, "notes.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(pictures, "0001 - Third Sample.jpg"), [1, 2, 3]);
            Require(HdCardImages.Locate(pictures) == pictures && HdCardImages.Locate(Path.Combine(root, "pack")) == pictures,
                "The picture folder was not found directly or through its images child.");
            Require(HdCardImages.Locate(pictures + Path.DirectorySeparatorChar) == pictures, "A trailing separator changed the folder.");
            Require(HdCardImages.Count(pictures) == 3, "Only numbered card pictures and the shared back count.");
            Require(HdCardImages.Locate(Path.Combine(root, "empty")) is null && HdCardImages.Locate(Path.Combine(root, "missing")) is null
                && HdCardImages.Locate(null) is null && HdCardImages.Locate("") is null && HdCardImages.Locate("pack") is null
                && HdCardImages.Locate(pictures + "\n") is null && HdCardImages.Locate(Path.Combine(root, new string('x', 200))) is null,
                "An unusable folder was accepted.");
            Require(HdCardImages.IsPicture("0000 - First Sample.png") && HdCardImages.IsPicture("CARD-BACK-SHARED.PNG")
                && !HdCardImages.IsPicture("0000.png") && !HdCardImages.IsPicture("000a - x.png") && !HdCardImages.IsPicture("0000 - x.bmp"),
                "Picture name recognition changed.");
            Require(!HdCardArtworkSupport.ComponentPresent(Path.Combine(root, "empty")), "An empty folder passed as an installed HD component.");
            return Task.CompletedTask;
        });

        await run("HD card artwork prepares its own provider, recipe and picture folder setting beside a verified executable", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "hp"));
            var options = new GameModOptions(HdCardArtwork: true, HdCardImageDirectory: Path.Combine(fixture.Root, "pack"));
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var directory = Path.GetDirectoryName(path)!;
            Require(Hash(await File.ReadAllBytesAsync(path)) == Hash(fixture.Original), "The HD launch executable is not the verified original.");
            Require(Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "ddraw.dll"))) == fixture.HdComponents.Single(c => c.FileName == "ddraw.dll").Sha256
                && Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "winmm.dll"))) == fixture.HdComponents.Single(c => c.FileName == "winmm.dll").Sha256,
                "The HD cache does not hold the HD provider and bootstrap.");
            var ini = await File.ReadAllTextAsync(Path.Combine(directory, "ddraw.ini"));
            Require(ini == OriginalAspectRatioPreparer.CreateHdConfiguration(Path.Combine(directory, "screenshots"))
                && ini.Contains("\nrenderer=hd\n", StringComparison.Ordinal) && ini.Contains("\nfullscreen=true\n", StringComparison.Ordinal)
                && ini.Contains("\nmaintas=true\n", StringComparison.Ordinal) && ini.Contains("\nmaxgameticks=-1\n", StringComparison.Ordinal),
                "Wrong HD display recipe.");
            if (OperatingSystem.IsWindows())
                Require(await File.ReadAllTextAsync(Path.Combine(directory, HdCardArtworkComponents.SettingsFileName)) == "images=" + fixture.Pictures + "\n",
                    "The provider settings do not name the located picture folder.");
            foreach (var notice in fixture.HdComponents.Where(component => !component.IsDll))
                Require(Hash(await File.ReadAllBytesAsync(Path.Combine(directory, notice.FileName))) == notice.Sha256, "An attribution notice is missing: " + notice.FileName);
            Require(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath) is { } installed && Hash(installed) == Hash(fixture.Original)
                && !File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Game.ExecutablePath)!, "ddraw.dll")), "The installation was changed.");

            // The provider's own log does not invalidate the cache; anything else does and is rebuilt clean.
            await File.WriteAllTextAsync(Path.Combine(directory, HdCardArtworkComponents.LogFileName), "provider log");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                && File.Exists(Path.Combine(directory, HdCardArtworkComponents.LogFileName)), "The provider log invalidated a valid HD cache.");
            await File.WriteAllTextAsync(Path.Combine(directory, "stray.dll"), "not a component");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                && !File.Exists(Path.Combine(directory, "stray.dll")), "An unexpected file survived in the HD cache.");
            await File.WriteAllTextAsync(Path.Combine(directory, HdCardArtworkComponents.SettingsFileName), "images=C:\\elsewhere\n");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                && (!OperatingSystem.IsWindows() || await File.ReadAllTextAsync(Path.Combine(directory, HdCardArtworkComponents.SettingsFileName)) == "images=" + fixture.Pictures + "\n"),
                "Edited provider settings were not replaced.");

            // The aspect mod alone keeps its own provider and recipe; HD wins when both are selected.
            var aspect = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(RestoreOriginalAspectRatio: true));
            var aspectDirectory = Path.GetDirectoryName(aspect)!;
            Require(aspectDirectory != directory && !File.Exists(Path.Combine(aspectDirectory, HdCardArtworkComponents.SettingsFileName))
                && Hash(await File.ReadAllBytesAsync(Path.Combine(aspectDirectory, "ddraw.dll"))) == fixture.Components.Single(c => c.FileName == "ddraw.dll").Sha256
                && await File.ReadAllTextAsync(Path.Combine(aspectDirectory, "ddraw.ini")) == OriginalAspectRatioPreparer.CreateConfiguration(Path.Combine(aspectDirectory, "screenshots")),
                "The aspect mod no longer uses its own provider and recipe.");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { RestoreOriginalAspectRatio = true }) == path,
                "Selecting both display mods did not use the HD provider.");

            // Another picture folder is another cache identity.
            var second = Path.Combine(fixture.Root, "pack2"); Directory.CreateDirectory(second);
            await File.WriteAllBytesAsync(Path.Combine(second, "0001 - Third Sample.png"), [1]);
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { HdCardImageDirectory = second }) != path,
                "A changed picture folder reused the previous cache.");
        });

        await run("HD card artwork combines with the executable mods and refuses missing pictures or components before any cache exists", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "hm"));
            var missingPictures = new GameModOptions(HdCardArtwork: true, HdCardImageDirectory: Path.Combine(fixture.Root, "nowhere"));
            await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, missingPictures));
            await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(HdCardArtwork: true)));
            Require(!Directory.Exists(fixture.Data), "A refused HD launch published a cache.");
            var options = new GameModOptions(true, true, false, true, fixture.Pictures);
            var provider = Path.Combine(fixture.HdComponentDirectory, "ddraw.dll");
            var saved = await File.ReadAllBytesAsync(provider);
            File.Delete(provider);
            await ExpectAsync<FileNotFoundException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
            var damaged = saved.ToArray(); damaged[^1] ^= 0x55;
            await File.WriteAllBytesAsync(provider, damaged);
            await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
            Require(!Directory.Exists(fixture.Data), "A damaged HD component published a cache.");
            await File.WriteAllBytesAsync(provider, saved);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            Require(Hash(await File.ReadAllBytesAsync(path)) == fixture.Profiles.Single(profile => profile.Options == options.BinaryOptions).ModifiedHash,
                "HD card artwork did not carry the selected executable mods.");
        });

        await run("HD selection is saved, captured at Play and makes the launch plan accept the provider directory", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "hs"));
            var directory = Path.Combine(fixture.Root, "settings");
            var settings = await new SettingsStore(directory).LoadAsync();
            Require(!settings.HdCardArtwork && settings.HdCardImageDirectory is null, "HD card artwork must be off by default.");
            settings.HdCardArtwork = true;
            settings.HdCardImageDirectory = fixture.Pictures + Path.DirectorySeparatorChar;
            await new SettingsStore(directory).SaveAsync(settings);
            var loaded = await new SettingsStore(directory).LoadAsync();
            Require(loaded.HdCardArtwork && loaded.HdCardImageDirectory == fixture.Pictures, "The HD selection or picture folder was not saved.");
            loaded.HdCardImageDirectory = "relative";
            await new SettingsStore(directory).SaveAsync(loaded);
            Require((await new SettingsStore(directory).LoadAsync()).HdCardImageDirectory is null, "A relative picture folder was saved.");

            var options = new GameModOptions(HdCardArtwork: true, HdCardImageDirectory: fixture.Pictures);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            Expect<InvalidDataException>(() => LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), path, isWindows: true));
            var plan = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { HdCardArtwork = true }, path, isWindows: true);
            Require(plan.FileName == path && plan.WorkingDirectory == Path.GetDirectoryName(fixture.Game.ExecutablePath),
                "The HD launch does not run the private executable from the installation directory.");
            var wine = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { HdCardArtwork = true }, path, isWindows: false,
                new Dictionary<string, string?> { ["CNC_DDRAW_CONFIG_FILE"] = "elsewhere.ini" });
            Require(!wine.Environment.ContainsKey("CNC_DDRAW_CONFIG_FILE") && wine.Environment["WINEDLLOVERRIDES"] == "winmm,ddraw,*winmm,*ddraw=n",
                "The HD launch environment does not select the private provider.");
        });
        await run("Speedy mode passes one game argument for every title and display combination on Windows and Wine", async () =>
        {
            foreach (var title in GameCatalog.All)
            {
                var fixture = await Fixture.Create(Path.Combine(fixtures, "sp-" + title.Id), title.Id);
                foreach (var (hd, aspect) in new[] { (false, false), (false, true), (true, false), (true, true) })
                {
                    var options = new GameModOptions(RestoreOriginalAspectRatio: aspect, HdCardArtwork: hd,
                        HdCardImageDirectory: fixture.Pictures);
                    var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
                    foreach (var speedy in new[] { false, true })
                    {
                        var settings = new LauncherSettings
                        {
                            RestoreOriginalAspectRatio = aspect,
                            HdCardArtwork = hd,
                            SpeedyMode = speedy,
                            WineExecutable = "/test/runner with spaces/wine"
                        };
                        string[] gameArguments = speedy ? ["-speedy"] : [];
                        var windows = LaunchPlan.CreatePrepared(fixture.Game, settings, path, isWindows: true);
                        Require(windows.FileName == path && windows.ArgumentList.SequenceEqual(gameArguments),
                            $"Windows selected the wrong executable or arguments for {title.Id} (HD={hd}, aspect={aspect}, Speedy={speedy}).");
                        var wine = LaunchPlan.CreatePrepared(fixture.Game, settings, path, isWindows: false,
                            new Dictionary<string, string?> { ["WINEDLLOVERRIDES"] = "foo=b" });
                        string[] wineArguments = ["start", "/wait", "/unix", path, .. gameArguments];
                        Require(wine.FileName == settings.WineExecutable && wine.ArgumentList.SequenceEqual(wineArguments),
                            $"Wine selected the wrong runner or game arguments for {title.Id} (HD={hd}, aspect={aspect}, Speedy={speedy}).");
                        Require(wine.Environment["WINEDLLOVERRIDES"] == (hd || aspect ? "foo=b;winmm,ddraw,*winmm,*ddraw=n" : "foo=b"),
                            "An argument-only setting changed the display environment.");
                    }
                }
            }
        });
    }

    private sealed record Fixture(string Root, string Data, string ComponentDirectory, string HdComponentDirectory, string Pictures,
        GameInstallation Game, byte[] Original, SplashProfile[] Profiles, DisplayComponent[] Components, DisplayComponent[] HdComponents,
        GameLaunchPreparer Preparer)
    {
        internal static async Task<Fixture> Create(string root, string id = "joey")
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
            var notices = OriginalAspectRatioComponents.Required.Where(component => !component.IsDll).ToDictionary(component => component.FileName,
                component => Encoding.UTF8.GetBytes("Authored synthetic notice fixture: " + component.FileName + "\n" + new string('L', 256)));
            var componentDirectory = Path.Combine(root, "c");
            var hdComponentDirectory = Path.Combine(root, "h");
            var components = await WriteBundle(componentDirectory, ddraw, winmm, notices);
            var hdComponents = await WriteBundle(hdComponentDirectory, hdProvider, winmm, notices);
            var pictures = Path.Combine(root, "pack", "images"); Directory.CreateDirectory(pictures);
            await File.WriteAllBytesAsync(Path.Combine(pictures, "0000 - First Sample.png"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(pictures, "card-back-shared.png"), [1, 2, 3]);
            var data = Path.Combine(root, "d");
            return new(root, data, componentDirectory, hdComponentDirectory, pictures, game, image, profiles, components, hdComponents,
                new GameLaunchPreparer(data, profiles, componentDirectory, components, null, hdComponentDirectory, hdComponents));
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
