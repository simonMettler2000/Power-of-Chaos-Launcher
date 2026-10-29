using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.HdLauncher.Tests;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--validate-pack", var pack])
        {
            foreach (var game in GameCatalog.All)
            {
                var snapshot = await HdCardPack.ReadAsync(pack, game.Id, default);
                Console.WriteLine($"PASS {game.Id} validated {snapshot.Configuration.Length} configuration bytes, SHA256 {snapshot.Sha256}");
            }
            return 0;
        }
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply an absolute ignored fixture directory.");
        var root = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var passed = 0;
        async Task Check(string name, Func<Task> action)
        { await action(); Console.WriteLine("PASS " + name); passed++; }
        await Check("future settings survive HD preference saves", async () =>
        {
            var directory = Path.Combine(root, "settings"); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "launcher-settings.json"), "{\"SchemaVersion\":1,\"FutureCollection\":{\"mode\":\"shared\",\"cards\":[1,2]},\"FutureUi\":true}");
            var store = new SettingsStore(directory); var settings = await store.LoadAsync();
            settings.HdCardArtwork = true; settings.HdPackDirectory = root; settings.HdCardImageDirectory = Path.Combine(root, "images");
            await store.SaveAsync(settings);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "launcher-settings.json")));
            Require(saved.RootElement.GetProperty("FutureCollection").GetProperty("cards").GetArrayLength() == 2
                && saved.RootElement.GetProperty("FutureUi").GetBoolean(), "Unknown settings lost.");
            var loaded = await store.LoadAsync(); Require(loaded.HdCardArtwork && loaded.HdPackDirectory == root && loaded.HdCardImageDirectory == settings.HdCardImageDirectory, "HD settings lost.");
        });
        await Check("HD preferences preserve current collection, language and stable selections", async () =>
        {
            var data = Path.Combine(root, "current-preferences");
            var settings = new LauncherSettings { HdCardArtwork = true, HdPackDirectory = root,
                RestoreOriginalAspectRatio = true, SkipSplashScreens = true, SkipCharacterInteractions = true,
                CollectionSavePath = Path.Combine(root, "synthetic-system.dat"), CardLanguage = "de", MinimizeOnLaunch = false };
            var store = new SettingsStore(data); await store.SaveAsync(settings); var loaded = await store.LoadAsync();
            Require(loaded.HdCardArtwork && loaded.HdPackDirectory == root && loaded.RestoreOriginalAspectRatio
                && loaded.SkipSplashScreens && loaded.SkipCharacterInteractions && !loaded.MinimizeOnLaunch
                && loaded.CollectionSavePath == settings.CollectionSavePath && loaded.CardLanguage == "de", "Current settings were lost.");
        });
        await Check("invalid explicit folder remains unavailable after a settings round trip", async () =>
        {
            var store = new SettingsStore(Path.Combine(root, "invalid-explicit-preferences"));
            await store.SaveAsync(new LauncherSettings { HdCardArtwork = true, HdPackDirectory = "relative-invalid" });
            var loaded = await store.LoadAsync();
            Require(!string.IsNullOrWhiteSpace(loaded.HdPackDirectory), "Invalid explicit selection became automatic.");
            await Expect<InvalidDataException>(() => HdCardPackLocation.ResolveDirectoryAsync(loaded.HdPackDirectory));
        });
        await Check("automatic packs prefer app folder, fall back only for automatic detection, and preserve explicit selection", async () =>
        {
            var app = Path.Combine(root, "auto-app"); var data = Path.Combine(root, "auto-data");
            var appPack = Path.Combine(app, "HdCards"); var dataPack = Path.Combine(data, "HdCards");
            Directory.CreateDirectory(appPack); Directory.CreateDirectory(dataPack);
            foreach (var game in GameCatalog.All)
            {
                var fixture = await Fixture.Create(Path.Combine(root, "auto-inputs", game.Id), game.Id);
                File.Copy(Path.Combine(fixture.Pack, game.Id + "-565.tsv"), Path.Combine(appPack, game.Id + "-565.tsv"));
                File.Copy(Path.Combine(fixture.Pack, game.Id + "-565.tsv"), Path.Combine(dataPack, game.Id + "-565.tsv"));
            }
            Require(await HdCardPackLocation.ResolveDirectoryAsync("", app, data) == appPack, "App-local pack did not win precedence.");
            await File.WriteAllTextAsync(Path.Combine(appPack, "yugi-565.tsv"), "invalid");
            Require(await HdCardPackLocation.ResolveDirectoryAsync("", app, data) == dataPack, "Invalid automatic pack prevented local-data fallback.");
            Require(await HdCardPackLocation.ResolveDirectoryAsync(appPack, app, data) == appPack, "An explicit invalid pack silently changed selection.");
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(appPack, "yugi", default));
            var missing = Path.Combine(root, "explicit-missing");
            Require(await HdCardPackLocation.ResolveDirectoryAsync(missing, app, data) == missing, "An explicit missing pack was replaced.");
            await Expect<DirectoryNotFoundException>(() => HdCardPack.ReadAsync(missing, "yugi", default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Expect<OperationCanceledException>(() => HdCardPackLocation.ResolveDirectoryAsync("", app, data, cancelled.Token));
            var launch = await Fixture.Create(Path.Combine(root, "auto-launch"), "yugi");
            var local = Path.Combine(launch.Data, "HdCards"); Directory.CreateDirectory(local);
            foreach (var game in GameCatalog.All) File.Copy(Path.Combine(dataPack, game.Id + "-565.tsv"), Path.Combine(local, game.Id + "-565.tsv"));
            var output = await launch.Preparer.PrepareExecutableAsync(launch.Game, new GameModOptions(HdCardArtwork: true));
            Require(File.Exists(Path.Combine(Path.GetDirectoryName(output)!, HdCardPack.ConfigurationFileName)), "Blank-path immutable launch did not use data-local pack.");
        });
        foreach (var gameId in new[] { "yugi", "kaiba", "joey" })
        {
            await Check(gameId + " all HD combinations preserve executable identity, assets and child environment", async () =>
            {
                var fixture = await Fixture.Create(Path.Combine(root, gameId), gameId);
                var originalHash = Hash(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath));
                var cachePaths = new HashSet<string>();
                var parentEnvironment = Environment.GetEnvironmentVariable("POC_HD_CONFIG");
                for (var mask = 0; mask < 8; mask++)
                {
                    var options = new GameModOptions((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, true, fixture.Pack);
                    Require(!options.BinaryOptions.HdCardArtwork && options.BinaryOptions.HdPackDirectory.Length == 0
                        && !options.BinaryOptions.RestoreOriginalAspectRatio, "Binary profile includes display/pack fields.");
                    var output = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
                    Require(cachePaths.Add(output), "Combination reused a different options identity.");
                    var directory = Path.GetDirectoryName(output)!;
                    var ini = await File.ReadAllTextAsync(Path.Combine(directory, "ddraw.ini"));
                    Require(ini.Contains("width=1280\nheight=960", StringComparison.Ordinal) && ini.Contains("renderer=gdi\n", StringComparison.Ordinal)
                        && ini.Contains("rgb555=false\n", StringComparison.Ordinal) && ini.Contains("maxgameticks=-1\n", StringComparison.Ordinal), "Wrong HD rendering recipe.");
                    var settings = Settings(options);
                    var inherited = new Dictionary<string, string?> { ["POC_HD_CONFIG"] = "untrusted", ["CNC_DDRAW_CONFIG_FILE"] = "untrusted", ["KEEP_ME"] = "yes" };
                    var plan = LaunchPlan.CreatePrepared(fixture.Game, settings, output, true, inherited);
                    Require(plan.Environment["POC_HD_CONFIG"] == Path.Combine(directory, HdCardPack.ConfigurationFileName)
                        && !plan.Environment.ContainsKey("CNC_DDRAW_CONFIG_FILE") && plan.Environment["KEEP_ME"] == "yes", "Child environment escaped isolation.");
                    Require(plan.WorkingDirectory == Path.GetDirectoryName(fixture.Game.ExecutablePath)
                        && !File.Exists(Path.Combine(directory, "data.dat")), "Asset working directory changed.");
                    Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == output, "Stable input changed cache.");
                    Require(File.ReadAllBytes(Path.Combine(directory, HdCardPack.ConfigurationFileName)).SequenceEqual(
                        File.ReadAllBytes(Path.Combine(fixture.Pack, gameId + "-565.tsv"))), "Snapshot differs from validated config.");
                }
                Require(Hash(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)) == originalHash, "Installed executable changed.");
                Require(Environment.GetEnvironmentVariable("POC_HD_CONFIG") == parentEnvironment, "Launcher global environment changed.");
                var off = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), fixture.Game.ExecutablePath, true,
                    new Dictionary<string, string?> { ["poc_hd_config"] = "untrusted" });
                Require(!off.Environment.Keys.Any(key => key.Equals("POC_HD_CONFIG", StringComparison.OrdinalIgnoreCase)), "HD-off inherited configuration.");
            });
        }
        await Check("optional cursor bank hashes and configuration identity are strict", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "cursor-bank"), "joey");
            var config = Path.Combine(fixture.Pack, "joey-565.tsv");
            var old = await File.ReadAllTextAsync(config);
            var first = Path.Combine(fixture.Pack, "normal0.bgra"); var second = Path.Combine(fixture.Pack, "normal1.bgra");
            var bank = new byte[16384]; bank[3] = 255;
            await File.WriteAllBytesAsync(first, bank); bank[4] = 21; await File.WriteAllBytesAsync(second, bank);
            var line = "CURSORS_BGRA64_V1\t" + first + "\t" + Hash(await File.ReadAllBytesAsync(first)) + "\t" + second + "\t" + Hash(bank);
            var v2 = old.Replace("POC_HD_VISIBLE_V1\n3\t1\n", "POC_HD_VISIBLE_V2\n3\t1\n" + line + "\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(config, v2);
            var snapshot = await HdCardPack.ReadAsync(fixture.Pack, "joey", default);
            Require(snapshot.InputPaths.Contains(first) && snapshot.InputPaths.Contains(second), "Cursor bank missing from input ownership.");
            Require(snapshot.Sha256 != Hash(System.Text.Encoding.UTF8.GetBytes(old)), "V2 cursor identity reused old config.");
            bank[15] ^= 1; await File.WriteAllBytesAsync(second, bank);
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "joey", default));
            await File.WriteAllBytesAsync(second, new byte[16383]);
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "joey", default));
            await File.WriteAllTextAsync(config, v2.Replace("CURSORS_BGRA64_V1", "CURSORS_BGRA64_V9", StringComparison.Ordinal));
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "joey", default));
        });
        await Check("V3 background pins and finite pose metadata bind immutable launch identity", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "flip-profile"), "yugi");
            var config = Path.Combine(fixture.Pack, "yugi-565.tsv"); var v1 = await File.ReadAllTextAsync(config);
            var cursor = Path.Combine(fixture.Pack, "normal.bgra"); var bank = Path.Combine(fixture.Pack, "background.rgb565");
            var cursorBytes = new byte[16384]; var backgroundBytes = new byte[116000];
            await File.WriteAllBytesAsync(cursor, cursorBytes); await File.WriteAllBytesAsync(bank, backgroundBytes);
            var cursorLine = $"CURSORS_BGRA64_V1\t{cursor}\t{Hash(cursorBytes)}\t{cursor}\t{Hash(cursorBytes)}";
            var backgroundLine = $"FLIP_BG565_V1\t{bank}\t{Hash(backgroundBytes)}\t{bank}\t{Hash(backgroundBytes)}";
            const string poses = "FLIP_POSES_V1\t196,9,15\t184,15,14\t166,24,13\t141,37,12\t111,52,10\t76,69,7\t39,88,5";
            var v3 = v1.Replace("POC_HD_VISIBLE_V1\n1\t1\n", $"POC_HD_VISIBLE_V3\n1\t1\n{cursorLine}\n{backgroundLine}\n{poses}\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(config, v3);
            var snapshot = await HdCardPack.ReadAsync(fixture.Pack, "yugi", default);
            Require(snapshot.InputPaths.Contains(bank) && snapshot.InputPaths.Contains(cursor) && snapshot.Sha256 != Hash(Encoding.UTF8.GetBytes(v1)), "V3 omitted owned inputs/identity.");
            await File.WriteAllTextAsync(config, v3.Replace("184,15,14", "183,15,14", StringComparison.Ordinal));
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default));
            await File.WriteAllTextAsync(config, v3); backgroundBytes[114] = 1; await File.WriteAllBytesAsync(bank, backgroundBytes);
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default));
            await File.WriteAllBytesAsync(bank, new byte[115999]);
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default));
        });
        await Check("pack validation rejects malformed, wrong-game, oversized and unsafe local paths before cache writes", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "invalid"), "yugi");
            var config = Path.Combine(fixture.Pack, "yugi-565.tsv"); var valid = await File.ReadAllTextAsync(config);
            foreach (var invalid in new[] { valid.Replace("1\t1\n", "2\t1\n", StringComparison.Ordinal), valid + valid.Split('\n')[2] + "\n",
                "POC_HD_VISIBLE_V1\n1\t1\n", valid.Replace("1\t1\n", "1\t2\n", StringComparison.Ordinal), new string('a', HdCardPack.MaximumConfigBytes + 1) })
            {
                await File.WriteAllTextAsync(config, invalid);
                await Expect<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(HdCardArtwork: true, HdPackDirectory: fixture.Pack)));
                Require(!Directory.Exists(fixture.Data), "Malformed pack created a cache.");
            }
            await File.WriteAllTextAsync(config, valid);
            foreach (var path in new[] { "relative", "\\\\server\\share", "\\\\?\\C:\\x", Path.Combine(root, "..", "x"), Path.Combine(root, "NUL.png"), root + "\tbad" })
                await Expect<InvalidDataException>(() => Task.FromResult(HdCardPack.ValidatePath(path)));
            await File.WriteAllBytesAsync(fixture.Reference, new byte[115999]);
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default));
            await Expect<OperationCanceledException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", new CancellationToken(true)));
        });
        await Check("Windows device names including superscript suffixes are rejected lexically", async () =>
        {
            foreach (var device in new[] { "COM1", "LPT9", "COM\u00b9", "LPT\u00b2", "COM\u00b3", "CONIN$", "CONOUT$" })
                await Expect<InvalidDataException>(() => Task.FromResult(HdCardPack.ValidatePath(Path.Combine(root, device + ".png"))));
        });
        await Check("HD output cannot overlap the selected pack inputs", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "overlap"), "yugi");
            Directory.CreateDirectory(fixture.Data);
            File.Copy(Path.Combine(fixture.Pack, "yugi-565.tsv"), Path.Combine(fixture.Data, "yugi-565.tsv"));
            await Expect<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game,
                new GameModOptions(HdCardArtwork: true, HdPackDirectory: fixture.Data)));
            Require(!Directory.Exists(Path.Combine(fixture.Data, "display")), "Overlapping pack published display output.");
        });
        await Check("missing components fail closed and cached config tampering is repaired", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "cache"), "yugi");
            var options = new GameModOptions(HdCardArtwork: true, HdPackDirectory: fixture.Pack);
            var output = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var alternatePack = Path.Combine(fixture.Pack, "alternate"); Directory.CreateDirectory(alternatePack);
            File.Copy(Path.Combine(fixture.Pack, "yugi-565.tsv"), Path.Combine(alternatePack, "yugi-565.tsv"));
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { HdPackDirectory = alternatePack }) != output,
                "Changed pack selection reused the same launch identity.");
            var config = Path.Combine(Path.GetDirectoryName(output)!, HdCardPack.ConfigurationFileName);
            await File.WriteAllTextAsync(config, "tampered");
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == output
                && File.ReadAllText(config).StartsWith("POC_HD_VISIBLE_V1", StringComparison.Ordinal), "Tampered snapshot survived.");
            File.Delete(Path.Combine(fixture.Components, "ddraw.dll"));
            await Expect<FileNotFoundException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
        });
        await Check("a slow older HD inspection cannot replace a newer folder or its availability", async () =>
        {
            var first = new TaskCompletionSource<HdCardArtworkStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<HdCardArtworkStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var tracker = new HdCardArtworkAvailabilityTracker((folder, _) => folder == "first" ? first.Task : second.Task);
            var older = tracker.RefreshAsync("first");
            var newer = tracker.RefreshAsync("second");
            Require(tracker.Status.PackDirectory == "second" && tracker.Status.UnavailableReason is not null,
                "Pending newer selection retained older availability.");
            var selected = new HdCardArtworkStatus("second", "second-images", null);
            second.SetResult(selected); await newer;
            first.SetResult(new("first", "first-images", "Older pack is unavailable")); await older;
            Require(tracker.Status == selected, "Late older I/O overwrote the newer folder, image path or enabled state.");
        });
        await Check("closing the HD availability owner rejects pending completion and future inspection", async () =>
        {
            var pending = new TaskCompletionSource<HdCardArtworkStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var tracker = new HdCardArtworkAvailabilityTracker((_, _) => { calls++; return pending.Task; });
            var refresh = tracker.RefreshAsync("pending"); var closedState = tracker.Status;
            tracker.Dispose(); pending.SetResult(new("late", "late-images", null)); await refresh;
            await tracker.RefreshAsync("after-close");
            Require(tracker.Status == closedState && calls == 1, "Closed owner published late status or performed new I/O.");
        });
        await Check("pending launch captures immutable HD path and disposal cancels without starting a process", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "session"), "yugi");
            var output = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(HdCardArtwork: true, HdPackDirectory: fixture.Pack));
            var paused = new PausingPreparer(output); var runner = new FakeRunner();
            await using (var service = new GameSessionService(Path.Combine(root, "time"), runner, preparer: paused,
                temporaryCollections: new TemporaryCollectionService(Path.Combine(root, "session-vault"),
                    reader: new CardCollectionReader(_ => []), hasRunningGames: () => false)))
            {
                var settings = new LauncherSettings { HdCardArtwork = true, HdPackDirectory = fixture.Pack, HdCardImageDirectory = "captured-images", SkipSplashScreens = true, RestoreOriginalAspectRatio = true };
                var pending = service.LaunchAsync(fixture.Game, settings);
                await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                settings.HdCardArtwork = false; settings.HdPackDirectory = "changed"; settings.HdCardImageDirectory = "changed-images"; settings.SkipSplashScreens = false; settings.RestoreOriginalAspectRatio = false;
                paused.Release.TrySetResult();
                Require((await pending).Success && paused.Captured is { HdCardArtwork: true } && paused.Captured.HdPackDirectory == fixture.Pack
                    && paused.Captured.HdCardImageDirectory == "captured-images" && paused.Captured.SkipSplashScreens && paused.Captured.RestoreOriginalAspectRatio
                    && runner.Starts == 1 && runner.Plan!.Environment.ContainsKey("POC_HD_CONFIG"), "Mutable settings reached pending launch.");
            }
            var cancelled = new PausingPreparer(output); var blockedRunner = new FakeRunner();
            var closing = new GameSessionService(Path.Combine(root, "cancel"), blockedRunner, preparer: cancelled,
                temporaryCollections: new TemporaryCollectionService(Path.Combine(root, "cancel-vault"),
                    reader: new CardCollectionReader(_ => []), hasRunningGames: () => false));
            var launch = closing.LaunchAsync(fixture.Game, new LauncherSettings { HdCardArtwork = true, HdPackDirectory = fixture.Pack });
            await cancelled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(closing.DisposeAsync().AsTask(), closing.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
            Require(!(await launch).Success && blockedRunner.Starts == 0, "Disposal started a process.");
        });
        await RunImagePathChecks(root, Check);
        Console.WriteLine($"{passed} checks passed; synthetic data only, no native DLL or game executed."); return 0;
    }

    private static LauncherSettings Settings(GameModOptions options) => new() { SkipSplashScreens = options.SkipSplashScreens,
        SkipCharacterInteractions = options.SkipCharacterInteractions, RestoreOriginalAspectRatio = options.RestoreOriginalAspectRatio,
        HdCardArtwork = options.HdCardArtwork, HdPackDirectory = options.HdPackDirectory, HdCardImageDirectory = options.HdCardImageDirectory };

    private sealed record Fixture(GameInstallation Game, string Data, string Components, string Pack, string Reference, GameLaunchPreparer Preparer)
    {
        internal static async Task<Fixture> Create(string root, string gameId)
        {
            var game = Path.Combine(root, "g"); Directory.CreateDirectory(game);
            var image = Image(false); var executable = Path.Combine(game, GameCatalog.Get(gameId).ExecutableName);
            await File.WriteAllBytesAsync(executable, image);
            foreach (var name in new[] { "data.dat", "voice.dat" }) await File.WriteAllTextAsync(Path.Combine(game, name), "Synthetic archive header only.");
            var installation = new GameInstallation(gameId, executable, Path.Combine(game, "data.dat"), Path.Combine(game, "voice.dat"), game);
            var components = Path.Combine(root, "c"); Directory.CreateDirectory(components);
            var pins = new List<DisplayComponent>();
            foreach (var name in new[] { "ddraw.dll", "winmm.dll", "authored-notice.txt" })
            {
                var bytes = name.EndsWith(".dll", StringComparison.Ordinal) ? Image(true) : Encoding.UTF8.GetBytes(new string('n', 256));
                await File.WriteAllBytesAsync(Path.Combine(components, name), bytes); pins.Add(new(name, bytes.Length, Hash(bytes), name.EndsWith(".dll", StringComparison.Ordinal)));
            }
            var profiles = new List<SplashProfile>();
            for (var mask = 1; mask <= 3; mask++)
            {
                var changed = image.ToArray(); var changes = new List<ScalarChange>();
                if ((mask & 1) != 0) { changed[0x210] = 1; changes.Add(new(0x1010, 0x210, 0, 1, 1)); }
                if ((mask & 2) != 0) { changed[0x220] = 1; changes.Add(new(0x1020, 0x220, 0, 1, 1)); }
                profiles.Add(new(gameId, image.Length, Hash(image), Hash(changed), changes) { Options = new((mask & 1) != 0, (mask & 2) != 0) });
            }
            var pack = Path.Combine(root, "p"); Directory.CreateDirectory(pack);
            var reference = Path.Combine(pack, "reference.bin"); await File.WriteAllBytesAsync(reference, new byte[116000]);
            var master = Path.Combine(pack, "master.png"); var header = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 13); "IHDR"u8.CopyTo(header.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 800); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 1160); header[24] = 8; header[25] = 2;
            await File.WriteAllBytesAsync(master, header);
            var numeric = gameId == "yugi" ? 1 : gameId == "kaiba" ? 2 : 3;
            await File.WriteAllTextAsync(Path.Combine(pack, gameId + "-565.tsv"), $"POC_HD_VISIBLE_V1\n{numeric}\t1\n1\t{reference}\t{master}\n", new UTF8Encoding(false));
            var data = Path.Combine(root, "d");
            return new(installation, data, components, pack, reference, new GameLaunchPreparer(data, profiles, components, pins, hdComponentDirectory: components, hdComponents: pins));
        }
    }
    private static byte[] Image(bool dll)
    {
        // Inert authored PE metadata: no entry point, code, original bytes or native execution.
        var bytes = new byte[1024]; W16(bytes, 0, 0x5a4d); W32(bytes, 0x3c, 0x80); W32(bytes, 0x80, 0x4550);
        W16(bytes, 0x84, 0x14c); W16(bytes, 0x86, 1); W16(bytes, 0x94, 224); W16(bytes, 0x96, dll ? (ushort)0x2002 : (ushort)2);
        W16(bytes, 0x98, 0x10b); W32(bytes, 0x98 + 92, 16); W32(bytes, 0x178 + 12, 0x1000); W32(bytes, 0x178 + 16, 512); W32(bytes, 0x178 + 20, 512); return bytes;
    }
    private sealed class PausingPreparer(string path) : IGameLaunchPreparer
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GameModOptions? Captured { get; private set; }
        public async Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options, CancellationToken cancellationToken = default)
        { Captured = options; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return path; }
    }
    private sealed class FakeRunner : IGameProcessRunner
    {
        internal int Starts { get; private set; } internal ProcessStartInfo? Plan { get; private set; }
        public IGameProcess Start(ProcessStartInfo startInfo) { Starts++; Plan = startInfo; return new FakeProcess(); }
    }
    private sealed class FakeProcess : IGameProcess
    { public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken); public void Dispose() { } }
    private static void W16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
