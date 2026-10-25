using System.Buffers.Binary;

namespace PowerOfChaos.Launcher.Core.Discovery;

/// <summary>Finds installations by inspecting file headers. Never starts a game.</summary>
public sealed class GameDiscovery
{
    private const int MaximumEntriesPerDirectory = 20000;
    private const int MaximumWarnings = 24;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Task<DiscoveryResult> ScanAsync(string root, DiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DiscoveryOptions();
        if (options.MaxDepth is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options), "Search depth must be between 0 and 32.");
        if (options.MaxDirectories is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(options), "Directory limit must be between 1 and 100000.");
        return Task.Run(() => Scan(root, options, cancellationToken), cancellationToken);
    }

    private static DiscoveryResult Scan(string root, DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var games = new List<GameInstallation>();
        var warnings = new List<string>();
        var seenDirectories = new HashSet<string>(PathComparer);
        var seenExecutables = new HashSet<string>(PathComparer);
        var pending = new Queue<(string Path, int Depth)>();
        var visited = 0;
        var skipped = 0;
        var limitReached = false;
        cancellationToken.ThrowIfCancellationRequested();

        void Warn(string message)
        {
            if (warnings.Count < MaximumWarnings) warnings.Add(message);
        }

        DiscoveryResult Result() => new(games.OrderBy(g => GameCatalog.Get(g.GameId).Order)
            .ThenBy(g => g.ExecutablePath, StringComparer.Ordinal).ToArray(), visited, skipped,
            limitReached, warnings.ToArray());

        string selectedRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                Warn("Choose a game installation folder.");
                return Result();
            }
            selectedRoot = Path.GetFullPath(root.Trim().Trim('"'));
            if (File.Exists(selectedRoot) && GameCatalog.All.Any(g =>
                    string.Equals(Path.GetFileName(selectedRoot), g.ExecutableName, StringComparison.OrdinalIgnoreCase)))
                selectedRoot = Path.GetDirectoryName(selectedRoot)!;
            selectedRoot = Path.TrimEndingDirectorySeparator(selectedRoot);
            if (!Directory.Exists(selectedRoot))
            {
                Warn("The selected folder does not exist or cannot be accessed.");
                return Result();
            }
            if ((File.GetAttributes(selectedRoot) & FileAttributes.ReparsePoint) != 0)
            {
                Warn("Choose the actual installation folder rather than a symbolic link.");
                return Result();
            }
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            Warn("The selected path is not an accessible folder.");
            return Result();
        }

        seenDirectories.Add(selectedRoot);
        pending.Enqueue((selectedRoot, 0));
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited++;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var children = new List<string>();
            var ambiguous = false;
            var enumerated = 0;
            try
            {
                foreach (var entry in new DirectoryInfo(current.Path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++enumerated > MaximumEntriesPerDirectory)
                    {
                        limitReached = true;
                        Warn($"Search was limited in {current.Path}. Choose a more specific game folder.");
                        break;
                    }
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        {
                            skipped++;
                            continue;
                        }
                        if (current.Depth < options.MaxDepth) children.Add(entry.FullName);
                    }
                    else if (IsRelevantFilename(entry.Name) && !files.TryAdd(entry.Name, entry.FullName))
                    {
                        ambiguous = true;
                    }
                }
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                skipped++;
                Warn($"Could not completely inspect {current.Path}.");
            }

            if (ambiguous)
                Warn($"Ambiguous game filenames in {current.Path}; files differ only by letter case.");
            else
            {
                foreach (var game in GameCatalog.All)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!files.TryGetValue(game.ExecutableName, out var executable)) continue;
                    if (!files.TryGetValue("data.dat", out var data) || !files.TryGetValue("voice.dat", out var voice))
                    {
                        Warn($"{game.Title}: installation is incomplete; data.dat and Voice.dat are required beside the executable.");
                        continue;
                    }
                    try
                    {
                        if (!IsGameExecutable(executable) || !IsGameArchive(data) || !IsGameArchive(voice))
                        {
                            Warn($"{game.Title}: the files in {current.Path} are not a supported Power of Chaos installation.");
                            continue;
                        }
                        if (seenExecutables.Add(executable))
                            games.Add(new GameInstallation(game.Id, executable, data, voice, selectedRoot));
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        Warn($"{game.Title}: some installation files could not be read.");
                    }
                }
            }

            children.Sort(StringComparer.Ordinal);
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
                if (seenDirectories.Contains(canonical)) continue;
                if (seenDirectories.Count >= options.MaxDirectories)
                {
                    limitReached = true;
                    continue;
                }
                seenDirectories.Add(canonical);
                pending.Enqueue((canonical, current.Depth + 1));
            }
        }
        if (limitReached) Warn("The search limit was reached. Choose a folder closer to the games to search the remaining locations.");
        return Result();
    }

    private static bool IsRelevantFilename(string name) =>
        string.Equals(name, "data.dat", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "voice.dat", StringComparison.OrdinalIgnoreCase) ||
        GameCatalog.All.Any(g => string.Equals(name, g.ExecutableName, StringComparison.OrdinalIgnoreCase));

    private static bool IsGameExecutable(string path)
    {
        using var input = OpenRead(path);
        if (input.Length < 64) return false;
        Span<byte> dos = stackalloc byte[64];
        input.ReadExactly(dos);
        if (dos[0] != 'M' || dos[1] != 'Z') return false;
        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3c..]);
        if (peOffset < 64 || peOffset > input.Length - 26) return false;
        input.Position = peOffset;
        Span<byte> header = stackalloc byte[26];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("PE\0\0"u8)) return false;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        var optionalMagic = BinaryPrimitives.ReadUInt16LittleEndian(header[24..]);
        return machine == 0x14c && sectionCount is > 0 and <= 96 && optionalSize >= 96 &&
               (characteristics & 0x0002) != 0 && (characteristics & 0x2000) == 0 && optionalMagic == 0x10b &&
               (long)peOffset + 24 + optionalSize + sectionCount * 40 <= input.Length;
    }

    private static bool IsGameArchive(string path)
    {
        using var input = OpenRead(path);
        if (input.Length < 12) return false;
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        return header[..8].SequenceEqual("KCEJYUGI"u8) && count is > 0 and <= 100000 &&
               12L + count * 268L <= input.Length;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    private static bool IsFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
