using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PowerOfChaos.Launcher.Core.Launching;

internal sealed record HdCardPackSnapshot(byte[] Configuration, string Sha256, IReadOnlyList<string> InputPaths);

/// <summary>Validates local pack metadata without decoding or changing the user's artwork.</summary>
internal static class HdCardPack
{
    internal const string ConfigurationFileName = "hd-visible.tsv";
    internal const int MaximumConfigBytes = 1024 * 1024;
    internal const int MaximumEntries = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<HdCardPackSnapshot> ReadAsync(string directory, string gameId,
        CancellationToken cancellationToken)
    {
        var game = gameId switch { "yugi" => 1, "kaiba" => 2, "joey" => 3,
            _ => throw new InvalidDataException("Unknown HD card game.") };
        var root = ValidatePath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing local HD pack folder in Settings.");
        var config = Path.Combine(root, gameId + "-565.tsv");
        byte[] bytes;
        try { bytes = await ReadBoundedAsync(config, 1, MaximumConfigBytes, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { throw new FileNotFoundException("The HD pack is missing " + gameId + "-565.tsv. Choose the generated pack folder in Settings."); }
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("HD pack configuration must use valid UTF-8."); }
        if (text.Any(c => char.IsControl(c) && c is not '\t' and not '\r' and not '\n')) Invalid();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var cursorVersion = lines.Count > 0 && lines[0] == "POC_HD_VISIBLE_V2";
        var firstEntry = cursorVersion ? 3 : 2;
        if (lines.Count < firstEntry + 1 || lines.Count > MaximumEntries + firstEntry
            || (lines[0] != "POC_HD_VISIBLE_V1" && !cursorVersion)
            || lines[1] != game.ToString(CultureInfo.InvariantCulture) + "\t1") Invalid();
        var keys = new HashSet<uint>();
        var inputs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { root, config };
        if (cursorVersion)
        {
            var cursors = lines[2].Split('\t');
            if (cursors.Length != 5 || cursors[0] != "CURSORS_BGRA64_V1") Invalid();
            for (var state = 1; state <= 2; state++)
            {
                var path = ValidatePath(cursors[state * 2 - 1]);
                var digest = cursors[state * 2];
                if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) Invalid();
                var bank = await ReadBoundedAsync(path, 16384, 16384, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bank)), digest, StringComparison.OrdinalIgnoreCase)) Invalid();
                inputs.Add(path);
            }
        }
        foreach (var line in lines.Skip(firstEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = line.Split('\t');
            if (fields.Length != 3 || fields[0].Length > 10
                || !fields[0].All(char.IsAsciiDigit)
                || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var key)
                || key == 0 || !keys.Add(key)) Invalid();
            var referencePath = ValidatePath(fields[1]); var masterPath = ValidatePath(fields[2]);
            inputs.Add(referencePath); inputs.Add(masterPath);
            await ReadBoundedAsync(referencePath, 116000, 116000, cancellationToken).ConfigureAwait(false);
            var master = await ReadBoundedAsync(masterPath, 33, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!master.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadUInt32BigEndian(master.AsSpan(8, 4)) != 13
                || !master.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(master.AsSpan(16, 4)) != 800
                || BinaryPrimitives.ReadUInt32BigEndian(master.AsSpan(20, 4)) != 1160
                || master[24] != 8 || master[25] != 2 || master[26] != 0 || master[27] != 0 || master[28] != 0)
                throw new InvalidDataException("HD pack masters must be 800 x 1160 RGB8 non-interlaced PNG images.");
        }
        return new HdCardPackSnapshot(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Array.AsReadOnly(inputs.ToArray()));
    }

    internal static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32000 || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("HD packs require absolute drive-rooted paths without links, devices or UNC shares.");
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny([':', '"', '<', '>', '|', '*', '?']) >= 0) InvalidPath();
            var name = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789\u00b9\u00b2\u00b3".Contains(name[3])) InvalidPath();
        }
        var full = Path.GetFullPath(path);
        OriginalAspectRatioPreparer.RefuseLinks(full);
        return full;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int minimum, int maximum, CancellationToken cancellationToken)
    {
        OriginalAspectRatioPreparer.RefuseLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length < minimum || stream.Length > maximum)
            throw new InvalidDataException("An HD pack file is missing, oversized or has the wrong size: " + Path.GetFileName(path));
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != bytes.Length) throw new InvalidDataException("The HD pack changed during validation.");
        OriginalAspectRatioPreparer.RefuseLinks(path);
        return bytes;
    }

    private static void Invalid() => throw new InvalidDataException("The HD pack configuration is malformed, empty, duplicated or for another game. Regenerate the local pack.");
    private static void InvalidPath() => throw new InvalidDataException("An HD pack path contains traversal, a device name or unsupported characters.");
}

/// <summary>Availability is checked again during Play; no components are downloaded automatically.</summary>
public static class HdCardArtworkAvailability
{
    public static async Task<string?> GetUnavailableReasonAsync(string packDirectory, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return "HD card artwork currently requires Windows.";
        try
        {
            foreach (var game in GameCatalog.All) await HdCardPack.ReadAsync(packDirectory, game.Id, cancellationToken).ConfigureAwait(false);
            var components = Path.Combine(AppContext.BaseDirectory, "Components", HdCardArtworkComponents.DirectoryName);
            await OriginalAspectRatioPreparer.VerifyComponentsAsync(components, HdCardArtworkComponents.Required, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return exception.Message; }
    }
}
