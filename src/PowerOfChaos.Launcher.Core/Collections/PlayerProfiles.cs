using System.Security.Cryptography;
using System.Text.Json;

namespace PowerOfChaos.Launcher.Core.Collections;

public sealed record PlayerProfile(string Id, string Name)
{
    public bool IsBase => Id == PlayerProfiles.BaseId;
}

public sealed record PlayerProfileSelection(string ActiveId, IReadOnlyList<PlayerProfile> Profiles)
{
    public PlayerProfile Active => Profiles.Single(profile => profile.Id == ActiveId);
}

/// <summary>Owns launcher profiles and preserves the stock shared save during an alternate profile's session.</summary>
public sealed class PlayerProfiles
{
    public const int MaximumProfiles = 5;
    public const string BaseId = "player";
    public const int MaximumNameLength = 32;
    private const int MaximumMetadataBytes = 16384;
    private readonly string _dataDirectory;
    private readonly string _profileDirectory;
    private readonly string _journalDirectory;
    private readonly CardCollectionReader _collections;
    private readonly TemporaryCollectionService _shared;
    private readonly Func<GameInstallation, CancellationToken, NativeStarterCards> _readStarter;
    private string CatalogPath => Path.Combine(_profileDirectory, "profiles.json");
    private string JournalPath => Path.Combine(_journalDirectory, "session.json");

    public PlayerProfiles(TemporaryCollectionService shared, string? dataDirectory = null, CardCollectionReader? collections = null)
        : this(shared, dataDirectory, collections, NativeStarterCards.Read) { }

    internal PlayerProfiles(TemporaryCollectionService shared, string? dataDirectory, CardCollectionReader? collections,
        Func<GameInstallation, CancellationToken, NativeStarterCards> readStarter)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory ?? LauncherPaths.DataDirectory);
        _profileDirectory = Path.Combine(_dataDirectory, "profiles");
        _journalDirectory = shared.PlayerProfileJournalDirectory;
        _shared = shared;
        _collections = collections ?? new CardCollectionReader();
        _readStarter = readStarter;
    }

    public PlayerProfileSelection Read()
    {
        var state = ReadMetadataIfPresent<PlayerProfileSelection>(CatalogPath);
        if (state is null) return new(BaseId, [new(BaseId, "Player")]);
        var entries = state.Profiles;
        if (entries is null || entries.Count is < 1 or > MaximumProfiles || entries.Any(p => p is null || !ValidId(p.Id)
                || p.Name is not { Length: > 0 and <= MaximumNameLength } || p.Name != p.Name.Trim() || p.Name.Any(char.IsControl))
            || entries.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count
            || entries.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count
            || !entries.Any(p => p.Id == state.ActiveId))
            throw new InvalidDataException("The profile list could not be read.");
        return state;
    }

    public void Create(string name)
    {
        using var gate = OpenForChange();
        var state = Read();
        if (state.Profiles.Count == MaximumProfiles) throw new InvalidOperationException("You can have up to five profiles.");
        RequireTemporaryOff();
        var profile = new PlayerProfile(Guid.NewGuid().ToString("N"), ValidateName(name, state));
        WriteMetadata(CatalogPath, new PlayerProfileSelection(profile.Id, [.. state.Profiles, profile]));
    }

    public void Select(string id)
    {
        using var gate = OpenForChange();
        var state = Read();
        if (state.ActiveId == id) return;
        if (!state.Profiles.Any(p => p.Id == id)) throw new InvalidOperationException("This profile no longer exists.");
        RequireTemporaryOff();
        WriteMetadata(CatalogPath, state with { ActiveId = id });
    }

    public void Rename(string id, string name)
    {
        using var gate = OpenForChange();
        var state = Read();
        if (!state.Profiles.Any(p => p.Id == id)) throw new InvalidOperationException("This profile no longer exists.");
        var updated = ValidateName(name, state, id);
        WriteMetadata(CatalogPath, state with
        {
            Profiles = state.Profiles.Select(p => p.Id == id ? p with { Name = updated } : p).ToArray()
        });
    }

    public void Delete(string id)
    {
        using var gate = OpenForChange();
        var state = Read();
        if (!state.Profiles.Any(p => p.Id == id)) throw new InvalidOperationException("This profile no longer exists.");
        if (state.Profiles.Count == 1) throw new InvalidOperationException("Keep at least one profile.");
        RequireTemporaryOff();
        var remaining = state.Profiles.Where(p => p.Id != id).ToArray();
        var fallback = remaining.FirstOrDefault(p => p.IsBase) ?? remaining[0];
        var paths = id == BaseId ? new[] { PlaytimePath(id) } : new[] { SavePath(id), PlaytimePath(id) };
        foreach (var path in paths) TemporaryCollectionService.RequireOrdinary(path, mayNotExist: true);
        WriteMetadata(CatalogPath, new PlayerProfileSelection(state.ActiveId == id ? fallback.Id : state.ActiveId, remaining));
        foreach (var path in paths)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public Task<CollectionSnapshot> ReadCollectionAsync(string profileId, IReadOnlyList<GameInstallation> games,
        string? manualSave, CancellationToken token = default)
    {
        var mounted = ReadJournal();
        if (profileId == BaseId)
            return _collections.ReadAsync(games, mounted is not null && (manualSave is null || PathsEqual(manualSave, mounted.Target))
                ? BackupPath(mounted) : manualSave, token);
        var path = SavePath(profileId);
        if (mounted is { Phase: "playing" } && mounted.ProfileSave == path) path = mounted.Target;
        return File.Exists(path) ? _collections.ReadAsync(games, path, token) : _collections.ReadUnplayedAsync(games, token);
    }

    public Task ResetCardsAsync(string profileId, GameInstallation game, IReadOnlyList<GameInstallation> games,
        string? manualSave = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var gate = OpenForChange();
        if (!Read().Profiles.Any(p => p.Id == profileId)) throw new InvalidOperationException("This profile no longer exists.");
        RequireTemporaryOff();
        var target = _collections.ResolveActiveSave(games);
        if (profileId == BaseId && !string.IsNullOrWhiteSpace(manualSave) && !PathsEqual(Path.GetFullPath(manualSave), target))
            throw new InvalidOperationException("Choose the games' active shared save in Collection before resetting cards.");
        var starter = _readStarter(game, cancellationToken);
        var save = profileId == BaseId ? target : SavePath(profileId);
        using var previous = File.Exists(save) ? TemporaryCollectionService.OpenTarget(save) : null;
        byte[] reset;
        if (previous is null)
        {
            using var legacy = TemporaryCollectionService.OpenTarget(target);
            reset = CollectionSave.CreateFresh(TemporaryCollectionService.ReadSave(legacy), starter);
        }
        else reset = CollectionSave.ResetCards(TemporaryCollectionService.ReadSave(previous), starter);
        cancellationToken.ThrowIfCancellationRequested();
        _shared.EnsureNoGame();
        EnsureParent(save);
        _shared.AtomicWrite(save, reset);
    }, cancellationToken);

    internal string PlaytimePath(string id) => id == BaseId
        ? Path.Combine(_dataDirectory, "playtime.json") : Path.Combine(Folder(id), "playtime.json");

    /// <summary>Call after acquiring the shared game lease, before invoking the runner.</summary>
    internal void Mount(string profileId, GameInstallation game, IReadOnlyList<GameInstallation> games, CancellationToken token)
    {
        Recover(games);
        if (!Read().Profiles.Any(p => p.Id == profileId)) throw new InvalidOperationException("This profile no longer exists.");
        if (profileId == BaseId) return;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Separate game profiles are currently supported on Windows.");
        RequireTemporaryOff();
        var target = _collections.ResolveActiveSave(games);
        var save = SavePath(profileId);
        if (PathsEqual(target, save)) throw new InvalidDataException("The profile file and shared save must be separate.");
        using var live = TemporaryCollectionService.OpenTarget(target);
        var original = TemporaryCollectionService.ReadSave(live);
        byte[] selected;
        if (File.Exists(save))
        {
            using var privateSave = TemporaryCollectionService.OpenTarget(save);
            selected = TemporaryCollectionService.ReadSave(privateSave);
            if (!CollectionSave.SameProfileIdentity(original, selected))
                throw new InvalidDataException("The shared save identity changed after this profile was created.");
        }
        else selected = CollectionSave.CreateFresh(original, _readStarter(game, token));
        token.ThrowIfCancellationRequested();
        _shared.EnsureNoGame();
        var journal = new MountJournal(Guid.NewGuid().ToString("N"), "mounting", target, save, Hash(original), Hash(selected));
        var backup = BackupPath(journal);
        EnsureParent(backup);
        _shared.AtomicWrite(backup, original);
        WriteMetadata(JournalPath, journal);
        _shared.AtomicWrite(target, selected);
        WriteMetadata(JournalPath, journal with { Phase = "playing" });
    }

    public void RecoverIfNeeded(IReadOnlyList<GameInstallation> games)
    {
        if (ReadJournal() is null) return;
        using var gate = _shared.OpenGate();
        Recover(games);
    }

    /// <summary>Call with the shared lease held, after an observed exit or on a failed preparation.</summary>
    internal void Recover(IReadOnlyList<GameInstallation> games)
    {
        var journal = ReadJournal();
        if (journal is null) return;
        _shared.EnsureNoGame();
        if (!_collections.IsRecoveryTarget(games, journal.Target))
            throw new InvalidOperationException("The active shared save changed while a profile was in use.");
        using var backup = TemporaryCollectionService.OpenTarget(BackupPath(journal));
        var original = TemporaryCollectionService.ReadSave(backup);
        if (Hash(original) != journal.OriginalHash) throw new InvalidDataException("The original save backup could not be verified.");
        using var live = OpenRecoveryTarget(journal.Target);
        if (live?.Length > 1024 * 1024) throw new InvalidDataException("The active profile save could not be preserved.");
        byte[]? current = null;
        if (live is not null)
        {
            current = new byte[(int)live.Length];
            live.ReadExactly(current);
        }
        if (journal.Phase == "mounting" && current is not null && Hash(current) != journal.InitialHash && Hash(current) != journal.OriginalHash)
            throw new InvalidDataException("The active profile save changed during recovery.");
        if (journal.Phase == "playing")
        {
            if (current is not null) PreserveProfile(journal, original, current);
            WriteMetadata(JournalPath, journal with { Phase = "restoring" });
        }
        _shared.EnsureNoGame();
        if (current is null || !current.AsSpan().SequenceEqual(original)) _shared.AtomicWrite(journal.Target, original);
        File.Delete(JournalPath);
    }

    private static FileStream? OpenRecoveryTarget(string path)
    {
        try { return TemporaryCollectionService.OpenTarget(path); }
        catch (FileNotFoundException) { return null; }
    }

    private void PreserveProfile(MountJournal journal, byte[] original, byte[] current)
    {
        var captured = journal.ProfileSave;
        try
        {
            if (!CollectionSave.SameProfileIdentity(original, current))
                throw new InvalidDataException("The profile's save identity changed.");
        }
        catch (InvalidDataException)
        {
            // Keep the last usable private save and preserve these bytes for recovery.
            captured = Path.Combine(Path.GetDirectoryName(captured)!, "unreadable-" + journal.Generation + ".dat");
        }
        EnsureParent(captured);
        _shared.AtomicWrite(captured, current);
    }

    private MountJournal? ReadJournal()
    {
        var journal = ReadMetadataIfPresent<MountJournal>(JournalPath);
        if (journal is null) return null;
        if (!Guid.TryParseExact(journal.Generation, "N", out _) || journal.Phase is not ("mounting" or "playing" or "restoring")
            || !ValidProfileSave(journal.ProfileSave) || !IsHash(journal.OriginalHash) || !IsHash(journal.InitialHash)
            || !PathsEqual(journal.Target, TemporaryCollectionService.ValidateTargetPath(journal.Target))
            || PathsEqual(journal.Target, journal.ProfileSave))
            throw new InvalidDataException("The profile recovery data could not be verified.");
        return journal;
    }

    private bool ValidProfileSave(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path)) return false;
        var id = Path.GetFileName(Path.GetDirectoryName(path));
        return id is not null && id != BaseId && ValidId(id) && PathsEqual(path, SavePath(id));
    }

    private FileStream OpenForChange()
    {
        var gate = _shared.OpenGate();
        try
        {
            _shared.EnsureNoGame();
            if (ReadJournal() is not null) throw new InvalidOperationException("Finish profile recovery before changing profiles.");
            return gate;
        }
        catch { gate.Dispose(); throw; }
    }

    private void RequireTemporaryOff()
    {
        var state = _shared.ReadStateAsync().GetAwaiter().GetResult();
        if (state.Enabled || state.RecoveryRequired)
            throw new InvalidOperationException("Turn off Temporary all cards before changing profiles or resetting cards.");
    }

    private static T? ReadMetadataIfPresent<T>(string path) where T : class
    {
        try
        {
            TemporaryCollectionService.RequireOrdinary(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumMetadataBytes) throw new InvalidDataException("Invalid profile metadata.");
            return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Invalid profile metadata.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw new InvalidDataException("Invalid profile metadata.", error); }
    }

    private void WriteMetadata<T>(string path, T value)
    {
        EnsureParent(path);
        _shared.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private static void EnsureParent(string path)
    {
        TemporaryCollectionService.RequireOrdinary(path, mayNotExist: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TemporaryCollectionService.RequireOrdinary(path, mayNotExist: true);
    }

    private string Folder(string id) => ValidId(id)
        ? Path.Combine(_profileDirectory, id) : throw new ArgumentException("Invalid profile identifier.");
    private string SavePath(string id) => Path.Combine(Folder(id), "system.dat");
    private string BackupPath(MountJournal journal) => Path.Combine(_journalDirectory, journal.Generation, "system.dat");
    private static bool ValidId(string id) => id == BaseId || Guid.TryParseExact(id, "N", out _);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool PathsEqual(string? first, string? second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string ValidateName(string name, PlayerProfileSelection state, string? except = null)
    {
        var normalized = name.Trim();
        if (normalized.Length is 0 or > MaximumNameLength || normalized.Any(char.IsControl))
            throw new ArgumentException("Enter a profile name between 1 and 32 characters.");
        if (state.Profiles.Any(p => p.Id != except && p.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A profile with that name already exists.");
        return normalized;
    }

    private sealed record MountJournal(string Generation, string Phase, string Target, string ProfileSave, string OriginalHash, string InitialHash);
}
