using System.Security.Cryptography;
using System.Text.Json;

namespace PowerOfChaos.HdCards;

/// <summary>Read-only, lazy local pack. False from lookup means the caller must retain its original rendering.
/// No lookup substitutes a different game, side, card or art variant. This class does not decide visibility.
/// Filesystem links are refused. Pack directories must not be concurrently renamed or modified by an untrusted writer.
/// </summary>
public sealed class HdCardPack
{
    private sealed record Entry(string Path, string Hash);
    private sealed record Cached(string Path, RgbCardImage Image);
    private readonly object gate = new();
    private readonly Dictionary<CardArtKey, Entry> entries;
    private readonly Dictionary<string, LinkedListNode<Cached>> cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Cached> lru = new();
    private readonly HdCardPackOptions options;
    private readonly string assetRoot;
    private long residentBytes, hits, misses;

    private HdCardPack(string assetRoot, HdCardPackOptions options, Dictionary<CardArtKey, Entry> entries)
    {
        this.assetRoot = assetRoot;
        this.options = options;
        this.entries = entries;
        Keys = Array.AsReadOnly(entries.Keys.ToArray());
    }

    public int EntryCount => entries.Count;
    public IReadOnlyList<CardArtKey> Keys { get; }
    public HdCardCacheStats CacheStats
    {
        get { lock (gate) return new(cached.Count, residentBytes, hits, misses); }
    }

    public static bool TryOpen(string manifestPath, HdCardPackOptions? options,
        out HdCardPack? pack, out string? error)
    {
        pack = null;
        error = null;
        try
        {
            options ??= new();
            ValidateOptions(options);
            string manifest = Path.GetFullPath(manifestPath);
            string root = Path.GetFullPath(options.AssetRootDirectory ?? Path.GetDirectoryName(manifest)!);
            SafeFiles.EnsureNoLinks(root);
            if (!Directory.Exists(root)) throw new InvalidDataException("Asset root does not exist.");
            byte[] bytes = SafeFiles.ReadBounded(manifest, options.MaximumManifestBytes);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement top = document.RootElement;
            RequireProperties(top, "schema", "dimensions", "variantPolicy", "entries");
            if (Text(top, "schema") != "power-of-chaos-hd-cards-v1" ||
                Text(top, "variantPolicy") != "explicit-art-id-only")
                throw new InvalidDataException("Unsupported pack schema or variant policy.");
            JsonElement dimensions = top.GetProperty("dimensions");
            RequireProperties(dimensions, "physicalWidth", "physicalHeight", "logicalWidth", "logicalHeight");
            if (dimensions.GetProperty("physicalWidth").GetInt32() != 800 ||
                dimensions.GetProperty("physicalHeight").GetInt32() != 1160 ||
                dimensions.GetProperty("logicalWidth").GetInt32() != 200 ||
                dimensions.GetProperty("logicalHeight").GetInt32() != 290)
                throw new InvalidDataException("Pack dimensions must be 800x1160 physical and 200x290 logical.");
            JsonElement array = top.GetProperty("entries");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > options.MaximumEntries)
                throw new InvalidDataException("Invalid entry count.");
            var mapping = new Dictionary<CardArtKey, Entry>();
            var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement item in array.EnumerateArray())
            {
                RequireProperties(item, "game", "cardId", "artId", "side", "path", "sha256");
                CardGame game = Text(item, "game") switch
                {
                    "yugi" => CardGame.Yugi, "kaiba" => CardGame.Kaiba, "joey" => CardGame.Joey,
                    _ => throw new InvalidDataException("Unknown game identifier.")
                };
                CardSide side = Text(item, "side") switch
                {
                    "front" => CardSide.Front, "back" => CardSide.Back,
                    _ => throw new InvalidDataException("Unknown side identifier.")
                };
                string card = Identifier(item, "cardId"), art = Identifier(item, "artId");
                string relative = Text(item, "path");
                string path = SafeFiles.Resolve(root, relative);
                string hash = Text(item, "sha256");
                if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToLowerInvariant())
                    throw new InvalidDataException("SHA-256 must be 64 lowercase hexadecimal characters.");
                if (fileHashes.TryGetValue(path, out string? previous) && previous != hash)
                    throw new InvalidDataException("Conflicting hashes for one asset path.");
                fileHashes[path] = hash;
                if (!mapping.TryAdd(new(game, card, art, side), new(path, hash)))
                    throw new InvalidDataException("Duplicate exact card/art/side mapping.");
            }
            pack = new(root, options, mapping);
            return true;
        }
        catch (Exception ex) when (IsPackFailure(ex))
        {
            error = ex.Message;
            return false;
        }
    }

    public bool Contains(CardArtKey key) => entries.ContainsKey(key);

    public bool TryGetImage(CardArtKey key, out RgbCardImage? image)
    {
        image = null;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out Entry? entry)) { misses++; return false; }
            if (cached.TryGetValue(entry.Path, out LinkedListNode<Cached>? node))
            {
                hits++;
                lru.Remove(node);
                lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
            misses++;
            try
            {
                SafeFiles.EnsureNoLinks(assetRoot);
                byte[] bytes = SafeFiles.ReadBounded(entry.Path, options.MaximumPngBytes);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(entry.Hash)))
                    return false;
                image = RgbPngDecoder.Decode(bytes);
                if (image.ByteCount <= options.MaxCacheBytes)
                {
                    while (residentBytes + image.ByteCount > options.MaxCacheBytes)
                    {
                        LinkedListNode<Cached> last = lru.Last!;
                        cached.Remove(last.Value.Path);
                        residentBytes -= last.Value.Image.ByteCount;
                        lru.RemoveLast();
                    }
                    cached.Add(entry.Path, lru.AddFirst(new Cached(entry.Path, image)));
                    residentBytes += image.ByteCount;
                }
                return true;
            }
            catch (Exception ex) when (IsPackFailure(ex)) { image = null; return false; }
        }
    }

    private static bool IsPackFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or JsonException or OverflowException or NotSupportedException or FormatException;

    private static void ValidateOptions(HdCardPackOptions o)
    {
        if (o.MaxCacheBytes < 0 || o.MaxCacheBytes > 256L * 1024 * 1024 ||
            o.MaximumPngBytes < 64 || o.MaximumPngBytes > 64 * 1024 * 1024 ||
            o.MaximumManifestBytes < 128 || o.MaximumManifestBytes > 16 * 1024 * 1024 ||
            o.MaximumEntries < 1 || o.MaximumEntries > 50000)
            throw new ArgumentException("Pack bounds are outside supported limits.");
    }

    private static string Text(JsonElement item, string name) =>
        item.GetProperty(name).GetString() ?? throw new InvalidDataException("Null manifest string.");

    private static string Identifier(JsonElement item, string name)
    {
        string value = Text(item, name);
        if (value.Length is < 1 or > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            throw new InvalidDataException("Invalid explicit identifier.");
        return value;
    }

    private static void RequireProperties(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected manifest object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate manifest property.");
        if (found.Count != names.Length) throw new InvalidDataException("Missing manifest property.");
    }
}

internal static class SafeFiles
{
    internal static string Resolve(string root, string relative)
    {
        if (relative.Length is < 1 or > 512 || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new InvalidDataException("Asset path must be a portable relative path.");
        foreach (string component in relative.Split('/'))
        {
            if (component.Length == 0 || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') ||
                component.Any(c => c < 32 || c is ':' or '"' or '<' or '>' or '|' or '?' or '*'))
                throw new InvalidDataException("Unsafe asset path component.");
            // Windows reserves these basenames even with extensions, in every directory.
            // Trim basename spaces conservatively before checking device aliases.
            string stem = component.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            bool portDevice = stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || portDevice)
                throw new InvalidDataException("Device paths are not assets.");
        }
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset path escapes root or is not PNG.");
        EnsureNoLinks(full);
        return full;
    }

    internal static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Filesystem links are not supported in pack paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static byte[] ReadBounded(string path, int limit)
    {
        EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1 || stream.Length > limit) throw new InvalidDataException("File exceeds configured bounds.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("File changed length during read.");
        EnsureNoLinks(path);
        return bytes;
    }
}
