using System.Buffers.Binary;
using System.Security;

namespace PowerOfChaos.Launcher.Core.Artwork;

/// <summary>Reads the requested portraits from an installation without extracting files to disk.</summary>
public sealed class GamePortraitLoader
{
    private const int DirectoryEntrySize = 268;
    private const int MaximumEntryCount = 100_000;
    private const int MaximumPayloadBytes = 8_000_000;
    private const int MaximumDimension = 4096;
    // The requested eye frame aligns at this position on the original, unscaled Yugi canvas.
    private const int OverlayX = 277;
    private const int OverlayY = 238;

    private const string KaibaPortrait = @"k\bust_up\kaiba_bu\start_2#.bmp";
    private const string JoeyPortrait = @"j\bust_up\start\reaction16.bmp";
    private const string YugiPortrait = @"y\bust_up\yugi_bu\start_2#.bmp";
    private const string YugiEyes = @"y\bust_up\yugi_bu\kaisi_00_2.bmp";

    public Task<PortraitPixels?> LoadAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Load(installation, cancellationToken), cancellationToken);
    }

    private static PortraitPixels? Load(GameInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            var names = installation.GameId switch
            {
                "kaiba" => new[] { KaibaPortrait },
                "joey" => new[] { JoeyPortrait },
                "yugi" => new[] { YugiPortrait, YugiEyes },
                _ => []
            };
            if (names.Length == 0 || string.IsNullOrWhiteSpace(installation.DataPath)) return null;

            using var archive = new FileStream(installation.DataPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.RandomAccess);
            var entries = ReadDirectory(archive, names, cancellationToken);
            if (entries is null) return null;

            var keyBlue = installation.GameId == "joey" ? (byte)6 : (byte)0;
            var portrait = DecodeBitmap(ReadPayload(archive, entries[0], cancellationToken), keyBlue, cancellationToken);
            if (portrait is null || names.Length == 1) return portrait;
            var eyes = DecodeBitmap(ReadPayload(archive, entries[1], cancellationToken), 0, cancellationToken);
            if (eyes is null || !Overlay(portrait, eyes, cancellationToken)) return null;
            return portrait;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static ArchiveEntry[]? ReadDirectory(FileStream archive, string[] names, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[12];
        archive.ReadExactly(header);
        if (!header[..8].SequenceEqual("KCEJYUGI"u8)) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var directoryEnd = 12L + (long)count * DirectoryEntrySize;
        if (count == 0 || count >= MaximumEntryCount || directoryEnd > archive.Length) return null;

        var entries = new ArchiveEntry?[names.Length];
        Span<byte> row = stackalloc byte[DirectoryEntrySize];
        Span<char> decodedName = stackalloc char[256];
        for (var index = 0u; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            archive.ReadExactly(row);
            var nameLength = DecodeName(row[..256], decodedName);
            if (nameLength < 0) return null;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(row[256..]);
            var expanded = BinaryPrimitives.ReadUInt32LittleEndian(row[260..]);
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(row[264..]);
            if (offset < directoryEnd || offset > archive.Length || stored > archive.Length - offset) return null;

            for (var target = 0; target < names.Length; target++)
            {
                if (!MemoryExtensions.Equals(decodedName[..nameLength], names[target].AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
                if (entries[target] is not null || expanded == 0 || expanded > MaximumPayloadBytes
                    || stored == 0 || stored > MaximumPayloadBytes) return null;
                entries[target] = new ArchiveEntry(offset, (int)expanded, (int)stored);
            }
        }

        return entries.All(entry => entry is not null) ? entries.Select(entry => entry!.Value).ToArray() : null;
    }

    private static int DecodeName(ReadOnlySpan<byte> encoded, Span<char> decoded)
    {
        for (var index = 0; index < encoded.Length; index++)
        {
            var value = (byte)((encoded[index] << 4) | (encoded[index] >> 4));
            if (value == 0) return index;
            if (value > 127) return -1;
            decoded[index] = (char)value;
        }
        return -1;
    }

    private static byte[] ReadPayload(FileStream archive, ArchiveEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = new byte[entry.Stored];
        archive.Position = entry.Offset;
        for (var position = 0; position < stored.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(64 * 1024, stored.Length - position);
            archive.ReadExactly(stored.AsSpan(position, length));
            position += length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return entry.Stored == entry.Expanded ? stored : Decompress(stored, entry.Expanded, cancellationToken);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> stored, int expanded, CancellationToken cancellationToken)
    {
        var output = new byte[expanded];
        Span<byte> window = stackalloc byte[4096];
        window.Clear();
        var cursor = 0xFEE;
        var inputPosition = 0;
        var outputPosition = 0;
        var flags = 0;
        while (outputPosition < output.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            flags >>= 1;
            if ((flags & 0x100) == 0)
            {
                if (inputPosition >= stored.Length) throw new InvalidDataException("Missing LZSS flags.");
                flags = stored[inputPosition++] | 0xFF00;
            }
            if ((flags & 1) != 0)
            {
                if (inputPosition >= stored.Length) throw new InvalidDataException("Missing LZSS literal.");
                var value = stored[inputPosition++];
                output[outputPosition++] = value;
                window[cursor] = value;
                cursor = (cursor + 1) & 4095;
            }
            else
            {
                if (stored.Length - inputPosition < 2) throw new InvalidDataException("Short LZSS pair.");
                var low = stored[inputPosition++];
                var high = stored[inputPosition++];
                var start = low | ((high & 0xF0) << 4);
                var length = (high & 15) + 3;
                if (length > output.Length - outputPosition) throw new InvalidDataException("LZSS output overflow.");
                for (var index = 0; index < length; index++)
                {
                    var value = window[(start + index) & 4095];
                    output[outputPosition++] = value;
                    window[cursor] = value;
                    cursor = (cursor + 1) & 4095;
                }
            }
        }
        if (inputPosition != stored.Length) throw new InvalidDataException("Trailing LZSS input.");
        return output;
    }

    private static PortraitPixels? DecodeBitmap(ReadOnlySpan<byte> bitmap, byte keyBlue, CancellationToken cancellationToken)
    {
        if (bitmap.Length < 54 || bitmap[0] != 'B' || bitmap[1] != 'M') return null;
        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[2..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[10..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[14..]);
        if (fileSize > bitmap.Length || headerSize < 40 || headerSize > bitmap.Length - 14
            || pixelOffset < 14L + headerSize || pixelOffset > fileSize) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(bitmap[18..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bitmap[22..]);
        var height = Math.Abs((long)signedHeight);
        if (width <= 0 || width > MaximumDimension || height == 0 || height > MaximumDimension
            || (long)width * height * 4 > MaximumPayloadBytes
            || BinaryPrimitives.ReadUInt16LittleEndian(bitmap[26..]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bitmap[28..]) != 24
            || BinaryPrimitives.ReadUInt32LittleEndian(bitmap[30..]) != 0) return null;
        var stride = ((long)width * 3 + 3) & ~3L;
        var pixelBytes = stride * height;
        var declaredPixelBytes = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[34..]);
        if (pixelBytes > fileSize - pixelOffset || (declaredPixelBytes != 0 && declaredPixelBytes != pixelBytes)) return null;

        var pixels = new byte[width * (int)height * 4];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = signedHeight < 0 ? y : height - 1 - y;
            var sourceRow = bitmap.Slice((int)(pixelOffset + sourceY * stride), width * 3);
            var targetRow = pixels.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var source = x * 3;
                var target = x * 4;
                var blue = sourceRow[source];
                var green = sourceRow[source + 1];
                var red = sourceRow[source + 2];
                targetRow[target] = blue;
                targetRow[target + 1] = green;
                targetRow[target + 2] = red;
                targetRow[target + 3] = blue == keyBlue && green == 255 && red == 0 ? (byte)0 : (byte)255;
            }
        }
        return new PortraitPixels(width, (int)height, pixels);
    }

    private static bool Overlay(PortraitPixels portrait, PortraitPixels overlay, CancellationToken cancellationToken)
    {
        if ((long)OverlayX + overlay.Width > portrait.Width || (long)OverlayY + overlay.Height > portrait.Height) return false;
        for (var y = 0; y < overlay.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < overlay.Width; x++)
            {
                var source = (y * overlay.Width + x) * 4;
                if (overlay.BgraPixels[source + 3] == 0) continue;
                var target = ((y + OverlayY) * portrait.Width + x + OverlayX) * 4;
                overlay.BgraPixels.AsSpan(source, 4).CopyTo(portrait.BgraPixels.AsSpan(target, 4));
            }
        }
        return true;
    }

    private readonly record struct ArchiveEntry(long Offset, int Expanded, int Stored);
}
