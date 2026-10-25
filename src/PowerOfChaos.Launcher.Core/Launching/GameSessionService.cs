using System.Diagnostics;
using System.Text.Json;

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

/// <summary>Tracks only explicit launcher sessions. It never scans or kills game processes.</summary>
public sealed class GameSessionService : IAsyncDisposable
{
    private sealed class PlaytimeData
    {
        public Dictionary<string, long> Totals { get; set; } = [];
        public string? ActiveGameId { get; set; }
        public DateTimeOffset? LastCheckpointUtc { get; set; }
    }

    private sealed record ActiveSession(string GameId, IGameProcess Process, long StartedTimestamp, long PreviousSeconds);
    private readonly string _file;
    private readonly IGameProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _checkpointInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private PlaytimeData? _data;
    private ActiveSession? _active;
    private Task? _monitor;
    private bool _disposed;
    private const long MaximumSeconds = 100L * 366 * 24 * 60 * 60;

    public GameSessionService(string? dataDirectory = null, IGameProcessRunner? runner = null,
        TimeProvider? clock = null, TimeSpan? checkpointInterval = null)
    {
        _file = Path.Combine(dataDirectory ?? LauncherPaths.DataDirectory, "playtime.json");
        _runner = runner ?? new SystemGameProcessRunner();
        _clock = clock ?? TimeProvider.System;
        _checkpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(30);
        if (_checkpointInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
    }

    public event EventHandler? SessionsChanged;
    public string? LastWarning { get; private set; }
    public bool IsRunning(string gameId) => _active?.GameId == gameId;
    public bool AnyRunning => _active is not null;

    public async Task<IReadOnlyDictionary<string, long>> GetPlaytimeSecondsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            var snapshot = new Dictionary<string, long>(_data!.Totals);
            if (_active is { } active) snapshot[active.GameId] = CurrentSeconds(active);
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task<LaunchResult> LaunchAsync(GameInstallation installation, LauncherSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) return new(false, "Close the running game before launching another.");
            var plan = LaunchPlan.Create(installation, settings);
            await EnsureLoadedAsync();
            // Check that tracking can be persisted before starting an external application.
            await SaveAsync();
            var process = _runner.Start(plan);
            _active = new ActiveSession(installation.GameId, process, _clock.GetTimestamp(),
                _data!.Totals.GetValueOrDefault(installation.GameId));
            _data.ActiveGameId = installation.GameId;
            _data.LastCheckpointUtc = _clock.GetUtcNow();
            try { await SaveAsync(); }
            catch (Exception exception) when (IsStorageException(exception))
            {
                LastWarning = "The game started, but its playtime could not be saved.";
            }
            var active = _active;
            _monitor = Task.Run(() => MonitorAsync(active, _shutdown.Token));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "Could not start the game: " + exception.Message);
        }
        finally { _gate.Release(); }
        NotifyChanged();
        return new(true);
    }

    private async Task MonitorAsync(ActiveSession session, CancellationToken cancellationToken)
    {
        using var checkpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task exit;
        try { exit = session.Process.WaitForExitAsync(cancellationToken); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastWarning = "The session could not be monitored: " + exception.Message;
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastWarning = "Session tracking stopped: " + exception.Message;
        }
        finally
        {
            await checkpointCancellation.CancelAsync();
            await RecordAsync(session, true);
            session.Process.Dispose();
        }
    }

    private async Task RecordAsync(ActiveSession session, bool finished)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_active, session)) return;
            _data!.Totals[session.GameId] = CurrentSeconds(session);
            _data.ActiveGameId = finished ? null : session.GameId;
            _data.LastCheckpointUtc = _clock.GetUtcNow();
            if (finished) _active = null;
            try { await SaveAsync(); }
            catch (Exception exception) when (IsStorageException(exception))
            {
                LastWarning = "Playtime is available for this session, but could not be saved.";
            }
        }
        finally { _gate.Release(); }
        NotifyChanged();
    }

    private long CurrentSeconds(ActiveSession session) => Math.Clamp(session.PreviousSeconds
        + (long)Math.Max(0, _clock.GetElapsedTime(session.StartedTimestamp).TotalSeconds), 0, MaximumSeconds);

    private async Task EnsureLoadedAsync()
    {
        if (_data is not null) return;
        if (!File.Exists(_file)) { _data = new(); return; }
        try
        {
            if (new FileInfo(_file).Length > 1024 * 1024) throw new JsonException("Playtime file is too large.");
            await using var stream = File.OpenRead(_file);
            _data = await JsonSerializer.DeserializeAsync<PlaytimeData>(stream) ?? new();
            _data.Totals ??= [];
            _data.Totals = _data.Totals.Where(p => GameCatalog.All.Any(g => g.Id == p.Key))
                .ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, MaximumSeconds));
            // Totals already include the last durable checkpoint. Never count an unknown offline interval.
            _data.ActiveGameId = null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LastWarning = "The saved playtime could not be read; a fresh counter is being used.";
            _data = new();
            if (exception is JsonException)
            {
                try { File.Move(_file, _file + ".corrupt-" + Guid.NewGuid().ToString("N")); }
                catch (Exception moveException) when (IsStorageException(moveException)) { }
            }
        }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, _data, new JsonSerializerOptions { WriteIndented = true });
                await output.FlushAsync();
            }
            File.Move(temporary, _file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsStorageException(Exception e) => e is IOException or UnauthorizedAccessException;
    private void NotifyChanged()
    {
        foreach (EventHandler handler in SessionsChanged?.GetInvocationList() ?? [])
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? monitor;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            monitor = _monitor;
        }
        finally { _gate.Release(); }
        await _shutdown.CancelAsync();
        if (monitor is not null) await monitor.ConfigureAwait(false);
        _shutdown.Dispose();
        // The semaphore may still have an already-entered reader or a second disposal waiting.
        // It owns no OS handle unless AvailableWaitHandle is requested, which this class never does.
    }
}
