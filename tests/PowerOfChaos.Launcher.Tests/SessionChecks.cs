using System.Diagnostics;
using System.Text.Json;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.TestFixtures;

namespace PowerOfChaos.Launcher.Tests;

internal static class SessionChecks
{
    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        var game = Installation(fixtures, "fake process game; spaces", "yugi");

        await run("native launch plans preserve paths and never use a command shell", () =>
        {
            var plan = LaunchPlan.Create(game, new LauncherSettings(), isWindows: true);
            Check(plan.FileName == game.ExecutablePath, "Native executable path changed.");
            Check(plan.WorkingDirectory == Path.GetDirectoryName(game.ExecutablePath), "Native working directory changed.");
            Check(!plan.UseShellExecute && plan.ArgumentList.Count == 0, "Native launch unexpectedly invokes a shell or adds arguments.");
            return Task.CompletedTask;
        });

        await run("Wine plans keep runner and executable as separate arguments", () =>
        {
            var settings = new LauncherSettings { WineExecutable = "/opt/runner with spaces/wine" };
            var plan = LaunchPlan.Create(game, settings, isWindows: false);
            Check(plan.FileName == settings.WineExecutable, "Wine runner was split or replaced.");
            Check(!plan.UseShellExecute, "Wine launch unexpectedly uses a shell.");
            Check(plan.ArgumentList.SequenceEqual(new[] { "start", "/wait", "/unix", game.ExecutablePath }),
                "Wine executable path must remain a single argument after /unix.");
            return Task.CompletedTask;
        });

        await run("launch rejects missing files and mismatched game executables", () =>
        {
            Expect<FileNotFoundException>(() => LaunchPlan.Create(game with { DataPath = game.DataPath + ".missing" }, new LauncherSettings()));
            Expect<ArgumentException>(() => LaunchPlan.Create(game with { GameId = "kaiba" }, new LauncherSettings()));
            Expect<ArgumentException>(() => LaunchPlan.Create(game with { GameId = "unknown" }, new LauncherSettings()));
            return Task.CompletedTask;
        });

        await run("fake process sessions measure time, prevent overlap, and persist totals", async () =>
        {
            var folder = Path.Combine(fixtures, "fake-session-state");
            var clock = new ManualClock();
            var runner = new FakeRunner();
            await using (var service = new GameSessionService(folder, runner, clock, TimeSpan.FromHours(1), temporaryCollections: TestCollectionProfiles.Create()))
            {
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.SessionsChanged += (_, _) => { if (!service.AnyRunning) finished.TrySetResult(); };
                var result = await service.LaunchAsync(game, new LauncherSettings());
                Check(result.Success && runner.Starts == 1, "Injected fake process was not started exactly once.");
                Check(service.IsRunning("yugi"), "Session must be marked running.");
                var overlap = await service.LaunchAsync(game, new LauncherSettings());
                Check(!overlap.Success && runner.Starts == 1, "Overlapping session launched another process.");
                clock.Advance(TimeSpan.FromSeconds(97));
                var runningTotals = await service.GetPlaytimeSecondsAsync();
                Check(runningTotals["yugi"] == 97, "Elapsed monotonic time was not reflected in live totals.");
                runner.Process.Complete();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!service.AnyRunning, "Completed fake process remains active.");
                Check((await service.GetPlaytimeSecondsAsync())["yugi"] == 97, "Final total changed unexpectedly.");
            }
            await using var reloaded = new GameSessionService(folder, new FakeRunner(), clock, temporaryCollections: TestCollectionProfiles.Create());
            Check((await reloaded.GetPlaytimeSecondsAsync())["yugi"] == 97, "Total did not survive service recreation.");
        });

        await run("restart recovery retains checkpoint but excludes unknown offline time", async () =>
        {
            var folder = Path.Combine(fixtures, "restart-session-state");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "playtime.json"), JsonSerializer.Serialize(new
            {
                Totals = new Dictionary<string, long> { ["kaiba"] = 42 },
                ActiveGameId = "kaiba",
                LastCheckpointUtc = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }));
            var runner = new FakeRunner();
            await using var service = new GameSessionService(folder, runner, new ManualClock(), temporaryCollections: TestCollectionProfiles.Create());
            Check((await service.GetPlaytimeSecondsAsync())["kaiba"] == 42, "Offline interval was incorrectly counted.");
            Check(!service.AnyRunning && runner.Starts == 0, "Recovery unexpectedly starts a game or invents an active session.");
        });

        await run("storage failure prevents starting a process", async () =>
        {
            var blockedDirectory = Path.Combine(fixtures, "blocked-session-directory");
            await File.WriteAllTextAsync(blockedDirectory, "This is a file, not a directory.");
            var runner = new FakeRunner();
            await using var service = new GameSessionService(blockedDirectory, runner, temporaryCollections: TestCollectionProfiles.Create());
            var result = await service.LaunchAsync(game, new LauncherSettings());
            Check(!result.Success && runner.Starts == 0 && !service.AnyRunning, "Untracked process started after storage failure.");
        });

        await run("disposing tracking records elapsed time without completing the fake game", async () =>
        {
            var folder = Path.Combine(fixtures, "disposed-session-state");
            var runner = new FakeRunner();
            var clock = new ManualClock();
            var service = new GameSessionService(folder, runner, clock, TimeSpan.FromHours(1), temporaryCollections: TestCollectionProfiles.Create());
            Check((await service.LaunchAsync(game, new LauncherSettings())).Success, "Fake process did not start.");
            clock.Advance(TimeSpan.FromSeconds(13));
            await service.DisposeAsync();
            Check(runner.Process.Disposed, "Tracking process handle was not released.");
            Check(!runner.Process.Completed, "Tracking disposal must not terminate a game.");
            await using var reloaded = new GameSessionService(folder, new FakeRunner(), clock, temporaryCollections: TestCollectionProfiles.Create());
            Check((await reloaded.GetPlaytimeSecondsAsync())["yugi"] == 13, "Disposal lost elapsed playtime.");
        });

        await run("malformed playtime is preserved and recovers without a process", async () =>
        {
            var folder = Path.Combine(fixtures, "corrupt-playtime");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "playtime.json");
            await File.WriteAllTextAsync(path, "not JSON");
            var runner = new FakeRunner();
            await using var service = new GameSessionService(folder, runner, temporaryCollections: TestCollectionProfiles.Create());
            Check((await service.GetPlaytimeSecondsAsync()).Count == 0, "Malformed state did not reset totals.");
            Check(Directory.EnumerateFiles(folder, "playtime.json.corrupt-*").Any(), "Malformed playtime was not preserved.");
            Check(runner.Starts == 0, "Reading corrupt playtime started a process.");
        });

        await run("all launch modes hold the shared collection lease until their fake session ends", async () =>
        {
            var folder = Path.Combine(fixtures, "shared-profile-session");
            var profiles = new TemporaryCollectionService(Path.Combine(folder, "profiles"), hasRunningGames: () => false);
            var firstRunner = new FakeRunner();
            var secondRunner = new FakeRunner();
            var first = new GameSessionService(Path.Combine(folder, "first"), firstRunner, temporaryCollections: profiles);
            await using var second = new GameSessionService(Path.Combine(folder, "second"), secondRunner, temporaryCollections: profiles);
            try
            {
                Check((await first.LaunchAsync(game, new LauncherSettings())).Success, "First synthetic session failed.");
                var blocked = await second.LaunchAsync(game, new LauncherSettings());
                Check(!blocked.Success && secondRunner.Starts == 0, "Another launcher bypassed the shared session lease.");
                try { await profiles.SetEnabledAsync(false, [game]); throw new Exception("A live session allowed a profile transition."); }
                catch (InvalidOperationException) { }
                firstRunner.Process.Complete();
                await first.DisposeAsync();
                Check((await second.LaunchAsync(game, new LauncherSettings())).Success, "Completed session retained its lease.");
                secondRunner.Process.Complete();
            }
            finally { await first.DisposeAsync(); }
        });

        await run("tracking cancellation never restores temporary cards underneath a still-running fake game", async () =>
        {
            var folder = Path.Combine(fixtures, "temporary-session-disposal");
            var temporaryGame = Installation(folder, "game", "yugi");
            File.Delete(temporaryGame.DataPath);
            CollectionFixtures.WriteArchive(temporaryGame.DataPath);
            var save = Path.Combine(folder, "system.dat");
            var original = CollectionFixtures.Save([(CollectionFixtures.Slot(1), (byte)2)]);
            await File.WriteAllBytesAsync(save, original);
            var runner = new FakeRunner();
            var profiles = new TemporaryCollectionService(Path.Combine(folder, "vault"), new CardCollectionReader(_ => [save]),
                () => runner.Starts > 0 && !runner.Process.Completed);
            await profiles.SetEnabledAsync(true, [temporaryGame]);
            var unlocked = await File.ReadAllBytesAsync(save);
            var session = new GameSessionService(Path.Combine(folder, "tracking"), runner, temporaryCollections: profiles);
            Check((await session.LaunchAsync(temporaryGame, new LauncherSettings())).Success, "Temporary synthetic session failed.");
            await session.DisposeAsync();
            Check(!runner.Process.Completed && (await profiles.ReadStateAsync()).Enabled, "Tracking cancellation ended the game or temporary profile.");
            Check((await File.ReadAllBytesAsync(save)).SequenceEqual(unlocked), "Tracking cancellation restored a save under a live game.");
            try { await profiles.SetEnabledAsync(false, [temporaryGame]); throw new Exception("OS game guard was bypassed after tracking disposal."); }
            catch (InvalidOperationException) { }
            runner.Process.Complete();
            await profiles.SetEnabledAsync(false, [temporaryGame]);
            Check((await File.ReadAllBytesAsync(save)).SequenceEqual(original), "Original was not restored after the actual fake exit.");
        });
    }

    private static GameInstallation Installation(string fixtures, string name, string id)
    {
        var folder = Path.Combine(fixtures, name);
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, GameCatalog.Get(id).ExecutableName);
        var data = Path.Combine(folder, "data.dat");
        var voice = Path.Combine(folder, "Voice.dat");
        // These are inert marker files for launch-plan tests with a fake process runner only.
        foreach (var path in new[] { executable, data, voice }) File.WriteAllText(path, "Launcher test marker; never execute.");
        return new(id, executable, data, voice, folder);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class FakeRunner : IGameProcessRunner
    {
        public int Starts { get; private set; }
        public FakeProcess Process { get; } = new();
        public IGameProcess Start(ProcessStartInfo startInfo)
        {
            Starts++;
            return Process;
        }
    }

    private sealed class FakeProcess : IGameProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed => completion.Task.IsCompleted;
        public bool Disposed { get; private set; }
        public void Complete() => completion.TrySetResult();
        public Task WaitForExitAsync(CancellationToken cancellationToken) => completion.Task.WaitAsync(cancellationToken);
        public void Dispose() => Disposed = true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
}
