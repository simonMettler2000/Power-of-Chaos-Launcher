using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Launches user-provided tools and validates a separate Joey installation without changing its files.</summary>
public static class ExternalCardTools
{
    public static ProcessStartInfo EditorPlan(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || GameCatalog.All.Any(game => game.ExecutableName.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose the card editor’s Windows executable.");
        using var executable = ReadPe(path, isDll: false);
        var fullPath = Path.GetFullPath(path);
        return new ProcessStartInfo { FileName = fullPath, WorkingDirectory = Path.GetDirectoryName(fullPath)!, UseShellExecute = false };
    }

    public static Task<GameInstallation> InspectJoeyAsync(string folder, IReadOnlyList<GameInstallation> stockGames,
        CancellationToken cancellationToken = default) => Task.Run(() => InspectJoey(folder, stockGames, cancellationToken), cancellationToken);

    internal static GameInstallation InspectJoey(string folder, IReadOnlyList<GameInstallation> stockGames, CancellationToken token,
        SplashProfile? supportedBuild = null)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new InvalidDataException("Choose the edited Joey folder.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (stockGames.Any(game => Overlaps(root, Path.GetDirectoryName(game.ExecutablePath)!)))
            throw new InvalidOperationException("Choose a separate edited Joey folder outside the installed games.");
        var executable = Path.Combine(root, GameCatalog.Get("joey").ExecutableName);
        var data = Path.Combine(root, "data.dat");
        var voice = Path.Combine(root, "voice.dat");
        using (var file = ReadPe(executable, isDll: false, x86: true))
        {
            var build = supportedBuild ?? GameModProfiles.Profiles.First(profile => profile.GameId == "joey");
            if (file.Length != build.Length || !Convert.ToHexString(SHA256.HashData(file))
                    .Equals(build.OriginalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This Joey executable is unsupported by the Modded Joey integration.");
        }
        // The loader is supplied by the user and loaded by the game from this same folder.
        using (ReadPe(Path.Combine(root, "msacm32.dll"), isDll: true, x86: true)) { }
        TemporaryCollectionService.RequireOrdinary(data);
        TemporaryCollectionService.RequireOrdinary(voice);
        if (!File.Exists(voice)) throw new InvalidDataException("The edited Joey folder needs its original voice.dat file.");
        var original = GameArchive.Read(data, [@"bin#\card_id.bin"], 8192, token);
        var bin = Path.Combine(root, "data", "bin#");
        var idsPath = Path.Combine(bin, "card_id.bin");
        TemporaryCollectionService.RequireOrdinary(idsPath);
        using var edited = File.OpenRead(idsPath);
        var originalLength = CollectionSave.CardSlots * sizeof(ushort);
        if (original is null || original[0].Length != originalLength || edited.Length < originalLength
            || edited.Length > 4096 * sizeof(ushort) || edited.Length % sizeof(ushort) != 0)
            throw new InvalidDataException("The edited Joey card table has an unsupported size.");
        var ids = new byte[(int)edited.Length];
        edited.ReadExactly(ids);
        if (!ids.AsSpan(0, originalLength).SequenceEqual(original[0]))
            throw new InvalidDataException("Keep Joey’s original card slots in order and add custom cards after them.");
        var seen = new HashSet<ushort>();
        for (var offset = 0; offset < ids.Length; offset += sizeof(ushort))
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(ids.AsSpan(offset));
            var added = seen.Add(id);
            if (offset >= originalLength && (!added || id > 4095))
                throw new InvalidDataException("Each custom card needs an unused ID between 0 and 4095.");
        }
        var slots = ids.Length / sizeof(ushort);
        VerifyTableSize(Path.Combine(bin, "card_pack.bin"), slots * sizeof(ushort));
        VerifyTableSize(Path.Combine(bin, "card_prop.bin"), slots * sizeof(uint));
        token.ThrowIfCancellationRequested();
        return new GameInstallation("joey", executable, data, voice, root);
    }

    internal static bool IsEditedJoey(GameInstallation game, string? editedFolder) =>
        game.GameId == "joey" && editedFolder is not null && string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(editedFolder)),
            StringComparison.OrdinalIgnoreCase);

    internal static bool HasEditedJoeyLayout(GameInstallation game)
    {
        if (game.GameId != "joey") return false;
        var folder = Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath))!;
        if (!File.Exists(Path.Combine(folder, "msacm32.dll"))) return false;
        var unpacked = Path.Combine(folder, "data");
        var bin = Path.Combine(unpacked, "bin#");
        foreach (var directory in new[] { unpacked, bin })
        {
            if (!Directory.Exists(directory)) return false;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return File.Exists(Path.Combine(bin, "card_id.bin"));
    }

    private static bool Overlaps(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void VerifyTableSize(string path, int expectedLength)
    {
        TemporaryCollectionService.RequireOrdinary(path);
        using var table = File.OpenRead(path);
        if (table.Length != expectedLength)
            throw new InvalidDataException("The edited Joey card tables contain different numbers of entries.");
    }

    private static FileStream ReadPe(string path, bool isDll, bool x86 = false)
    {
        TemporaryCollectionService.RequireOrdinary(path);
        var file = File.OpenRead(path);
        try
        {
            using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
            var header = pe.PEHeaders.CoffHeader;
            if (pe.PEHeaders.PEHeader is null || !header.Characteristics.HasFlag(Characteristics.ExecutableImage)
                || header.Characteristics.HasFlag(Characteristics.Dll) != isDll || x86 && header.Machine != Machine.I386)
                throw new InvalidDataException("An executable or loader file has an unsupported Windows format.");
            file.Position = 0;
            return file;
        }
        catch { file.Dispose(); throw; }
    }
}
