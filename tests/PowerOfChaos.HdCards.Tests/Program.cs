using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerOfChaos.HdCards;

// All image pixels and files below are newly generated synthetic test data.
// No game process, data, provider or native renderer is used.
string scratch = args.Length == 1 ? Path.GetFullPath(args[0]) :
    Path.Combine(AppContext.BaseDirectory, ".local", "hd-cards-authored-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int checks = 0, skipped = 0;
byte[] pixels = Fixtures.Pixels();
byte[] normal = Fixtures.Png(pixels);
var key = new CardArtKey(CardGame.Yugi, "synthetic-1", "authored-a", CardSide.Front);

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
string Folder(string name)
{
    string path = Path.Combine(scratch, name);
    Directory.CreateDirectory(path);
    return path;
}
string WritePack(string root, byte[] png, string? manifest = null)
{
    File.WriteAllBytes(Path.Combine(root, "authored.png"), png);
    string path = Path.Combine(root, "pack.json");
    File.WriteAllText(path, manifest ?? Fixtures.Manifest(Fixtures.Entry(key, "authored.png", png)));
    return path;
}
HdCardPack Open(string manifest, HdCardPackOptions? options = null)
{
    Check(HdCardPack.TryOpen(manifest, options, out HdCardPack? result, out string? error), "open: " + (error ?? Path.GetFileName(Path.GetDirectoryName(manifest))));
    return result!;
}
void BadPng(string name, byte[] png)
{
    var pack = Open(WritePack(Folder(name), png));
    Check(!pack.TryGetImage(key, out var image) && image is null, name);
}
void BadManifest(string name, string json)
{
    string path = WritePack(Folder(name), normal, json);
    Check(!HdCardPack.TryOpen(path, null, out var pack, out var error) && pack is null && error is not null, name);
}

foreach (int filter in Enumerable.Range(0, 5))
{
    foreach (CompressionLevel level in new[] { CompressionLevel.Optimal, CompressionLevel.NoCompression })
    {
        byte[] png = Fixtures.Png(pixels, filter, level);
        var pack = Open(WritePack(Folder($"filter-{filter}-{level}"), png));
        Check(pack.CacheStats.ResidentImages == 0, "no preload");
        Check(pack.TryGetImage(key, out var image) && image!.Pixels.SequenceEqual(pixels), $"RGB exact filter {filter}, {level}");
        byte[] copy = new byte[pixels.Length];
        image!.CopyPixelsTo(copy);
        copy[0] ^= 255;
        Check(image.Pixels[0] == pixels[0] && image.Width == 800 && image.Height == 1160 && image.LogicalWidth == 200 && image.LogicalHeight == 290, "immutable RGB and independent dimensions");
    }
}

string cacheRoot = Folder("cache");
var key2 = key with { ArtId = "authored-b" };
var back = key with { Side = CardSide.Back };
File.WriteAllBytes(Path.Combine(cacheRoot, "second.png"), normal);
string cacheManifest = WritePack(cacheRoot, normal, Fixtures.Manifest(
    Fixtures.Entry(key, "authored.png", normal), Fixtures.Entry(key2, "second.png", normal), Fixtures.Entry(back, "authored.png", normal)));
var cache = Open(cacheManifest, new() { MaxCacheBytes = pixels.Length });
Check(cache.TryGetImage(key, out var held), "first lazy image");
Check(cache.TryGetImage(back, out var same) && ReferenceEquals(held, same), "same path shares resident image with explicit back mapping");
Check(!cache.TryGetImage(key with { Game = CardGame.Kaiba }, out _), "no cross-game fallback");
Check(!cache.TryGetImage(key with { ArtId = "missing" }, out _), "no variant fallback");
Check(!cache.TryGetImage(key2 with { Side = CardSide.Back }, out _), "no front/back fallback");
Check(cache.TryGetImage(key2, out _), "load second path");
Check(cache.CacheStats.ResidentImages == 1 && cache.CacheStats.ResidentBytes == pixels.Length && cache.CacheStats.Hits == 1, "bounded LRU resident bytes");
Check(held!.Pixels.SequenceEqual(pixels), "caller retained image survives eviction");
File.Delete(Path.Combine(cacheRoot, "authored.png"));
Check(!cache.TryGetImage(key, out _), "evicted missing asset falls back");
var uncached = Open(cacheManifest, new() { MaxCacheBytes = 0 });
Check(uncached.TryGetImage(key2, out _) && uncached.CacheStats.ResidentBytes == 0, "zero-byte cache still supports transient decode");

string missing = WritePack(Folder("missing"), normal);
File.Delete(Path.Combine(Path.GetDirectoryName(missing)!, "authored.png"));
Check(!Open(missing).TryGetImage(key, out _), "missing image fallback");
string hashRoot = Folder("hash");
string badHash = Fixtures.Manifest(Fixtures.Entry(key, "authored.png", new byte[] { 1 }));
Check(!Open(WritePack(hashRoot, normal, badHash)).TryGetImage(key, out _), "hash mismatch fallback");
string assetRoot = Folder("separate-assets"), manifestRoot = Folder("separate-manifest");
File.WriteAllBytes(Path.Combine(assetRoot, "authored.png"), normal);
string selected = Path.Combine(manifestRoot, "pack.json");
File.WriteAllText(selected, Fixtures.Manifest(Fixtures.Entry(key, "authored.png", normal)));
Check(Open(selected, new() { AssetRootDirectory = assetRoot }).TryGetImage(key, out _), "trusted separate asset root");

string json = Fixtures.Manifest(Fixtures.Entry(key, "authored.png", normal));
BadManifest("duplicate-key", Fixtures.Manifest(Fixtures.Entry(key, "authored.png", normal), Fixtures.Entry(key, "authored.png", normal)));
BadManifest("duplicate-property", json.Replace("\"schema\":", "\"schema\":\"power-of-chaos-hd-cards-v1\",\"schema\":", StringComparison.Ordinal));
BadManifest("unknown-property", json.Replace("\"schema\":", "\"unexpected\":0,\"schema\":", StringComparison.Ordinal));
BadManifest("wrong-dimensions", json.Replace("\"physicalWidth\":800", "\"physicalWidth\":799", StringComparison.Ordinal));
BadManifest("wrong-schema", json.Replace("hd-cards-v1", "hd-cards-v2", StringComparison.Ordinal));
BadManifest("wrong-policy", json.Replace("explicit-art-id-only", "choose-first", StringComparison.Ordinal));
BadManifest("unknown-game", json.Replace("\"yugi\"", "\"unknown\"", StringComparison.Ordinal));
BadManifest("unknown-side", json.Replace("\"front\"", "\"face-up-guess\"", StringComparison.Ordinal));
foreach (string hostile in new[] { "../escape.png", "/absolute.png", "C:/absolute.png", "a\\b.png", "a//b.png", "a/./b.png", "NUL.png", "con.txt.png", "a./b.png", "a /b.png", "authored.png:secret", "not-png.txt" })
    BadManifest("path-" + checks, Fixtures.Manifest(Fixtures.Entry(key, hostile, normal)));

// These tests write only a normal manifest. They never create, inspect or open an
// asset at a device path, and never call TryGetImage for those manifest entries.
string deviceManifest = Path.Combine(Folder("device-name-manifests"), "pack.json");
void CheckManifestPath(string path, bool expected)
{
    File.WriteAllText(deviceManifest, Fixtures.Manifest(Fixtures.Entry(key, path, normal)));
    bool opened = HdCardPack.TryOpen(deviceManifest, null, out var pack, out var error);
    Check(opened == expected && (expected ? pack is not null && error is null : pack is null && error == "Device paths are not assets."),
        $"manifest-only device path {(expected ? "accepted neighbor" : "rejected before asset metadata")}: {path}");
}
foreach (string prefix in new[] { "COM", "LPT" })
{
    foreach (char digit in "123456789\u00b9\u00b2\u00b3")
    {
        string device = prefix + digit;
        foreach (string spelling in new[] { device, device.ToLowerInvariant() })
        {
            CheckManifestPath(spelling + ".png", false);
            CheckManifestPath(spelling + ".extra.PNG", false);
            CheckManifestPath("sub/" + spelling + ".png", false);
            CheckManifestPath(spelling + "/authored.png", false);
            CheckManifestPath(spelling + " .png", false);
        }
    }
}
foreach (string device in new[] { "CON", "PRN", "AUX", "NUL" })
{
    CheckManifestPath(device.ToLowerInvariant() + " .png", false);
    CheckManifestPath("sub/" + device + " .extra.png", false);
}
foreach (string neighbor in new[] { "COM0.png", "COM10.png", "COM\u2074.png", "COM\u00b9x.png", "xCOM\u00b9.png", "COM\u00b9-card.png", "LPT0.png", "LPT10.png", "LPT\u2074.png", "LPT\u00b3x.png", "LPT\u00b2-card.png", "CON-card.png", "AUXiliary.png", "normal name.png", "sub/card.png" })
    CheckManifestPath(neighbor, true);
BadManifest("conflicting-file-hashes", Fixtures.Manifest(Fixtures.Entry(key, "authored.png", normal), Fixtures.Entry(key2, "AUTHORED.png", new byte[] { 2 })));
Check(!HdCardPack.TryOpen(selected, new() { MaximumEntries = 0 }, out _, out _), "invalid options refused");
Check(!HdCardPack.TryOpen(selected, new() { MaximumManifestBytes = 128 }, out _, out _), "manifest byte bound");
Check(!Open(WritePack(Folder("png-byte-bound"), normal), new() { MaximumPngBytes = 64 }).TryGetImage(key, out _), "PNG file byte bound");

byte[] badCrc = (byte[])normal.Clone(); badCrc[29] ^= 1; BadPng("CRC", badCrc);
BadPng("trailing-PNG", normal.Concat(new byte[] { 1 }).ToArray());
BadPng("truncated-PNG", normal[..^1]);
BadPng("truncated-signature", normal[..7]);
BadPng("missing-IEND", normal[..^12]);
BadPng("wrong-PNG-width", Fixtures.Png(pixels, width: 799));
BadPng("interlace", Fixtures.Png(pixels, interlace: 1));
BadPng("RGBA", Fixtures.Png(pixels, colorType: 6));
BadPng("unsupported-filter", Fixtures.Png(pixels, filter: 5));
BadPng("unknown-critical", Fixtures.Png(pixels, extraType: "ABCD"));
BadPng("transparency", Fixtures.Png(pixels, extraType: "tRNS"));
BadPng("animation", Fixtures.Png(pixels, extraType: "acTL"));
BadPng("truncated-zlib", Fixtures.Png(pixels, zlibTransform: b => b[..^2]));
BadPng("trailing-zlib", Fixtures.Png(pixels, zlibTransform: b => b.Concat(new byte[] { 0, 0 }).ToArray()));
BadPng("wrong-Adler", Fixtures.Png(pixels, zlibTransform: b => { b[^1] ^= 1; return b; }));
BadPng("reserved-DEFLATE", Fixtures.Png(pixels, zlibTransform: b => { b[2] = 7; return b; }));
BadPng("extra-raw-data", Fixtures.Png(pixels, extraRaw: true));

string linkRoot = Folder("links");
string target = Path.Combine(linkRoot, "target.png"), link = Path.Combine(linkRoot, "link.png");
File.WriteAllBytes(target, normal);
bool linkCreated = false;
try { File.CreateSymbolicLink(link, target); linkCreated = true; }
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
{ skipped++; Console.WriteLine("SKIP: symlink creation unavailable: " + ex.GetType().Name); }
if (linkCreated)
{
    string linkManifest = Path.Combine(linkRoot, "pack.json");
    File.WriteAllText(linkManifest, Fixtures.Manifest(Fixtures.Entry(key, "link.png", normal)));
    Check(!HdCardPack.TryOpen(linkManifest, null, out _, out _), "file link refused");
    string directoryLink = Path.Combine(linkRoot, "dir-link");
    Directory.CreateSymbolicLink(directoryLink, assetRoot);
    File.WriteAllText(linkManifest, Fixtures.Manifest(Fixtures.Entry(key, "dir-link/authored.png", normal)));
    Check(!HdCardPack.TryOpen(linkManifest, null, out _, out _), "directory link refused");
}
Console.WriteLine($"COMPLETE: {checks} checks, {skipped} explicit skips. Authored CPU fixtures only. Scratch: {scratch}");

static class Fixtures
{
    internal static byte[] Pixels()
    {
        byte[] result = new byte[800 * 1160 * 3];
        for (int y = 0; y < 1160; y++) for (int x = 0; x < 800; x++)
        {
            int p = (y * 800 + x) * 3;
            result[p] = (byte)(x % 251); result[p + 1] = (byte)(y % 253); result[p + 2] = (byte)((x + y) % 255);
        }
        return result;
    }
    internal static object Entry(CardArtKey key, string path, byte[] bytes) => new
    {
        game = key.Game.ToString().ToLowerInvariant(), cardId = key.CardId, artId = key.ArtId,
        side = key.Side.ToString().ToLowerInvariant(), path, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
    };
    internal static string Manifest(params object[] entries) => JsonSerializer.Serialize(new
    {
        schema = "power-of-chaos-hd-cards-v1",
        dimensions = new { physicalWidth = 800, physicalHeight = 1160, logicalWidth = 200, logicalHeight = 290 },
        variantPolicy = "explicit-art-id-only", entries
    });
    internal static byte[] Png(byte[] pixels, int filter = 0, CompressionLevel level = CompressionLevel.Optimal,
        int width = 800, byte interlace = 0, byte colorType = 2, string? extraType = null,
        Func<byte[], byte[]>? zlibTransform = null, bool extraRaw = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1160);
        header[8] = 8; header[9] = colorType; header[12] = interlace;
        Chunk(output, "IHDR", header);
        if (extraType is not null) Chunk(output, extraType, new byte[] { 0 });
        using var raw = new MemoryStream();
        const int stride = 2400;
        for (int y = 0; y < 1160; y++)
        {
            raw.WriteByte((byte)filter);
            for (int x = 0; x < stride; x++)
            {
                int current = y * stride + x, left = x >= 3 ? pixels[current - 3] : 0;
                int up = y > 0 ? pixels[current - stride] : 0, corner = y > 0 && x >= 3 ? pixels[current - stride - 3] : 0;
                int predictor = filter switch { 1 => left, 2 => up, 3 => (left + up) / 2, 4 => Paeth(left, up, corner), _ => 0 };
                raw.WriteByte(unchecked((byte)(pixels[current] - predictor)));
            }
        }
        if (extraRaw) raw.WriteByte(1);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, level, true)) z.Write(raw.ToArray());
        byte[] zlib = compressed.ToArray();
        if (zlibTransform is not null) zlib = zlibTransform(zlib);
        // Split IDAT to exercise correct concatenation across arbitrary chunk boundaries.
        int split = zlib.Length / 2;
        Chunk(output, "IDAT", zlib[..split]); Chunk(output, "IDAT", zlib[split..]);
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, da = Math.Abs(p - a), db = Math.Abs(p - b), dc = Math.Abs(p - c);
        return da <= db && da <= dc ? a : db <= dc ? b : c;
    }
    private static void Chunk(Stream output, string type, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length); output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type); output.Write(typeBytes); output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte value in typeBytes.Concat(bytes))
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
    }
}
