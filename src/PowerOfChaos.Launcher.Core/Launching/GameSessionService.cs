using System.Diagnostics;
using System.Text.Json;
using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.Core.Launching;

public sealed record LaunchResult(bool Success, string? Error = null);

public interface IGameProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IGameProcessRunner
{
    IGameProcess Start(ProcessStartInfo startInfo);
}

public sealed class SystemGameProcessRunner : IGameProcessRunner
{
    public IGameProcess Start(ProcessStartInfo startInfo) => new RunningProcess(
        Process.Start(startInfo) ?? throw new InvalidOperationException("The game process could not be started."));

    private sealed class RunningProcess(Process process) : IGameProcess
    {
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}

/// <summary>Tracks explicit launcher sessions; a shared collection guard also checks for games already running.</summary>
public sealed class GameSessionService : IAsyncDisposable
{
    private sealed class PlaytimeData
    {
        public Dictionary<string, long> Totals { get; set; } = [];
        public string? ActiveGameId { get; set; }
        public DateTimeOffset? LastCheckpointUtc { get; set; }
    }

    private sealed record ActiveSession(string GameId, string ProfileId, IGameProcess Process, long StartedTimestamp, long PreviousSeconds,
        PlaytimeData Playtime, IReadOnlyList<GameInstallation> Installations, IAsyncDisposable CollectionLease);
    private readonly string _file;
    private readonly IGameProcessRunner _runner;
    private readonly IGameLaunchPreparer _preparer;
    private readonly TemporaryCollectionService _temporaryCollections;
    private readonly PlayerProfiles? _profiles;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _checkpointInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposalLock = new();
    private Task? _disposal;
    private ActiveSession? _active;
    private Task? _monitor;
    private volatile bool _disposed;
    private const long MaximumSeconds = 100L * 366 * 24 * 60 * 60;

    public GameSessionService(string? dataDirectory = null, IGameProcessRunner? runner = null,
        TimeProvider? clock = null, TimeSpan? checkpointInterval = null, IGameLaunchPreparer? preparer = null,
        TemporaryCollectionService? temporaryCollections = null, PlayerProfiles? profiles = null)
    {
        _file = Path.Combine(dataDirectory ?? LauncherPaths.DataDirectory, "playtime.json");
        _runner = runner ?? new SystemGameProcessRunner();
        _preparer = preparer ?? new GameLaunchPreparer(dataDirectory);
        _temporaryCollections = temporaryCollections ?? new TemporaryCollectionService();
        _profiles = profiles;
        _clock = clock ?? TimeProvider.System;
        _checkpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(30);
        if (_checkpointInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
    }

    public event EventHandler? SessionsChanged;
    public string? LastWarning { get; private set; }
    public bool IsRunning(string gameId) => _active?.GameId == gameId;
    public bool AnyRunning => _active is not null;

    public async Task<IReadOnlyDictionary<string, long>> GetPlaytimeSecondsAsync(string? profileId = null)
    {
        await _gate.WaitAsync();
        try
        {
            profileId ??= _profiles?.Read().ActiveId ?? PlayerProfiles.BaseId;
            var active = _active?.ProfileId == profileId ? _active : null;
            var data = active?.Playtime ?? await LoadPlaytimeAsync(profileId);
            var snapshot = new Dictionary<string, long>(data.Totals);
            if (active is not null) snapshot[active.GameId] = CurrentSeconds(active);
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task<LaunchResult> LaunchAsync(GameInstallation installation, LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        // Capture before the first await, including when another launch owns the gate.
        var options = new GameModOptions(settings.SkipSplashScreens, settings.SkipCharacterInteractions,
            settings.RestoreOriginalAspectRatio, settings.HdCardArtwork,
            settings.HdCardArtwork ? settings.HdCardImageDirectory : null, settings.NewCardRewards, settings.CustomOpponentDecks);
        var installations = new[] { installation }.Concat(settings.Installations)
            .DistinctBy(game => game.GameId).ToArray();
        var launchSettings = new LauncherSettings
        {
            SkipSplashScreens = options.SkipSplashScreens,
            SkipCharacterInteractions = options.SkipCharacterInteractions,
            SpeedyMode = settings.SpeedyMode,
            RestoreOriginalAspectRatio = options.RestoreOriginalAspectRatio,
            HdCardArtwork = options.HdCardArtwork,
            HdCardImageDirectory = options.HdCardImageDirectory,
            NewCardRewards = options.NewCardRewards,
            CustomOpponentDecks = options.CustomOpponentDecks,
            WineExecutable = settings.WineExecutable,
            MinimizeOnLaunch = settings.MinimizeOnLaunch
        };
        CancellationTokenSource pendingCancellation;
        lock (_disposalLock)
        {
            if (_disposed) return new(false, "The launcher is closing. No game was started.");
            pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
        using var preparationCancellation = pendingCancellation;
        var enteredGate = false;
        var mountingStarted = false;
        IAsyncDisposable? collectionLease = null;
        try
        {
            await _gate.WaitAsync(preparationCancellation.Token);
            enteredGate = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) return new(false, "Close the running game before launching another.");
            _ = LaunchPlan.Create(installation, launchSettings); // Validate installed files before cache work.
            collectionLease = await _temporaryCollections.AcquireSessionLeaseAsync(installations,
                preparationCancellation.Token);
            var profileId = _profiles?.Read().ActiveId ?? PlayerProfiles.BaseId;
            var executable = await _preparer.PrepareExecutableAsync(installation, options,
                preparationCancellation.Token);
            var plan = LaunchPlan.CreatePrepared(installation, launchSettings, executable);
            var data = await LoadPlaytimeAsync(profileId, forLaunch: true);
            // Check that tracking can be persisted before starting an external application.
            await SavePlaytimeAsync(profileId, data);
            mountingStarted = _profiles is not null;
            preparationCancellation.Token.ThrowIfCancellationRequested();
            _profiles?.Mount(profileId, installation, installations, preparationCancellation.Token);
            var process = _runner.Start(plan);
            _active = new ActiveSession(installation.GameId, profileId, process, _clock.GetTimestamp(),
                data.Totals.GetValueOrDefault(installation.GameId), data, installations, collectionLease);
            collectionLease = null; // The monitor now owns the cross-launcher session lease.
            data.ActiveGameId = installation.GameId;
            data.LastCheckpointUtc = _clock.GetUtcNow();
            try { await SavePlaytimeAsync(profileId, data); }
            catch (Exception exception) when (IsStorageException(exception))
            {
                LastWarning = "The game started, but its playtime could not be saved.";
            }
            var active = _active;
            var shutdownToken = _shutdown.Token;
            _monitor = Task.Run(() => MonitorAsync(active, shutdownToken));
        }
        catch (OperationCanceledException)
        {
            return new(false, "Game launch was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "Could not start the game: " + exception.Message);
        }
        finally
        {
            if (collectionLease is not null)
            {
                try { if (mountingStarted) RecoverProfile(installations); }
                finally { await collectionLease.DisposeAsync(); }
            }
            if (enteredGate) _gate.Release();
        }
        NotifyChanged();
        return new(true);
    }

    private async Task MonitorAsync(ActiveSession session, CancellationToken cancellationToken)
    {
        using var checkpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var canObserveExit = true;
        var exited = false;
        Task exit;
        try { exit = session.Process.WaitForExitAsync(cancellationToken); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastWarning = "The session could not be monitored: " + exception.Message;
            canObserveExit = false;
            exit = Task.CompletedTask;
        }
        try
        {
            while (!exit.IsCompleted)
            {
                var checkpoint = Task.Delay(_checkpointInterval, checkpointCancellation.Token);
                if (await Task.WhenAny(exit, checkpoint) == exit) break;
                await checkpoint;
                await RecordAsync(session, false);
            }
            await exit;
            exited = canObserveExit;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastWarning = "Session tracking stopped: " + exception.Message;
        }
        finally
        {
            await checkpointCancellation.CancelAsync();
            try
            {
                if (exited) RecoverProfile(session.Installations);
                await RecordAsync(session, true);
            }
            finally
            {
                // Disposing tracking can cancel the wait while the game is alive. Never restore
                // a save here; the durable mod state and OS process guard survive this handle.
                try { session.Process.Dispose(); }
                finally { await session.CollectionLease.DisposeAsync(); }
            }
            NotifyChanged();
        }
    }

    private void RecoverProfile(IReadOnlyList<GameInstallation> installations)
    {
        try { _profiles?.Recover(installations); }
        catch (Exception exception) when (IsStorageException(exception) || exception is InvalidOperationException)
        {
            LastWarning = "Profile recovery is pending. Close all games and reopen the launcher. " + exception.Message;
        }
    }

    private async Task RecordAsync(ActiveSession session, bool finished)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_active, session)) return;
            session.Playtime.Totals[session.GameId] = CurrentSeconds(session);
            session.Playtime.ActiveGameId = finished ? null : session.GameId;
            session.Playtime.LastCheckpointUtc = _clock.GetUtcNow();
            if (finished) _active = null;
            try { await SavePlaytimeAsync(session.ProfileId, session.Playtime); }
            catch (Exception exception) when (IsStorageException(exception))
            {
                LastWarning = "Playtime is available for this session, but could not be saved.";
            }
        }
        finally { _gate.Release(); }
        if (!finished) NotifyChanged();
    }

    private long CurrentSeconds(ActiveSession session) => Math.Clamp(session.PreviousSeconds
        + (long)Math.Max(0, _clock.GetElapsedTime(session.StartedTimestamp).TotalSeconds), 0, MaximumSeconds);

    private string PlaytimePath(string profileId) => _profiles?.PlaytimePath(profileId) ?? _file;

    private async Task<PlaytimeData> LoadPlaytimeAsync(string profileId, bool forLaunch = false)
    {
        var file = PlaytimePath(profileId);
        try
        {
            TemporaryCollectionService.RequireOrdinary(file);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) throw new JsonException("Playtime file is too large.");
            var data = await JsonSerializer.DeserializeAsync<PlaytimeData>(stream) ?? new();
            data.Totals ??= [];
            data.Totals = data.Totals.Where(p => GameCatalog.All.Any(g => g.Id == p.Key))
                .ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, MaximumSeconds));
            // Totals already include the last durable checkpoint. Never count an unknown offline interval.
            data.ActiveGameId = null;
            return data;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException)
        {
            LastWarning = "The saved playtime could not be read.";
            if (forLaunch) File.Move(file, file + ".corrupt-" + Guid.NewGuid().ToString("N"));
            return new();
        }
        catch (Exception error) when (!forLaunch && IsStorageException(error))
        {
            LastWarning = "The saved playtime is temporarily unavailable.";
            return new();
        }
    }

    private async Task SavePlaytimeAsync(string profileId, PlaytimeData data)
    {
        var file = PlaytimePath(profileId);
        TemporaryCollectionService.RequireOrdinary(file, mayNotExist: true);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, data, new JsonSerializerOptions { WriteIndented = true });
                await output.FlushAsync();
            }
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsStorageException(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;
    private void NotifyChanged()
    {
        foreach (EventHandler handler in SessionsChanged?.GetInvocationList() ?? [])
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalLock)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        // Signal before waiting for the launch gate: a pending preparer holds that gate
        // and must receive cancellation in order to release it. One shared disposal task
        // owns the CTS lifetime, so concurrent disposal cannot race Cancel against Dispose.
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task? monitor;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            monitor = _monitor;
        }
        finally { _gate.Release(); }
        if (monitor is not null) await monitor.ConfigureAwait(false);
        _shutdown.Dispose();
        // The semaphore may still have an already-entered reader or a second disposal waiting.
        // It owns no OS handle unless AvailableWaitHandle is requested, which this class never does.
    }
}
