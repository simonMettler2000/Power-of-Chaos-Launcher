using System.Buffers.Binary;

namespace PowerOfChaos.Launcher.Core.Storage;

/// <summary>Bounded, in-memory reads of named resources; never extracts files.</summary>
internal static class GameArchive
{
    private const int DirectoryEntrySize = 268;
    private const int MaximumEntryCount = 100_000;

    public static byte[][]? Read(string path, string[] names, int maximumPayloadBytes, CancellationToken cancellationToken)
    {
        if (names.Length is < 1 or > 16 || maximumPayloadBytes is < 1 or > 8_000_000)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.RandomAccess);
        var entries = ReadDirectory(archive, names, maximumPayloadBytes, cancellationToken);
        return entries is null ? null : entries.Select(entry => ReadPayload(archive, entry, cancellationToken)).ToArray();
    }

    /// <summary>Indexes one archive for a bounded list read followed by selected native mini reads.</summary>
    public static BatchReader OpenBatch(string path, CancellationToken cancellationToken)
        => new(path, cancellationToken);

    internal sealed class BatchReader : IDisposable
    {
        private const int MaximumNames = 1115;
        private const int MaximumMiniBytes = 16 * 1024;
        private const int MaximumBatchBytes = 16 * 1024 * 1024;
        private readonly FileStream _archive;
        private readonly Dictionary<string, ArchiveEntry?> _entries = new(StringComparer.OrdinalIgnoreCase);

        internal BatchReader(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _archive = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.RandomAccess);
            try
            {
                Span<byte> header = stackalloc byte[12];
                _archive.ReadExactly(header);
                if (!header[..8].SequenceEqual("KCEJYUGI"u8))
                    throw new InvalidDataException("Invalid game archive signature.");
                var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                var directoryEnd = 12L + (long)count * DirectoryEntrySize;
                if (count == 0 || count >= MaximumEntryCount || directoryEnd > _archive.Length)
                    throw new InvalidDataException("Invalid game archive directory extent.");

                Span<byte> row = stackalloc byte[DirectoryEntrySize];
                Span<char> decodedName = stackalloc char[256];
                for (var index = 0u; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _archive.ReadExactly(row);
                    var length = DecodeName(row[..256], decodedName);
                    if (length < 0) throw new InvalidDataException("Invalid game archive resource name.");
                    var offset = BinaryPrimitives.ReadUInt32LittleEndian(row[256..]);
                    var expanded = BinaryPrimitives.ReadUInt32LittleEndian(row[260..]);
                    var stored = BinaryPrimitives.ReadUInt32LittleEndian(row[264..]);
                    if (offset < directoryEnd || offset > _archive.Length || stored > _archive.Length - offset)
                        throw new InvalidDataException("Invalid game archive resource extent.");
                    var name = new string(decodedName[..length]);
                    // A duplicate is retained as an invalid target even when the two rows agree.
                    var entry = expanded <= int.MaxValue && stored <= int.MaxValue
                        ? new ArchiveEntry(offset, (int)expanded, (int)stored) : (ArchiveEntry?)null;
                    if (!_entries.TryAdd(name, entry)) _entries[name] = null;
                }
            }
            catch (EndOfStreamException exception)
            {
                _archive.Dispose();
                throw new InvalidDataException("Truncated game archive directory.", exception);
            }
            catch
            {
                _archive.Dispose();
                throw;
            }
        }

        public byte[] ReadRequired(string name, int maximumPayloadBytes, CancellationToken cancellationToken)
        {
            if (maximumPayloadBytes is < 1 or > 128 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            cancellationToken.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(name, out var entry))
                throw new InvalidDataException("The native mini filename table is missing.");
            return ReadPayload(_archive, RequireEntry(entry, maximumPayloadBytes), cancellationToken);
        }

        /// <summary>Missing names have null payloads; malformed or oversized selected targets fail the batch.</summary>
        public byte[]?[] ReadSelected(string[] names, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(names);
            if (names.Length > MaximumNames) throw new ArgumentOutOfRangeException(nameof(names));
            cancellationToken.ThrowIfCancellationRequested();
            var selected = new ArchiveEntry?[names.Length];
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = 0L;
            for (var index = 0; index < names.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(names[index]) || !unique.Add(names[index]))
                    throw new ArgumentException("Batch resource names must be nonempty and unique.", nameof(names));
                if (!_entries.TryGetValue(names[index], out var entry)) continue;
                var valid = RequireEntry(entry, MaximumMiniBytes);
                totalBytes += Math.Max(valid.Stored, valid.Expanded);
                if (totalBytes > MaximumBatchBytes)
                    throw new InvalidDataException("Native mini resources exceed the batch byte budget.");
                selected[index] = valid;
            }
            var result = new byte[]?[names.Length];
            for (var index = 0; index < selected.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selected[index] is { } entry) result[index] = ReadPayload(_archive, entry, cancellationToken);
            }
            return result;
        }

        private static ArchiveEntry RequireEntry(ArchiveEntry? entry, int maximumPayloadBytes)
        {
            if (entry is not { } value || value.Expanded <= 0 || value.Expanded > maximumPayloadBytes
                || value.Stored <= 0 || value.Stored > maximumPayloadBytes)
                throw new InvalidDataException("Duplicate, empty or oversized game archive resource.");
            return value;
        }

        public void Dispose() => _archive.Dispose();
    }

    private static ArchiveEntry[]? ReadDirectory(FileStream archive, string[] names, int maximumPayloadBytes, CancellationToken cancellationToken)
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
                if (entries[target] is not null || expanded == 0 || expanded > maximumPayloadBytes
                    || stored == 0 || stored > maximumPayloadBytes) return null;
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

    private readonly record struct ArchiveEntry(long Offset, int Expanded, int Stored);
}
