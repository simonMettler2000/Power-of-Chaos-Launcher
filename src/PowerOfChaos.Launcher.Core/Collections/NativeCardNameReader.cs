using System.Collections.ObjectModel;
using System.Text;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Core.Collections;

/// <summary>Names are indexed by internal collection slot; absent names are omitted.</summary>
public sealed record NativeCardNameBatch(IReadOnlyDictionary<int, string> Names, int RequestedCount);

/// <summary>Reads bounded localized name records from an installation, without extracting or caching game data.</summary>
public sealed class NativeCardNameReader
{
    private const int SlotCount = 1115;
    private const int RecordBytes = 64;
    private const int MaximumRequestedCards = 771;

    static NativeCardNameReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Language must be a supported ISO ID. Normalize stored preferences with CardLanguages first.</summary>
    public Task<NativeCardNameBatch> LoadAsync(GameInstallation installation, IReadOnlyList<int> slots, string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(language);
        cancellationToken.ThrowIfCancellationRequested();
        var selectedLanguage = CardLanguages.All.FirstOrDefault(option => option.Id.Equals(language.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("A supported card-name language is required.", nameof(language));
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
        return Task.Run(() => Load(installation, requested, selectedLanguage, cancellationToken), cancellationToken);
    }

    private static NativeCardNameBatch Load(GameInstallation installation, int[] slots, CardLanguage language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var names = new Dictionary<int, string>(slots.Length);
        if (slots.Length == 0) return Batch();
        RequireOrdinaryFile(installation.DataPath, token);
        var resources = GameArchive.Read(installation.DataPath, [@"bin#\card_name" + language.ResourceCode + ".bin"], 128 * 1024, token);
        if (resources is null) return Batch();
        var table = resources[0];
        if (table.Length != SlotCount * RecordBytes)
            throw new InvalidDataException("Native card-name tables must contain exactly 1115 fixed-size records.");
        var lengths = new int[SlotCount];
        for (var slot = 0; slot < SlotCount; slot++)
        {
            token.ThrowIfCancellationRequested();
            var record = table.AsSpan(slot * RecordBytes, RecordBytes);
            var terminator = record.IndexOf((byte)0);
            if (terminator < 0 || record[terminator..].ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Native card names must be terminated and zero-padded within their records.");
            lengths[slot] = terminator;
        }
        var encoding = Encoding.GetEncoding(language.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var slot in slots)
        {
            token.ThrowIfCancellationRequested();
            string name;
            try { name = encoding.GetString(table.AsSpan(slot * RecordBytes, lengths[slot])); }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("A native card name is not valid in its language encoding.", exception);
            }
            if (name.Any(char.IsControl)) throw new InvalidDataException("Native card names cannot contain control characters.");
            name = name.Trim();
            if (name.Length > 0) names.Add(slot, name);
        }
        return Batch();

        NativeCardNameBatch Batch() => new(new ReadOnlyDictionary<int, string>(names), slots.Length);
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
