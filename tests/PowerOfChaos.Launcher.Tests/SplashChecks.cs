using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Discovery;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

internal static class SplashChecks
{
    internal static async Task VerifyInstalledProfiles(Func<string, Func<Task>, Task> run)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "KONAMI");
        var games = (await new GameDiscovery().ScanAsync(root)).Games;
        foreach (var profile in SkipSplashPatch.Profiles)
        {
            await run($"read-only installed {profile.GameId} splash profile verifies entirely in memory", async () =>
            {
                var game = games.Single(installation => installation.GameId == profile.GameId);
                var original = await File.ReadAllBytesAsync(game.ExecutablePath);
                var modified = SkipSplashPatch.Apply(original, profile);
                var changed = Enumerable.Range(0, original.Length).Where(index => original[index] != modified[index]).ToArray();
                Require(changed.Length == 5 && changed.All(index => profile.Changes.Any(change => index >= change.FileOffset && index < change.FileOffset + 4)),
                    "The production candidate changed unexpected bytes.");
                Require(Hash(original) == profile.OriginalHash && Hash(modified) == profile.ModifiedHash,
                    "Production input or result hash differs.");
                Require(Hash(await File.ReadAllBytesAsync(game.ExecutablePath)) == profile.OriginalHash,
                    "Read-only verification changed the original file.");
            });
        }
        Console.WriteLine("Installed mod verification used memory only: no game/cache executable was written or run.");
    }

    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        await run("global splash preference defaults off and survives reload in both directions", async () =>
        {
            var store = new SettingsStore(Path.Combine(fixtures, "mod-settings"));
            Require(!(await store.LoadAsync()).SkipSplashScreens, "A fresh install enabled the mod.");
            await store.SaveAsync(new LauncherSettings { SkipSplashScreens = true });
            Require((await store.LoadAsync()).SkipSplashScreens, "Enabling the global mod was lost.");
            await store.SaveAsync(new LauncherSettings { SkipSplashScreens = false });
            Require(!(await store.LoadAsync()).SkipSplashScreens, "Disabling the global mod was lost.");
        });

        await run("splash patch changes only declared scalar spans in a synthetic PE", () =>
        {
            var original = SyntheticImage();
            var profile = Profile(original);
            var modified = SkipSplashPatch.Apply(original, profile);
            Require(Read(original, 0x210) == 4294967292 && Read(original, 0x220) == 0, "Input buffer was mutated.");
            Require(Read(modified, 0x210) == 476 && Read(modified, 0x220) == 1, "Startup settings differ.");
            for (var i = 0; i < original.Length; i++)
                if (!(i >= 0x210 && i < 0x214) && !(i >= 0x220 && i < 0x224))
                    Require(original[i] == modified[i], "A byte outside the numeric operands changed.");
            return Task.CompletedTask;
        });

        await run("splash patch refuses unsupported input and incorrect expected values", () =>
        {
            var image = SyntheticImage();
            var profile = Profile(image);
            var changed = image.ToArray(); changed[^1] = 1;
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(changed, profile));
            Write(image, 0x210, 99);
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image, Profile(image)));
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image.AsSpan(0, 200), profile));
            return Task.CompletedTask;
        });

        await run("splash patch refuses wrong RVA mapping overlapping fields and changed output hash", () =>
        {
            var image = SyntheticImage();
            var profile = Profile(image);
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image,
                profile with { Changes = [new ScalarChange(0x1010, 0x211, 4294967292, 476)] }));
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image,
                profile with { Changes = [new ScalarChange(0x1010, 0x210, 4294967292, 476), new ScalarChange(0x1012, 0x212, 0, 1)] }));
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image, profile with { ModifiedHash = new string('0', 64) }));
            return Task.CompletedTask;
        });

        await run("splash patch checks PE architecture section bounds and relocations", () =>
        {
            var wrongMachine = SyntheticImage();
            BinaryPrimitives.WriteUInt16LittleEndian(wrongMachine.AsSpan(0x84, 2), 0x8664);
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(wrongMachine, Profile(wrongMachine)));
            var outside = SyntheticImage(); Write(outside, 0x178 + 16, 16);
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(outside, Profile(outside)));
            var relocated = SyntheticImage();
            Write(relocated, 0x98 + 96 + 5 * 8, 0x1100); Write(relocated, 0x98 + 100 + 5 * 8, 12);
            Write(relocated, 0x300, 0x1000); Write(relocated, 0x304, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(relocated.AsSpan(0x308, 2), 0x3010);
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(relocated, Profile(relocated)));
            return Task.CompletedTask;
        });

        foreach (var id in new[] { "yugi", "kaiba", "joey" })
        {
            await run($"global splash toggle selects private copy then original for {id}", async () =>
            {
                var root = Path.Combine(fixtures, "mod-" + id);
                var image = SyntheticImage();
                var profile = Profile(image, id);
                var game = await Installation(root, id, image);
                var data = Path.Combine(root, "launcher-data");
                var preparer = new GameLaunchPreparer(data, [profile]);
                Require(await preparer.PrepareExecutableAsync(game, false) == game.ExecutablePath, "Disabled mod changed executable.");
                Require(!Directory.Exists(data), "Disabled mod created cache state.");
                var cached = await preparer.PrepareExecutableAsync(game, true);
                Require(cached != game.ExecutablePath && Path.GetFileName(cached) == GameCatalog.Get(id).ExecutableName, "Private copy selection failed.");
                Require(SkipSplashPatch.MatchesHash(await File.ReadAllBytesAsync(cached), profile.ModifiedHash), "Cache result did not match profile.");
                Require(image.SequenceEqual(await File.ReadAllBytesAsync(game.ExecutablePath)), "Installed source changed.");
                Require(await preparer.PrepareExecutableAsync(game, false) == game.ExecutablePath, "Unticking failed to unload for next launch.");
                Require(await File.ReadAllTextAsync(game.DataPath) == "Synthetic archive marker.", "Game asset changed.");
                foreach (var windows in new[] { true, false })
                {
                    var plan = LaunchPlan.CreatePrepared(game, new LauncherSettings { WineExecutable = "runner with spaces" }, cached, windows);
                    Require(plan.WorkingDirectory == Path.GetDirectoryName(game.ExecutablePath), "Cache launch lost original WorkingDirectory.");
                    Require(windows ? plan.FileName == cached : plan.ArgumentList.Last() == cached, "Prepared path was not selected.");
                    Require(!plan.UseShellExecute, "A shell was selected.");
                }
            });
        }

        await run("cache preparation serializes parallel requests and repairs corrupt owned cache", async () =>
        {
            var root = Path.Combine(fixtures, "mod-cache-reuse");
            var image = SyntheticImage();
            var profile = Profile(image);
            var game = await Installation(root, "yugi", image);
            var preparer = new GameLaunchPreparer(Path.Combine(root, "launcher-data"), [profile]);
            var paths = await Task.WhenAll(preparer.PrepareExecutableAsync(game, true), preparer.PrepareExecutableAsync(game, true));
            Require(paths[0] == paths[1], "Concurrent preparation selected inconsistent caches.");
            var cached = paths[0];
            var stamp = File.GetLastWriteTimeUtc(cached);
            _ = await preparer.PrepareExecutableAsync(game, true);
            Require(stamp == File.GetLastWriteTimeUtc(cached), "Valid cache was unnecessarily overwritten.");
            await File.WriteAllTextAsync(cached, "Corrupt owned cache.");
            _ = await preparer.PrepareExecutableAsync(game, true);
            Require(SkipSplashPatch.MatchesHash(await File.ReadAllBytesAsync(cached), profile.ModifiedHash), "Corrupt cache was not repaired.");
            Require(!Directory.EnumerateFiles(Path.GetDirectoryName(cached)!, ".preparing-*").Any(), "Temporary cache output leaked.");
            var changed = image.ToArray(); changed[^1] = 3;
            await File.WriteAllBytesAsync(game.ExecutablePath, changed);
            await ExpectAsync<InvalidDataException>(() => preparer.PrepareExecutableAsync(game, true));
            Require(SkipSplashPatch.MatchesHash(await File.ReadAllBytesAsync(cached), profile.ModifiedHash), "Unsupported source changed existing cache.");
        });

        await run("cache preparation refuses installation output directories and honors cancellation", async () =>
        {
            var root = Path.Combine(fixtures, "mod-cache-paths");
            var image = SyntheticImage();
            var game = await Installation(root, "yugi", image);
            var profile = Profile(image);
            var unsafePreparer = new GameLaunchPreparer(Path.GetDirectoryName(game.ExecutablePath), [profile]);
            await ExpectAsync<InvalidDataException>(() => unsafePreparer.PrepareExecutableAsync(game, true));
            var data = Path.Combine(root, "launcher-data");
            var preparer = new GameLaunchPreparer(data, [profile]);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => preparer.PrepareExecutableAsync(game, true, cancellation.Token));
            Require(!Directory.Exists(data), "Cancelled preparation wrote cache output.");
        });

        await run("production mod refuses synthetic executables without starting any process", async () =>
        {
            var root = Path.Combine(fixtures, "mod-unsupported-session");
            var game = await Installation(root, "yugi", SyntheticImage());
            var runner = new FakeRunner();
            await using var service = new GameSessionService(Path.Combine(root, "launcher-data"), runner);
            var result = await service.LaunchAsync(game, new LauncherSettings { SkipSplashScreens = true });
            Require(!result.Success && runner.Starts == 0 && !service.AnyRunning, "Unsupported executable was launched.");
            Require(result.Error?.Contains("Untick", StringComparison.Ordinal) == true, "Unsupported-version error does not explain recovery.");
        });

        await run("session prepares a mod snapshot and starts only its returned path", async () =>
        {
            var root = Path.Combine(fixtures, "mod-session-snapshot");
            var image = SyntheticImage();
            var game = await Installation(root, "yugi", image);
            var cached = Path.Combine(root, "prepared", "yugi_pc.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!); await File.WriteAllBytesAsync(cached, image);
            var runner = new FakeRunner();
            var preparer = new PausingPreparer(cached);
            var settings = new LauncherSettings { SkipSplashScreens = true };
            await using var service = new GameSessionService(Path.Combine(root, "launcher-data"), runner, preparer: preparer);
            var launch = service.LaunchAsync(game, settings);
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            settings.SkipSplashScreens = false;
            preparer.Release.TrySetResult();
            Require((await launch).Success, "Prepared fake process did not start.");
            Require(preparer.SelectedMod && runner.Starts == 1 && runner.LastPlan is not null, "Launch snapshot changed while preparing.");
            Require(OperatingSystem.IsWindows() ? runner.LastPlan!.FileName == cached : runner.LastPlan!.ArgumentList.Last() == cached,
                "Session ignored the prepared executable.");
            Require(runner.LastPlan!.WorkingDirectory == Path.GetDirectoryName(game.ExecutablePath), "Session changed asset root.");
        });

        await run("cancelling launch preparation never starts the fake process", async () =>
        {
            var root = Path.Combine(fixtures, "mod-session-cancel");
            var game = await Installation(root, "yugi", SyntheticImage());
            var runner = new FakeRunner();
            var preparer = new PausingPreparer(game.ExecutablePath);
            using var cancellation = new CancellationTokenSource();
            await using var service = new GameSessionService(Path.Combine(root, "launcher-data"), runner, preparer: preparer);
            var launch = service.LaunchAsync(game, new LauncherSettings { SkipSplashScreens = true }, cancellation.Token);
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            Require(!(await launch).Success && runner.Starts == 0 && !service.AnyRunning, "Cancelled preparation started a process.");
        });

        await run("concurrent disposal cancels a suspended launch preparer without starting a process", async () =>
        {
            var root = Path.Combine(fixtures, "mod-session-dispose");
            var game = await Installation(root, "yugi", SyntheticImage());
            var runner = new FakeRunner();
            var preparer = new PausingPreparer(game.ExecutablePath);
            var service = new GameSessionService(Path.Combine(root, "launcher-data"), runner, preparer: preparer);
            var launch = service.LaunchAsync(game, new LauncherSettings { SkipSplashScreens = true });
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var first = service.DisposeAsync().AsTask();
            var second = service.DisposeAsync().AsTask();
            Require(ReferenceEquals(first, second), "Concurrent disposal did not share one lifetime operation.");
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            Require(!(await launch).Success && runner.Starts == 0 && !service.AnyRunning,
                "Shutdown waited forever or started a process after cancellation.");
            Require(!(await service.LaunchAsync(game, new LauncherSettings())).Success, "Disposed service accepted a later launch.");
        });

        await run("a cancelled launch waiting behind preparation returns promptly without releasing its gate", async () =>
        {
            var root = Path.Combine(fixtures, "mod-session-queued-cancel");
            var game = await Installation(root, "yugi", SyntheticImage());
            var runner = new FakeRunner();
            var preparer = new PausingPreparer(game.ExecutablePath);
            var service = new GameSessionService(Path.Combine(root, "launcher-data"), runner, preparer: preparer);
            var first = service.LaunchAsync(game, new LauncherSettings { SkipSplashScreens = true });
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            var second = service.LaunchAsync(game, new LauncherSettings(), cancellation.Token);
            cancellation.Cancel();
            Require(!(await second.WaitAsync(TimeSpan.FromSeconds(3))).Success && !first.IsCompleted && runner.Starts == 0,
                "Cancellation remained blocked behind another launch or released the first launch gate.");
            var preCancelled = await service.LaunchAsync(game, new LauncherSettings(), new CancellationToken(true)).WaitAsync(TimeSpan.FromSeconds(3));
            Require(!preCancelled.Success && runner.Starts == 0, "A pre-cancelled queued launch was not refused.");
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Require(!(await first).Success && runner.Starts == 0, "Disposal failed after cancelling a queued launch.");
        });
    }

    private static byte[] SyntheticImage()
    {
        // Inert authored PE structure with two data scalars. No entry point, instructions or game bytes.
        var image = new byte[1024];
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0, 2), 0x5A4D);
        Write(image, 0x3C, 0x80); Write(image, 0x80, 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84, 2), 0x14C);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94, 2), 224);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x96, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98, 2), 0x10B);
        Write(image, 0x98 + 92, 16);
        Write(image, 0x178 + 12, 0x1000); Write(image, 0x178 + 16, 512); Write(image, 0x178 + 20, 512);
        Write(image, 0x210, 4294967292); Write(image, 0x220, 0);
        return image;
    }

    private static SplashProfile Profile(byte[] image, string id = "yugi")
    {
        var modified = image.ToArray(); Write(modified, 0x210, 476); Write(modified, 0x220, 1);
        return new(id, image.Length, Hash(image), Hash(modified),
            [new ScalarChange(0x1010, 0x210, 4294967292, 476), new ScalarChange(0x1020, 0x220, 0, 1)]);
    }

    private static async Task<GameInstallation> Installation(string root, string id, byte[] image)
    {
        var folder = Path.Combine(root, "game"); Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, GameCatalog.Get(id).ExecutableName);
        var data = Path.Combine(folder, "data.dat"); var voice = Path.Combine(folder, "Voice.dat");
        await File.WriteAllBytesAsync(executable, image);
        await File.WriteAllTextAsync(data, "Synthetic archive marker.");
        await File.WriteAllTextAsync(voice, "Synthetic archive marker.");
        return new(id, executable, data, voice, folder);
    }

    private static uint Read(byte[] image, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
    private static void Write(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);
    private static string Hash(byte[] image) => Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class PausingPreparer(string path) : IGameLaunchPreparer
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SelectedMod { get; private set; }
        public async Task<string> PrepareExecutableAsync(GameInstallation installation, bool skipSplashScreens, CancellationToken cancellationToken)
        {
            SelectedMod = skipSplashScreens; Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken); return path;
        }
    }

    private sealed class FakeRunner : IGameProcessRunner
    {
        internal int Starts { get; private set; }
        internal ProcessStartInfo? LastPlan { get; private set; }
        public IGameProcess Start(ProcessStartInfo startInfo)
        { Starts++; LastPlan = startInfo; return new FakeProcess(); }
    }
    private sealed class FakeProcess : IGameProcess
    {
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public void Dispose() { }
    }
}
