using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Core.Artwork;

/// <summary>One opaque, native-resolution BGRA card mini from an installation.</summary>
public sealed record NativeCardImage(int Slot, int Width, int Height, byte[] BgraPixels);

/// <summary>Missing individual archive resources are omitted from Images.</summary>
public sealed record NativeCardArtworkBatch(IReadOnlyList<NativeCardImage> Images, int RequestedCount);

/// <summary>Reads native mini artwork on demand, entirely in memory and without HD assets.</summary>
public sealed class NativeCardArtworkReader
{
    private const int SlotCount = 1115;
    private const int MaximumRequestedCards = 771;

    public Task<NativeCardArtworkBatch> LoadAsync(GameInstallation installation, IReadOnlyList<int> slots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(slots);
        cancellationToken.ThrowIfCancellationRequested();
        if (slots.Count > MaximumRequestedCards) throw new ArgumentOutOfRangeException(nameof(slots));
        var requested = new int[slots.Count];
        var unique = new HashSet<int>();
        for (var index = 0; index < requested.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = slots[index];
            if (slot is < 0 or >= SlotCount || !unique.Add(slot))
                throw new ArgumentException("Native card slots must be unique and between 0 and 1114.", nameof(slots));
            requested[index] = slot;
        }
        return Task.Run(() => Load(installation, requested, cancellationToken), cancellationToken);
    }

    private static NativeCardArtworkBatch Load(GameInstallation installation, int[] slots, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (slots.Length == 0) return new([], 0);
        RequireOrdinaryFile(installation.DataPath, token);
        using var archive = GameArchive.OpenBatch(installation.DataPath, token);
        var filenames = ReadFilenames(archive.ReadRequired(@"mini\list_card.txt", 128 * 1024, token), token);
        var names = slots.Select(slot => @"mini\" + filenames[slot]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var payloads = archive.ReadSelected(names, token);
        var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < names.Length; index++)
            if (payloads[index] is { } payload) byName.Add(names[index], payload);
        var images = new List<NativeCardImage>(slots.Length);
        foreach (var slot in slots)
        {
            token.ThrowIfCancellationRequested();
            if (byName.TryGetValue(@"mini\" + filenames[slot], out var payload))
                images.Add(DecodeBitmap(slot, payload, token));
        }
        return new(images.AsReadOnly(), slots.Length);
    }

    private static string[] ReadFilenames(byte[] table, CancellationToken token)
    {
        var names = new List<string>(SlotCount);
        // Comments may contain localized names; only the filename records are interpreted as ASCII.
        using var reader = new StringReader(Encoding.Latin1.GetString(table));
        var hasSlotComment = false;
        while (reader.ReadLine() is { } raw)
        {
            token.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                var comment = line.AsSpan(2).TrimStart();
                if (!comment.IsEmpty && char.IsAsciiDigit(comment[0]))
                {
                    var colon = comment.IndexOf(':');
                    if (colon >= 0)
                    {
                        if (hasSlotComment || !int.TryParse(comment[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
                            || slot != names.Count)
                            throw new InvalidDataException("Native mini filename table has contradictory slot comments.");
                        hasSlotComment = true;
                    }
                }
                continue;
            }
            if (names.Count >= SlotCount || !SafeFilename(line))
                throw new InvalidDataException("Native mini filename table contains an invalid BMP basename.");
            names.Add(line);
            hasSlotComment = false;
        }
        if (names.Count != SlotCount || hasSlotComment)
            throw new InvalidDataException("Native mini filename table must contain exactly 1115 records.");
        return names.ToArray();
    }

    private static bool SafeFilename(string name)
    {
        if (name.Length is < 5 or > 68 || !name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var value in name.AsSpan(0, name.Length - 4))
            if (!char.IsAsciiLetterOrDigit(value) && value != '_') return false;
        return true;
    }

    private static NativeCardImage DecodeBitmap(int slot, ReadOnlySpan<byte> bitmap, CancellationToken token)
    {
        if (bitmap.Length < 54 || bitmap[0] != 'B' || bitmap[1] != 'M')
            throw new InvalidDataException("Native card mini is not a BMP image.");
        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[2..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[10..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[14..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(bitmap[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bitmap[22..]);
        if (fileSize != bitmap.Length || headerSize != 40 || pixelOffset != 54
            || width is < 49 or > 50 || height != 72
            || BinaryPrimitives.ReadUInt16LittleEndian(bitmap[26..]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bitmap[28..]) != 24
            || BinaryPrimitives.ReadUInt32LittleEndian(bitmap[30..]) != 0)
            throw new InvalidDataException("Unsupported native card mini BMP header or dimensions.");
        var stride = ((long)width * 3 + 3) & ~3L;
        var pixelBytes = stride * height;
        var declaredPixelBytes = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[34..]);
        var trailingBytes = (long)fileSize - pixelOffset - pixelBytes;
        if (trailingBytes is not (0 or 2) || (declaredPixelBytes != 0 && declaredPixelBytes != pixelBytes))
            throw new InvalidDataException("Invalid native card mini BMP pixel extent.");
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var source = bitmap.Slice((int)(pixelOffset + (height - 1 - y) * stride), width * 3);
            var target = pixels.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                source.Slice(x * 3, 3).CopyTo(target.Slice(x * 4, 3));
                target[x * 4 + 3] = 255;
            }
        }
        return new(slot, width, height, pixels);
    }

    private static void RequireOrdinaryFile(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("An absolute game archive path is required.");
        var fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The game archive must be an ordinary file.");
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked game archive paths are not supported.");
        }
    }
}
