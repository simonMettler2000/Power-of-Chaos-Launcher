using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Discovery;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

internal static class InteractionChecks
{
    private static readonly GameModOptions[] Selections =
    [new(), new(true, false), new(false, true), new(true, true)];

    internal static async Task VerifyInstalledProfiles(Func<string, Func<Task>, Task> run)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "KONAMI");
        var games = (await new GameDiscovery().ScanAsync(root)).Games;
        foreach (var game in games)
        {
            foreach (var options in Selections.Where(options => options.AnyEnabled))
            {
                await run($"read-only installed {game.GameId} {options.CacheKey} full image verifies in memory", async () =>
                {
                    var profile = GameModProfiles.Profiles.Single(item => item.GameId == game.GameId && item.Options == options);
                    var original = await File.ReadAllBytesAsync(game.ExecutablePath);
                    var modified = SkipSplashPatch.Apply(original, profile);
                    var changed = Enumerable.Range(0, original.Length).Where(index => original[index] != modified[index]).ToArray();
                    var characterCount = game.GameId switch { "yugi" => 34, "kaiba" => 44, "joey" => 40, _ => throw new InvalidOperationException() };
                    var expectedCount = (options.SkipSplashScreens ? 5 : 0) + (options.SkipCharacterInteractions ? characterCount : 0);
                    Require(changed.Length == expectedCount && changed.All(index => profile.Changes.Any(change =>
                        index >= change.FileOffset && index < change.FileOffset + change.Width)), "Production changes escaped the declared fields.");
                    Require(Hash(original) == profile.OriginalHash && Hash(modified) == profile.ModifiedHash, "Full production hashes differ.");
                    Require(Hash(await File.ReadAllBytesAsync(game.ExecutablePath)) == profile.OriginalHash, "Read-only original changed.");
                });
            }
        }
        Require(games.Count == 3, "All three installed inputs are required by --installed-mods.");
        Console.WriteLine("Installed mod checks used memory only; no original, copied or modified game was executed or written.");
    }

    internal static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        await run("both global preferences default off and persist independently through atomic reloads", async () =>
        {
            var directory = Path.Combine(fixtures, "interaction-settings");
            var store = new SettingsStore(directory);
            var initial = await store.LoadAsync();
            Require(!initial.SkipSplashScreens && !initial.SkipCharacterInteractions, "Fresh settings enable a mod.");
            foreach (var options in Selections.Concat(Selections.Reverse()))
            {
                var settings = new LauncherSettings
                { SkipSplashScreens = options.SkipSplashScreens, SkipCharacterInteractions = options.SkipCharacterInteractions };
                var saving = store.SaveAsync(settings);
                settings.SkipSplashScreens = !settings.SkipSplashScreens;
                settings.SkipCharacterInteractions = !settings.SkipCharacterInteractions;
                await saving;
                var loaded = await new SettingsStore(directory).LoadAsync();
                Require(loaded.SkipSplashScreens == options.SkipSplashScreens && loaded.SkipCharacterInteractions == options.SkipCharacterInteractions,
                    "Save did not capture and reload both preference values together.");
                Require(!Directory.EnumerateFiles(directory, "launcher-settings.tmp-*").Any(), "Atomic settings temporary leaked.");
            }
        });

        await run("one-byte and four-byte fields map from nonidentity RVAs and preserve neighboring data", () =>
        {
            var original = Image();
            var profile = Profiles(original, "yugi").Single(item => item.Options == new GameModOptions(false, true));
            var modified = SkipSplashPatch.Apply(original, profile);
            Require(original[0x230] == 7 && Read(original, 0x240) == 13, "Original synthetic image was changed.");
            Require(modified[0x230] == 9 && Read(modified, 0x240) == 42, "Mixed-width values did not change.");
            Require(Enumerable.Range(0, original.Length).Where(index => original[index] != modified[index]).SequenceEqual(new[] { 0x230, 0x240 }),
                "Mixed-width edit touched neighboring bytes.");
            return Task.CompletedTask;
        });

        await run("field widths values overlap aliases and section ends fail closed", () =>
        {
            var image = Image();
            var profile = Profiles(image, "yugi")[1];
            foreach (var change in new[]
            {
                new ScalarChange(0x1030, 0x230, 7, 9, 0), new ScalarChange(0x1030, 0x230, 7, 9, 2),
                new ScalarChange(0x1030, 0x230, 256, 9, 1), new ScalarChange(0x1030, 0x230, 7, 256, 1),
                new ScalarChange(0x1030, 0x230, 8, 9, 1),
                new ScalarChange(0x1030, 0x231, 7, 9, 1), new ScalarChange(0x1200, 0x400, 7, 9, 1),
                new ScalarChange(0x11FF, 0x3FF, 0, 9, 4)
            }) Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image, profile with { Changes = [change] }));
            var shared = new ScalarChange(0x1030, 0x230, 7, 9, 1);
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image, profile with { Changes = [shared, shared] }));
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image, profile with
            { Changes = [new ScalarChange(0x1030, 0x230, 7, 9, 4), new ScalarChange(0x1033, 0x233, 0, 1, 1)] }));
            var alias = image.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(alias.AsSpan(0x86, 2), 2);
            alias.AsSpan(0x178, 40).CopyTo(alias.AsSpan(0x178 + 40, 40));
            Expect<InvalidDataException>(() => SkipSplashPatch.Apply(alias, profile with { OriginalHash = Hash(alias) }));
            var edgeOutput = image.ToArray(); edgeOutput[0x3FF] = 9;
            _ = SkipSplashPatch.Apply(image, profile with
            { Changes = [new ScalarChange(0x11FF, 0x3FF, 0, 9, 1)], ModifiedHash = Hash(edgeOutput) });
            return Task.CompletedTask;
        });

        await run("relocation spans reject straddling either field width but allow adjacent data", () =>
        {
            foreach (var width in new[] { 1, 4 })
            {
                foreach (var relocationOffset in new[] { 0x2D, 0x30, 0x30 + width - 1 })
                {
                    var image = WithRelocation(relocationOffset);
                    var profile = SingleField(image, width);
                    Expect<InvalidDataException>(() => SkipSplashPatch.Apply(image, profile));
                }
                foreach (var offset in new[] { 0x2C, 0x30 + width })
                {
                    var adjacent = WithRelocation(offset);
                    _ = SkipSplashPatch.Apply(adjacent, SingleField(adjacent, width));
                }
            }
            return Task.CompletedTask;
        });

        foreach (var id in new[] { "yugi", "kaiba", "joey" })
        {
            await run($"{id} stock splash character and combined selection stay independent across cached launches", async () =>
            {
                var root = Path.Combine(fixtures, "interaction-selection-" + id);
                var original = Image();
                var profiles = Profiles(original, id);
                var game = await SplashChecks.Installation(root, id, original);
                var data = Path.Combine(root, "launcher-data");
                var preparer = new GameLaunchPreparer(data, profiles);
                var paths = new Dictionary<GameModOptions, string>();
                Require(await preparer.PrepareExecutableAsync(game, new GameModOptions()) == game.ExecutablePath && !Directory.Exists(data),
                    "Stock launch created cache state.");
                foreach (var options in new[] { Selections[3], Selections[1], Selections[2], Selections[0], Selections[2], Selections[1], Selections[3] })
                {
                    var path = await preparer.PrepareExecutableAsync(game, options);
                    if (paths.TryGetValue(options, out var previous)) Require(path == previous, "Same combination chose a different cache.");
                    paths[options] = path;
                    if (options.AnyEnabled)
                    {
                        var profile = profiles.Single(item => item.Options == options);
                        Require(path.Contains(profile.ModifiedHash, StringComparison.Ordinal), "Cache path omitted full output hash.");
                        Require(Hash(await File.ReadAllBytesAsync(path)) == profile.ModifiedHash, "Wrong combination was selected.");
                    }
                    else Require(path == game.ExecutablePath, "Unticking both selected a cached executable.");
                }
                Require(paths.Values.Distinct().Count() == 4, "Combination caches alias each other or the original.");
                Require(original.SequenceEqual(await File.ReadAllBytesAsync(game.ExecutablePath)), "Synthetic installation changed.");
                var corrupt = original.ToArray(); corrupt[^1] = 1;
                await File.WriteAllBytesAsync(game.ExecutablePath, corrupt);
                foreach (var options in Selections.Where(options => options.AnyEnabled))
                    await ExpectAsync<InvalidDataException>(() => preparer.PrepareExecutableAsync(game, options));
                Require(await preparer.PrepareExecutableAsync(game, new GameModOptions()) == game.ExecutablePath, "Stock selection depended on old cache validation.");
            });
        }

        await run("different output revisions for the same combination retain separate verified caches", async () =>
        {
            var root = Path.Combine(fixtures, "interaction-output-revision");
            var original = Image();
            var game = await SplashChecks.Installation(root, "yugi", original);
            var options = new GameModOptions(false, true);
            var firstProfile = Profiles(original, "yugi").Single(profile => profile.Options == options);
            var nextOutput = original.ToArray(); nextOutput[0x230] = 10; Write(nextOutput, 0x240, 42);
            var secondProfile = firstProfile with
            {
                ModifiedHash = Hash(nextOutput),
                Changes = [new ScalarChange(0x1030, 0x230, 7, 10, 1), new ScalarChange(0x1040, 0x240, 13, 42)]
            };
            var data = Path.Combine(root, "data");
            var first = await new GameLaunchPreparer(data, [firstProfile]).PrepareExecutableAsync(game, options);
            var second = await new GameLaunchPreparer(data, [secondProfile]).PrepareExecutableAsync(game, options);
            Require(first != second && Hash(await File.ReadAllBytesAsync(first)) == firstProfile.ModifiedHash
                && Hash(await File.ReadAllBytesAsync(second)) == secondProfile.ModifiedHash,
                "A new expected output revision overwrote or reused the old cache.");
        });

        await run("missing combinations and unsupported production inputs never start a process", async () =>
        {
            var root = Path.Combine(fixtures, "interaction-unsupported");
            var game = await SplashChecks.Installation(root, "kaiba", Image());
            var partial = new GameLaunchPreparer(Path.Combine(root, "partial-data"), [Profiles(Image(), "kaiba")[0]]);
            await ExpectAsync<InvalidDataException>(() => partial.PrepareExecutableAsync(game, new GameModOptions(true, true)));
            foreach (var options in Selections.Where(options => options.AnyEnabled))
            {
                var runner = new FakeRunner();
                await using var service = new GameSessionService(Path.Combine(root, options.CacheKey), runner);
                var result = await service.LaunchAsync(game, new LauncherSettings
                { SkipSplashScreens = options.SkipSplashScreens, SkipCharacterInteractions = options.SkipCharacterInteractions });
                Require(!result.Success && runner.Starts == 0 && !service.AnyRunning && result.Error!.Contains("Untick", StringComparison.Ordinal),
                    "Unsupported mod combination started a process or omitted recovery advice.");
            }
        });

        await run("both options are immutable through preparation and original working directory is retained", async () =>
        {
            var root = Path.Combine(fixtures, "interaction-snapshot");
            var game = await SplashChecks.Installation(root, "joey", Image());
            var runner = new FakeRunner();
            var preparer = new PausingPreparer(game.ExecutablePath);
            var settings = new LauncherSettings { SkipSplashScreens = true, SkipCharacterInteractions = true };
            await using var service = new GameSessionService(Path.Combine(root, "data"), runner, preparer: preparer);
            var launch = service.LaunchAsync(game, settings);
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            settings.SkipSplashScreens = false; settings.SkipCharacterInteractions = false;
            preparer.Release.TrySetResult();
            Require((await launch).Success && preparer.Captured == new GameModOptions(true, true) && runner.Starts == 1,
                "Preparation did not retain both launch options.");
            Require(runner.LastPlan?.WorkingDirectory == Path.GetDirectoryName(game.ExecutablePath), "Launch lost installed asset directory.");
            Require(!(await service.LaunchAsync(game, settings)).Success && runner.Starts == 1, "One-session guard was lost.");
        });

        await run("queued launch snapshots both options at request time before entering the launch gate", async () =>
        {
            var root = Path.Combine(fixtures, "interaction-queued-snapshot");
            var game = await SplashChecks.Installation(root, "yugi", Image());
            var preparer = new FirstFailsPreparer(game.ExecutablePath);
            var runner = new FakeRunner();
            await using var service = new GameSessionService(Path.Combine(root, "data"), runner, preparer: preparer);
            var first = service.LaunchAsync(game, new LauncherSettings { SkipSplashScreens = true });
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queuedSettings = new LauncherSettings { SkipCharacterInteractions = true };
            var second = service.LaunchAsync(game, queuedSettings);
            queuedSettings.SkipSplashScreens = true; queuedSettings.SkipCharacterInteractions = false;
            preparer.Release.TrySetResult();
            Require(!(await first).Success && (await second).Success && runner.Starts == 1, "Queued launch did not follow first preparation failure.");
            Require(preparer.SecondOptions == new GameModOptions(false, true), "Queued request read later UI options.");
        });

        await run("character and combined preparation cancel safely through concurrent disposal", async () =>
        {
            foreach (var options in new[] { Selections[2], Selections[3] })
            {
                var root = Path.Combine(fixtures, "interaction-cancel-" + options.CacheKey);
                var game = await SplashChecks.Installation(root, "yugi", Image());
                var runner = new FakeRunner(); var preparer = new PausingPreparer(game.ExecutablePath);
                var service = new GameSessionService(Path.Combine(root, "data"), runner, preparer: preparer);
                var launch = service.LaunchAsync(game, new LauncherSettings
                { SkipSplashScreens = options.SkipSplashScreens, SkipCharacterInteractions = options.SkipCharacterInteractions });
                await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var first = service.DisposeAsync().AsTask(); var second = service.DisposeAsync().AsTask();
                Require(ReferenceEquals(first, second), "Disposal did not share one task.");
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
                Require(!(await launch).Success && runner.Starts == 0 && !service.AnyRunning, "Cancelled launch started a process.");
            }
        });
    }

    private static byte[] Image()
    {
        var image = SplashChecks.SyntheticImage(); image[0x230] = 7; Write(image, 0x240, 13); return image;
    }

    private static SplashProfile[] Profiles(byte[] original, string id)
    {
        var splash = original.ToArray(); Write(splash, 0x210, 476); Write(splash, 0x220, 1);
        var character = original.ToArray(); character[0x230] = 9; Write(character, 0x240, 42);
        var both = splash.ToArray(); both[0x230] = 9; Write(both, 0x240, 42);
        ScalarChange[] splashFields = [new(0x1010, 0x210, 4294967292, 476), new(0x1020, 0x220, 0, 1)];
        ScalarChange[] characterFields = [new(0x1030, 0x230, 7, 9, 1), new(0x1040, 0x240, 13, 42)];
        return
        [
            new(id, original.Length, Hash(original), Hash(splash), splashFields),
            new(id, original.Length, Hash(original), Hash(character), characterFields) { Options = new(false, true) },
            new(id, original.Length, Hash(original), Hash(both), splashFields.Concat(characterFields).ToArray()) { Options = new(true, true) }
        ];
    }

    private static byte[] WithRelocation(int offset)
    {
        var image = Image();
        Write(image, 0x98 + 96 + 5 * 8, 0x1100); Write(image, 0x98 + 100 + 5 * 8, 12);
        Write(image, 0x300, 0x1000); Write(image, 0x304, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x308, 2), checked((ushort)(0x3000 + offset)));
        return image;
    }

    private static SplashProfile SingleField(byte[] image, int width)
    {
        var modified = image.ToArray();
        if (width == 1) modified[0x230] = 9; else Write(modified, 0x230, 9);
        return new("yugi", image.Length, Hash(image), Hash(modified), [new ScalarChange(0x1030, 0x230, 7, 9, width)]);
    }

    private static uint Read(byte[] image, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
    private static void Write(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);
    private static string Hash(byte[] image) => Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private class PausingPreparer(string path) : IGameLaunchPreparer
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GameModOptions? Captured { get; private set; }
        public async Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options, CancellationToken cancellationToken)
        { Captured = options; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return path; }
    }

    private sealed class FirstFailsPreparer(string path) : IGameLaunchPreparer
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GameModOptions? SecondOptions { get; private set; }
        private int calls;
        public async Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options, CancellationToken cancellationToken)
        {
            if (++calls == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); throw new InvalidDataException("Synthetic preparation failure."); }
            SecondOptions = options; return path;
        }
    }

    private sealed class FakeRunner : IGameProcessRunner
    {
        internal int Starts { get; private set; }
        internal ProcessStartInfo? LastPlan { get; private set; }
        public IGameProcess Start(ProcessStartInfo startInfo) { Starts++; LastPlan = startInfo; return new FakeProcess(); }
    }
    private sealed class FakeProcess : IGameProcess
    {
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public void Dispose() { }
    }
}
