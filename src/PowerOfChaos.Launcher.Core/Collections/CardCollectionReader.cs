using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Core.Collections;

/// <summary>Reads one shared collection and the installed games' own card pools.</summary>
public sealed class CardCollectionReader
{
    private const string CommonFolder = "Yu-Gi-Oh! Power of Chaos Common";
    private readonly Func<IReadOnlyList<GameInstallation>, IEnumerable<string>> _findSaves;

    public CardCollectionReader(Func<IReadOnlyList<GameInstallation>, IEnumerable<string>>? findSaves = null)
        => _findSaves = findSaves ?? FindSaves;

    public Task<CollectionSnapshot> ReadAsync(IReadOnlyList<GameInstallation> installations,
        string? selectedSave = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installations);
        cancellationToken.ThrowIfCancellationRequested();
        var games = installations.Where(i => GameCatalog.All.Any(g => g.Id == i.GameId))
            .DistinctBy(i => i.GameId).Take(3).ToArray();
        return Task.Run(() => Read(games, selectedSave, cancellationToken), cancellationToken);
    }

    private CollectionSnapshot Read(GameInstallation[] games, string? selectedSave, CancellationToken token)
    {
        var rows = GameCatalog.All.Select(g => new GameCollectionProgress(g.Id,
            CollectionSnapshot.TotalFor(g.Id), null, games.Any(i => i.GameId == g.Id) ? "Collection unavailable" : "Game not added")).ToArray();
        CollectionSnapshot Unavailable(string message, string? path = null) => new(rows, null, message, path);
        if (games.Length == 0) return Unavailable("Add a game folder to see your collection.");
        string? save = selectedSave;
        try
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(save))
            {
                var candidates = _findSaves(games).Take(128).Where(File.Exists)
                    .Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
                if (candidates.Length != 1) return Unavailable(candidates.Length == 0
                    ? "No collection save found. Choose your system.dat to show owned cards."
                    : "More than one collection save was found. Choose the system.dat you use.");
                save = candidates[0];
            }
            RequireOrdinaryFile(save);
            var encoded = new byte[CollectionSave.EncodedLength];
            // Do not prevent the game from saving or atomically replacing its file.
            // Checksums reject a torn snapshot; the next refresh can retry it.
            using (var input = new FileStream(save, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (input.Length != CollectionSave.EncodedLength)
                    return Unavailable("This collection save format is not supported.", save);
                input.ReadExactly(encoded);
                if (input.Length != CollectionSave.EncodedLength) throw new InvalidDataException("Save changed during reading.");
            }
            var quantities = CollectionSave.ReadQuantities(encoded);
            var unique = new HashSet<ushort>();
            byte[]? sharedIds = null;
            var readable = 0;
            foreach (var game in games)
            {
                token.ThrowIfCancellationRequested();
                var row = Array.FindIndex(rows, r => r.GameId == game.GameId);
                try
                {
                    RequireOrdinaryFile(game.DataPath);
                    var resources = GameArchive.Read(game.DataPath, [@"bin#\card_pack.bin", @"bin#\card_id.bin"], 4096, token);
                    if (resources is null || resources.Any(r => r.Length != CollectionSave.CardSlots * 2))
                        throw new InvalidDataException("Unsupported card tables.");
                    if (sharedIds is not null && !sharedIds.AsSpan().SequenceEqual(resources[1]))
                        return Unavailable("The games use different card layouts. Their shared collection cannot be compared safely.", save);
                    var mask = game.GameId switch { "yugi" => 1, "kaiba" => 3, _ => 7 };
                    var pool = new HashSet<ushort>();
                    var owned = new HashSet<ushort>();
                    var cards = new List<CollectionCard>();
                    for (var index = 0; index < CollectionSave.CardSlots; index++)
                    {
                        var flags = BinaryPrimitives.ReadUInt16LittleEndian(resources[0].AsSpan(index * 2));
                        if ((flags & mask) == 0) continue;
                        var id = BinaryPrimitives.ReadUInt16LittleEndian(resources[1].AsSpan(index * 2));
                        if (id == ushort.MaxValue || !pool.Add(id)) throw new InvalidDataException("Invalid card identities.");
                        if (quantities[index] > 0) owned.Add(id);
                        var origin = (flags & 1) != 0 ? "yugi" : (flags & 2) != 0 ? "kaiba" : "joey";
                        cards.Add(new CollectionCard(index, id, quantities[index], origin));
                    }
                    if (pool.Count != rows[row].TotalCards) throw new InvalidDataException("Unsupported card pool.");
                    sharedIds = resources[1];
                    unique.UnionWith(owned);
                    rows[row] = rows[row] with { OwnedCards = owned.Count, Message = "Shared collection",
                        Cards = cards.OrderBy(card => card.CardId).ToArray() };
                    readable++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
                {
                    rows[row] = rows[row] with { Message = "Card data unavailable or unsupported" };
                }
            }
            if (readable == 0) return Unavailable("The games' card data could not be read. Check their locations.", save);
            if (unique.Count > CollectionSnapshot.MaximumUniqueCards)
                return Unavailable("The combined card pool is not supported.", save);
            return new(rows, unique.Count, readable == 3
                ? "One shared collection. Duplicate cards count once."
                : "Unique cards from the readable games. Add all three games for the complete total.", save);
        }
        catch (FileNotFoundException) { return Unavailable("The selected collection save was not found. Choose system.dat again.", save); }
        catch (DirectoryNotFoundException) { return Unavailable("The collection save folder was not found.", save); }
        catch (InvalidDataException) { return Unavailable("This save is damaged or uses an unsupported format. No cards were counted.", save); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return Unavailable("The collection save could not be read. Refresh after the game finishes saving.", save);
        }
    }

    private static void RequireOrdinaryFile(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("An absolute file path is required.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked collection paths are not supported.");
    }

    private static IEnumerable<string> FindSaves(IReadOnlyList<GameInstallation> installations)
    {
        if (OperatingSystem.IsWindows())
            return FindSaves(installations, ReadCommonDirectories(),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), VirtualizedOrOriginal);
        return FindSaves(installations, [], null, path => path);
    }

    internal static IEnumerable<string> FindSaves(IReadOnlyList<GameInstallation> installations,
        IReadOnlyList<string> configuredDirectories, string? programFiles, Func<string, string> resolvePath)
    {
        // CommonDir is the game's configured shared save, not just another guessed search location.
        // A missing configured save must not silently select an unrelated old installation's save.
        if (configuredDirectories.Count > 0)
        {
            foreach (var directory in configuredDirectories)
                yield return resolvePath(Path.Combine(directory, "system.dat"));
            yield break;
        }
        if (!string.IsNullOrEmpty(programFiles))
            yield return resolvePath(Path.Combine(programFiles, "KONAMI", CommonFolder, "system.dat"));
        foreach (var installation in installations)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.DiscoveryRoot));
            var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var current = Path.GetDirectoryName(installation.ExecutablePath);
            for (var depth = 0; current is not null && depth < 8; depth++, current = Path.GetDirectoryName(current))
            {
                if (!current.Equals(root, comparer) && !current.StartsWith(root + Path.DirectorySeparatorChar, comparer)) break;
                yield return resolvePath(Path.Combine(current, "system.dat"));
                yield return resolvePath(Path.Combine(current, CommonFolder, "system.dat"));
                if (current.Equals(root, comparer)) break;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string VirtualizedOrOriginal(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var virtualized = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VirtualStore", path[root.Length..]);
            if (File.Exists(virtualized)) return virtualized;
        }
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadCommonDirectories() =>
        ReadCommonDirectories(ReadCommonDirectoryValue, Environment.Is64BitOperatingSystem);

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<string> ReadCommonDirectories(
        Func<RegistryHive, RegistryView, string, object?> readValue, bool is64BitOperatingSystem)
    {
        var result = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var suffix in new[] { "", @"\system" })
        {
            const string gameKey = @"KONAMI\Yu-Gi-Oh! Power Of Chaos";
            var key = @"SOFTWARE\" + gameKey + suffix;
            var virtualKey = @"SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE\"
                + (is64BitOperatingSystem && view == RegistryView.Registry32 ? @"WOW6432Node\" : "") + gameKey + suffix;
            // Legacy 32-bit games see the per-user virtual value before the machine value.
            // Inspect the explicit physical VirtualStore path in the 64-bit view to avoid redirection.
            var effectiveMachineValue = readValue(RegistryHive.CurrentUser, RegistryView.Registry64, virtualKey)
                ?? readValue(RegistryHive.LocalMachine, view, key);
            AddDirectory(effectiveMachineValue);
            AddDirectory(readValue(RegistryHive.CurrentUser, view, key));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        void AddDirectory(object? value)
        {
            if (value is string directory && Path.IsPathFullyQualified(directory)) result.Add(directory);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object? ReadCommonDirectoryValue(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path, writable: false);
            return key?.GetValue("CommonDir");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
