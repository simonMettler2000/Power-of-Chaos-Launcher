using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerOfChaos.Launcher.Core.Collections;

public sealed record TemporaryCollectionState(bool Enabled, bool RecoveryRequired, string? SavePath, string Message);

/// <summary>Swaps only system.dat. Original and temporary profiles remain in a private, persistent vault.</summary>
public sealed class TemporaryCollectionService
{
    private const string OffMessage = "Temporary all-cards mod is off.";
    private const string OnMessage = "Temporary all-cards mod is active. Turning it off restores your original system.dat.";
    private const string RecoveryMessage = "Temporary collection recovery is required. Turn the mod off to restore your preserved original save.";
    private const string DamagedMessage = "Temporary collection recovery data is damaged. Check your private backups before playing.";
    private const int MaximumJournalBytes = 16384;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8, WriteIndented = true };
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly CardCollectionReader _reader;
    private readonly Func<bool> _hasRunningGames;
    private readonly Action<string>? _checkpoint;
    private readonly bool _allowTemporaryProfile;
    private readonly Action<string, string> _replaceFile;

    public TemporaryCollectionService(string? vaultDirectory = null, CardCollectionReader? reader = null,
        Func<bool>? hasRunningGames = null, Action<string>? checkpoint = null)
        : this(vaultDirectory, reader, hasRunningGames, checkpoint, null) { }

    internal TemporaryCollectionService(string? vaultDirectory, CardCollectionReader? reader,
        Func<bool>? hasRunningGames, Action<string>? checkpoint, Action<string, string>? replaceFile)
    {
        VaultDirectory = Path.GetFullPath(vaultDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerOfChaosLauncher", "temporary-collection"));
        _reader = reader ?? new CardCollectionReader();
        _hasRunningGames = hasRunningGames ?? HasRunningGames;
        _checkpoint = checkpoint;
        _allowTemporaryProfile = OperatingSystem.IsWindows() || hasRunningGames is not null;
        _replaceFile = replaceFile ?? ((source, destination) => File.Replace(source, destination,
            destinationBackupFileName: null, ignoreMetadataErrors: false));
    }

    /// <summary>Independent of the optional launcher data-directory override, so all launcher instances share one gate.</summary>
    public string VaultDirectory { get; }

    public Task<TemporaryCollectionState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Atomic journal replacement allows this read while a session holds the long-lived game lease.
        try
        {
            var loaded = ReadJournal();
            var journal = loaded.Journal;
            if (journal is null) return Task.FromResult(Off());
            if (loaded.RecoveryRequired && journal.Phase == "inactive") return Task.FromResult(Recover(journal));
            if (journal.Phase == "inactive") return Task.FromResult(Off());
            var original = ReadOriginal(journal);
            if (loaded.RecoveryRequired) return Task.FromResult(Recover(journal));
            byte[] current;
            try { current = ReadSave(journal.SavePath); }
            catch (Exception ex) when (IsStorageError(ex)) { return Task.FromResult(Recover(journal)); }
            if (!SameIdentity(original, current)) return Task.FromResult(Recover(journal));
            var hash = Hash(current);
            var active = journal.Phase == "active" || journal.Phase == "enabling" && hash == journal.ExpectedHash;
            return Task.FromResult(active ? On(journal) : Recover(journal));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return Task.FromResult(new TemporaryCollectionState(true, true, null, DamagedMessage));
        }
    }

    public async Task<TemporaryCollectionState> SetEnabledAsync(bool enabled,
        IReadOnlyList<GameInstallation> games, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = OpenGate();
        EnsureNoGame();
        var loaded = ReadJournal();
        var journal = loaded.Journal;
        if (!enabled)
        {
            if (journal is null) return Off();
            if (loaded.RecoveryRequired) PreserveMetadataEvidence();
            if (journal.Phase == "inactive")
            {
                // Inactive was written only after restoration. Repair metadata without rewinding later normal progress.
                // A conflicting valid active copy weakens that evidence: only matching original bytes can prove
                // restoration already finished. Otherwise explicit deactivation must archive and restore the save.
                if (loaded.ConflictingCopies)
                {
                    var preserved = ReadOriginal(journal);
                    var alreadyRestored = HasExpectedBytes(journal.SavePath, preserved);
                    if (!alreadyRestored)
                    {
                        Restore(journal, cancellationToken);
                        return Off();
                    }
                }
                if (loaded.RecoveryRequired) SaveJournal(journal);
                return Off();
            }
            Restore(journal, cancellationToken);
            return Off();
        }
        if (loaded.RecoveryRequired) throw new InvalidOperationException(RecoveryMessage);
        if (!_allowTemporaryProfile)
            throw new InvalidOperationException("Temporary all-cards profiles are currently supported on Windows only.");
        if (journal is not null && journal.Phase != "inactive")
        {
            _ = ReadOriginal(journal);
            if (journal.Phase != "active" && !(journal.Phase == "enabling" && Hash(ReadSave(journal.SavePath)) == journal.ExpectedHash))
                throw new InvalidOperationException(RecoveryMessage);
            var pool = await ReadPool(games, cancellationToken).ConfigureAwait(false);
            RequireBoundPool(journal, pool);
            if (journal.Phase != "active") SaveJournal(journal with { Phase = "active" });
            return On(journal);
        }
        var selected = await ReadPool(games, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoGame();
        // A writer already holding system.dat prevents this guard from being acquired. Denying new readers/writers
        // closes the normal game-open race during the transaction; deletion remains shared for atomic replacement.
        using var target = OpenTarget(selected.Path);
        var original = ReadSave(target);
        var unlocked = CollectionSave.CreateUnlocked(original, selected.Slots);
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Guid.NewGuid().ToString("N");
        var generationDirectory = Path.Combine(VaultDirectory, generation);
        EnsureDirectory(generationDirectory);
        WriteNew(Path.Combine(generationDirectory, "original.system.dat"), original);
        journal = new Journal(1, generation, "enabling", selected.Path, Hash(original), Hash(unlocked), selected.Slots);
        // Once preservation begins, finish recording the transaction and swapping its target consistently.
        // Cancellation is honored above, before creating any recovery evidence or touching the target.
        SaveJournal(journal, () => _checkpoint?.Invoke("original-preserved"));
        _checkpoint?.Invoke("enable-journal");
        EnsureNoGame();
        ReplaceSave(selected.Path, unlocked);
        _checkpoint?.Invoke("enable-replaced");
        journal = journal with { Phase = "active" };
        SaveJournal(journal);
        _checkpoint?.Invoke("active-journal");
        return On(journal);
    }

    /// <summary>Acquire before every launch and keep alive until monitoring ends. Never restores a save on disposal.</summary>
    public async Task<IAsyncDisposable> AcquireSessionLeaseAsync(IReadOnlyList<GameInstallation> games,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        cancellationToken.ThrowIfCancellationRequested();
        var gate = OpenGate();
        try
        {
            EnsureNoGame();
            var loaded = ReadJournal();
            if (loaded.RecoveryRequired) throw new InvalidOperationException(RecoveryMessage);
            var journal = loaded.Journal;
            if (journal is not null && journal.Phase != "inactive")
            {
                if (!_allowTemporaryProfile)
                    throw new InvalidOperationException("Temporary all-cards profiles are currently supported on Windows only.");
                var original = ReadOriginal(journal);
                if (journal.Phase != "active" && !(journal.Phase == "enabling" && Hash(ReadSave(journal.SavePath)) == journal.ExpectedHash))
                    throw new InvalidOperationException(RecoveryMessage);
                var selected = await ReadPool(games, cancellationToken).ConfigureAwait(false);
                RequireBoundPool(journal, selected);
                cancellationToken.ThrowIfCancellationRequested();
                using var target = OpenTarget(journal.SavePath);
                var current = ReadSave(target);
                if (!SameIdentity(original, current)) throw new InvalidOperationException(RecoveryMessage);
                var unlocked = CollectionSave.CreateUnlocked(current, journal.Slots);
                if (!current.AsSpan().SequenceEqual(unlocked))
                {
                    // Deck changes and rewards in the temporary profile are preserved before refreshing its cards.
                    Archive(journal, current, "before-launch");
                    journal = journal with { Phase = "enabling", ExpectedHash = Hash(unlocked) };
                    SaveJournal(journal);
                    _checkpoint?.Invoke("refresh-journal");
                    EnsureNoGame();
                    ReplaceSave(journal.SavePath, unlocked);
                    _checkpoint?.Invoke("refresh-replaced");
                }
                journal = journal with { Phase = "active" };
                SaveJournal(journal);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoGame();
            return new Lease(gate);
        }
        catch { gate.Dispose(); throw; }
    }

    private void Restore(Journal journal, CancellationToken token)
    {
        var original = ReadOriginal(journal);
        token.ThrowIfCancellationRequested();
        EnsureNoGame();
        using var target = File.Exists(journal.SavePath) ? OpenTarget(journal.SavePath) : null;
        // Even an invalid/interrupted game save is retained verbatim. It cannot replace the immutable original.
        if (target is not null)
        {
            if (target.Length > 1024 * 1024) throw new InvalidDataException("The temporary save is too large to preserve safely.");
            var current = new byte[(int)target.Length];
            target.ReadExactly(current);
            Archive(journal, current, "temporary");
        }
        _checkpoint?.Invoke("temporary-preserved");
        token.ThrowIfCancellationRequested();
        journal = journal with { Phase = "restoring", ExpectedHash = Hash(original) };
        SaveJournal(journal);
        _checkpoint?.Invoke("restore-journal");
        EnsureNoGame();
        ReplaceSave(journal.SavePath, original);
        _checkpoint?.Invoke("restore-replaced");
        SaveJournal(journal with { Phase = "inactive" });
        _checkpoint?.Invoke("inactive-journal");
    }

    private async Task<Pool> ReadPool(IReadOnlyList<GameInstallation> games, CancellationToken token)
    {
        var known = games.Where(g => GameCatalog.All.Any(d => d.Id == g.GameId)).DistinctBy(g => g.GameId).Take(3).ToArray();
        var snapshot = await _reader.ReadAsync(known, selectedSave: null, token).ConfigureAwait(false);
        if (known.Length == 0 || snapshot.SavePath is null || snapshot.UniqueCards is null
            || known.Any(g => snapshot.Games.Single(r => r.GameId == g.GameId) is var row
                && (row.OwnedCards is null || row.Cards.Count != CollectionSnapshot.TotalFor(g.GameId))))
            throw new InvalidOperationException("The active shared save and every added game's card pool must be readable before enabling temporary cards.");
        var path = ValidateTargetPath(snapshot.SavePath);
        var slots = snapshot.Games.SelectMany(g => g.Cards).Select(c => c.Slot).Distinct().Order().ToArray();
        if (slots.Length == 0 || slots.Length > CollectionSnapshot.MaximumUniqueCards)
            throw new InvalidDataException("The temporary card pool is unsupported.");
        return new Pool(path, slots);
    }

    private static void RequireBoundPool(Journal journal, Pool pool)
    {
        if (!Paths.Equals(journal.SavePath, pool.Path) || !journal.Slots.SequenceEqual(pool.Slots))
            throw new InvalidOperationException("The shared save or added games changed. Turn the temporary all-cards mod off before changing game locations.");
    }

    private FileStream OpenGate()
    {
        EnsureDirectory(VaultDirectory);
        var path = Path.Combine(VaultDirectory, "session.lock");
        RequireOrdinary(path, mayNotExist: true);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Another game session or temporary collection change is in progress. Close all games and try again.", ex); }
    }

    private JournalRead ReadJournal()
    {
        RequireOrdinary(VaultDirectory, mayNotExist: true);
        var primary = ReadJournalCopy("state.json");
        var recovery = ReadJournalCopy("recovery.json");
        if (primary.Journal is not null && recovery.Journal is not null && SameJournal(primary.Journal, recovery.Journal))
            return new JournalRead(primary.Journal, false, false);
        if (primary.Journal is not null && recovery.Journal is not null)
        {
            // Mixed generations or changed baseline identities cannot be ordered safely from two untrusted files.
            if (primary.Journal.Generation != recovery.Journal.Generation
                || primary.Journal.OriginalHash != recovery.Journal.OriginalHash
                || !Paths.Equals(primary.Journal.SavePath, recovery.Journal.SavePath)
                || !primary.Journal.Slots.SequenceEqual(recovery.Journal.Slots))
                throw new InvalidDataException(DamagedMessage);
            return new JournalRead(recovery.Journal, true, true);
        }
        // recovery.json is the write-ahead copy. A mismatch is never resolved by timestamps or by silently rebasing.
        if (recovery.Journal is not null) return new JournalRead(recovery.Journal, true, false);
        if (primary.Journal is not null) return new JournalRead(primary.Journal, true, false);
        if (primary.Exists || recovery.Exists || HasRetainedEvidence()) throw new InvalidDataException(DamagedMessage);
        return new JournalRead(null, false, false);
    }

    private JournalCopy ReadJournalCopy(string name)
    {
        var path = Path.Combine(VaultDirectory, name);
        try { return new JournalCopy(ReadJournalFile(path), File.Exists(path)); }
        catch (Exception ex) when (IsStorageError(ex)) { return new JournalCopy(null, true); }
    }

    private Journal? ReadJournalFile(string path)
    {
        RequireOrdinary(path, mayNotExist: true);
        if (!File.Exists(path)) return null;
        // Reads are bounded immutable snapshots of atomically replaced files. Sharing write access is compatible
        // with Windows replacement implementations; it is not a claim about the cause of any transient error.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length is <= 0 or > MaximumJournalBytes) throw new InvalidDataException(DamagedMessage);
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        var journal = JsonSerializer.Deserialize<Journal>(bytes, JsonOptions);
        if (journal is null || journal.Version != 1 || journal.Generation is not { Length: 32 }
            || !Guid.TryParseExact(journal.Generation, "N", out _) || journal.Phase is not ("enabling" or "active" or "restoring" or "inactive")
            || !IsHash(journal.OriginalHash) || !IsHash(journal.ExpectedHash) || journal.Slots is not { Length: > 0 and <= 771 }
            || journal.Slots.Any(i => i < 0 || i >= CollectionSave.CardSlots) || !journal.Slots.SequenceEqual(journal.Slots.Distinct().Order()))
            throw new InvalidDataException(DamagedMessage);
        var target = ValidateTargetPath(journal.SavePath);
        if (!Paths.Equals(target, journal.SavePath)) throw new InvalidDataException(DamagedMessage);
        RequireOrdinary(GenerationDirectory(journal), mayNotExist: true);
        return journal;
    }

    private bool HasRetainedEvidence()
    {
        if (!Directory.Exists(VaultDirectory)) return false;
        // Missing metadata must not turn a surviving original/temporary generation into a new baseline.
        var entries = Directory.EnumerateFileSystemEntries(VaultDirectory).Take(1025).ToArray();
        if (entries.Length > 1024) throw new InvalidDataException(DamagedMessage);
        foreach (var path in entries)
        {
            RequireOrdinary(path);
            if (!Paths.Equals(Path.GetFileName(path), "session.lock")) return true;
        }
        return false;
    }

    private static bool SameJournal(Journal first, Journal second) => first.Version == second.Version
        && first.Generation == second.Generation && first.Phase == second.Phase && Paths.Equals(first.SavePath, second.SavePath)
        && first.OriginalHash == second.OriginalHash && first.ExpectedHash == second.ExpectedHash && first.Slots.SequenceEqual(second.Slots);

    private byte[] ReadOriginal(Journal journal)
    {
        var original = ReadSave(Path.Combine(GenerationDirectory(journal), "original.system.dat"));
        if (Hash(original) != journal.OriginalHash) throw new InvalidDataException(DamagedMessage);
        return original;
    }

    private void SaveJournal(Journal journal, Action? beforePrimary = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        if (bytes.Length > MaximumJournalBytes) throw new InvalidDataException(DamagedMessage);
        // A durable write-ahead copy always precedes both the primary journal and a target save replacement.
        AtomicWrite(Path.Combine(VaultDirectory, "recovery.json"), bytes);
        _checkpoint?.Invoke(journal.Phase + "-recovery");
        beforePrimary?.Invoke();
        AtomicWrite(Path.Combine(VaultDirectory, "state.json"), bytes);
    }

    private void PreserveMetadataEvidence()
    {
        foreach (var name in new[] { "state.json", "recovery.json" })
        {
            var path = Path.Combine(VaultDirectory, name);
            RequireOrdinary(path, mayNotExist: true);
            if (!File.Exists(path)) continue;
            var archive = Path.Combine(VaultDirectory, $"metadata-{Path.GetFileNameWithoutExtension(name)}-{Guid.NewGuid():N}.json");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 1024 * 1024) throw new InvalidDataException(DamagedMessage);
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            WriteNew(archive, bytes);
        }
    }

    private void Archive(Journal journal, byte[] bytes, string kind)
    {
        var path = Path.Combine(GenerationDirectory(journal), $"{kind}-{Guid.NewGuid():N}.system.dat");
        WriteNew(path, bytes);
    }

    private string GenerationDirectory(Journal journal) => Path.Combine(VaultDirectory, journal.Generation);

    private static string ValidateTargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetFileName(path), "system.dat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An ordinary absolute system.dat path is required.");
        var full = Path.GetFullPath(path);
        RequireOrdinary(full, mayNotExist: true);
        return full;
    }

    private static FileStream OpenTarget(string path)
    {
        ValidateTargetPath(path);
        // File.Replace needs delete sharing; all ordinary read/write opens are denied for this short transaction.
        // An already-open bounded status snapshot is harmless, but must finish before this stricter guard opens.
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete); }
            catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) is 32 or 33 && attempt < 5)
            {
                Thread.Sleep(20 * (attempt + 1));
                ValidateTargetPath(path);
            }
        }
    }

    private static byte[] ReadSave(string path)
    {
        RequireOrdinary(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadSave(input);
    }

    private static byte[] ReadSave(FileStream input, bool validate = true)
    {
        if (input.Length != CollectionSave.EncodedLength) throw new InvalidDataException("The collection save has an unsupported length.");
        var bytes = new byte[CollectionSave.EncodedLength];
        input.ReadExactly(bytes);
        if (input.Length != bytes.Length) throw new InvalidDataException("The collection save changed during reading.");
        if (validate) _ = CollectionSave.ReadQuantities(bytes);
        return bytes;
    }

    private void ReplaceSave(string path, byte[] bytes) => AtomicWrite(path, bytes);

    private void AtomicWrite(string path, byte[] bytes)
    {
        RequireOrdinary(path, mayNotExist: true);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".poc-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNew(temporary, bytes);
            const int attempts = 6;
            for (var attempt = 0; ; attempt++)
            {
                RequireOrdinary(path, mayNotExist: true);
                try
                {
                    if (File.Exists(path)) _replaceFile(temporary, path);
                    else File.Move(temporary, path);
                    break;
                }
                catch (IOException ex) when (IsTransientReplaceError(ex))
                {
                    // Windows can report a replacement error after changing directory entries. Establish the
                    // exact intended bytes before treating that ambiguous report as a completed write.
                    if (HasExpectedBytes(path, bytes)) break;
                    if (attempt + 1 >= attempts) throw;
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
        finally
        {
            // A failed write is not recovery evidence; durable originals, archives and journals are never deleted here.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsTransientReplaceError(IOException error) => OperatingSystem.IsWindows()
        && (error.HResult & 0xFFFF) is 32 or 33 or 1175;

    private static bool HasExpectedBytes(string path, byte[] expected)
    {
        try
        {
            RequireOrdinary(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length != expected.Length) return false;
            var bytes = new byte[expected.Length];
            input.ReadExactly(bytes);
            return bytes.AsSpan().SequenceEqual(expected);
        }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        RequireOrdinary(path, mayNotExist: true);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    private static void EnsureDirectory(string path)
    {
        RequireOrdinary(path, mayNotExist: true);
        Directory.CreateDirectory(path);
        RequireOrdinary(path);
    }

    private static void RequireOrdinary(string path, bool mayNotExist = false)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked temporary collection paths are not supported.");
            }
            catch (FileNotFoundException) when (mayNotExist) { }
            catch (DirectoryNotFoundException) when (mayNotExist) { }
        }
    }

    private void EnsureNoGame()
    {
        if (_hasRunningGames()) throw new InvalidOperationException("Close all three games before changing or launching a temporary collection profile.");
    }

    private static bool HasRunningGames()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            foreach (var game in GameCatalog.All)
            {
                var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExecutableName));
                try { if (processes.Length > 0) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            throw new InvalidOperationException("Running games could not be checked. Close all games and try again.", ex);
        }
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException or SecurityException;
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c));
    // Canonical encoding maps the unchanged eight identity bytes to the same first nine encoded bytes.
    private static bool SameIdentity(byte[] original, byte[] current) => original.AsSpan(4, 9).SequenceEqual(current.AsSpan(4, 9));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static TemporaryCollectionState Off() => new(false, false, null, OffMessage);
    private static TemporaryCollectionState On(Journal journal) => new(true, false, journal.SavePath, OnMessage);
    private static TemporaryCollectionState Recover(Journal journal) => new(true, true, journal.SavePath, RecoveryMessage);
    private sealed record Pool(string Path, int[] Slots);
    private sealed record JournalRead(Journal? Journal, bool RecoveryRequired, bool ConflictingCopies);
    private sealed record JournalCopy(Journal? Journal, bool Exists);
    private sealed record Journal(int Version, string Generation, string Phase, string SavePath,
        string OriginalHash, string ExpectedHash, int[] Slots);
    private sealed class Lease(FileStream gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => gate.DisposeAsync();
    }
}
