using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PowerOfChaos.HdCards;
using PowerOfChaos.HdRendering;

try
{
    var options = Parse(args);
    var manifest = options.GetValueOrDefault("--manifest") ?? throw new ArgumentException("--manifest is required");
    var roots = new HdCardPackOptions { AssetRootDirectory = options.GetValueOrDefault("--assets-root") };
    if (!HdCardPack.TryOpen(manifest, roots, out var opened, out var error) || opened is null)
        throw new InvalidDataException(error ?? "The pack could not be opened.");
    var keys = opened.Keys;
    if (options.TryGetValue("--game", out var game))
    {
        var title = game switch { "yugi" => CardGame.Yugi, "kaiba" => CardGame.Kaiba, "joey" => CardGame.Joey,
            _ => throw new ArgumentException("Game must be yugi, kaiba or joey") };
        keys = keys.Where(key => key.Game == title).ToArray();
    }
    if (options.TryGetValue("--card-id", out var id)) keys = keys.Where(key => key.CardId == id).ToArray();
    if (options.TryGetValue("--art-id", out var art)) keys = keys.Where(key => key.ArtId == art).ToArray();
    if (keys.Count == 0) throw new InvalidDataException("No exact matching card/art keys.");
    var verified = 0;
    if (options.ContainsKey("--verify-all"))
        foreach (var key in keys)
        {
            if (!opened.TryGetImage(key, out var image) || image is null || image.ByteCount != 800 * 1160 * 3)
                throw new InvalidDataException("Image decode failed for " + key);
            verified++;
        }
    string? proofHash = null;
    if (options.TryGetValue("--proof", out var proofPath))
    {
        var key = keys.FirstOrDefault(key => key.Side == CardSide.Front);
        if (key == default || !opened.TryGetImage(key, out var image) || image is null)
            throw new InvalidDataException("No selected front rendition to render.");
        var output = new byte[3200 * 2400 * 3];
        if (!CardCompositor.Draw(output, 3200, 2400, image, new LogicalRect(300, 155, 200, 290), new LogicalRect(0, 0, 800, 600)))
            throw new InvalidDataException("The selected card did not render.");
        // Check every displayed source pixel, including detail finer than the old 200 by 290 image.
        for (var y = 0; y < 1160; y++)
            if (!output.AsSpan(((620 + y) * 3200 + 1200) * 3, 800 * 3).SequenceEqual(image.Pixels.Slice(y * 800 * 3, 800 * 3)))
                throw new InvalidDataException("Physical output lost source detail.");
        WritePng(Path.GetFullPath(proofPath), output);
        proofHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(proofPath))).ToLowerInvariant();
    }
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, selectedMappings = keys.Count, decodedMappings = verified,
        cache = opened.CacheStats, proofSha256 = proofHash,
        scope = "Local PNG decode and authored high-resolution CPU composition; no game hook or native-HD claim" }));
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("HD Card Probe: " + error.Message);
    return 1;
}

static Dictionary<string, string> Parse(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (key == "--verify-all") { result.Add(key, "true"); continue; }
        if (key is not ("--manifest" or "--assets-root" or "--game" or "--card-id" or "--art-id" or "--proof") || i + 1 == args.Length)
            throw new ArgumentException("Usage: --manifest FILE [--assets-root DIRECTORY] [--verify-all] [--game yugi|kaiba|joey] [--card-id ID] [--art-id ID] [--proof NEW.png]");
        result.Add(key, args[++i]);
    }
    return result;
}

static void WritePng(string path, byte[] rgb)
{
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    var header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header, 3200); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2400);
    header[8] = 8; header[9] = 2; Chunk(file, "IHDR"u8, header);
    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        for (var y = 0; y < 2400; y++) { zlib.WriteByte(0); zlib.Write(rgb.AsSpan(y * 3200 * 3, 3200 * 3)); }
    Chunk(file, "IDAT"u8, compressed.ToArray()); Chunk(file, "IEND"u8, []);
}

static void Chunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> bytes)
{
    Span<byte> number = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); output.Write(number); output.Write(kind); output.Write(bytes);
    uint crc = 0xffffffff;
    foreach (var value in kind) crc = Step(crc, value);
    foreach (var value in bytes) crc = Step(crc, value);
    BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xffffffff); output.Write(number);
}

static uint Step(uint crc, byte value)
{
    crc ^= value;
    for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
    return crc;
}
