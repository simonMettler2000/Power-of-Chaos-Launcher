using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PowerOfChaos.HdCards;
using PowerOfChaos.HdRendering;

var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".local", "fixtures-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixtureRoot);
var pngPath = Path.Combine(fixtureRoot, "authored.png");
var rgb = new byte[800 * 1160 * 3];
for (var y = 0; y < 1160; y++)
    for (var x = 0; x < 800; x++)
    {
        var offset = (y * 800 + x) * 3;
        rgb[offset] = (byte)(x % 2 == 0 ? 0 : 255);
        rgb[offset + 1] = (byte)(y % 2 == 0 ? 0 : 255);
        rgb[offset + 2] = (byte)((x * 17 + y * 31) % 256);
    }
PngProof.Write(pngPath, 800, 1160, rgb);
var manifest = new
{
    schema = "power-of-chaos-hd-cards-v1",
    dimensions = new { physicalWidth = 800, physicalHeight = 1160, logicalWidth = 200, logicalHeight = 290 },
    variantPolicy = "explicit-art-id-only",
    entries = new[] { new { game = "yugi", cardId = "test", artId = "diagnostic", side = "front", path = "authored.png",
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pngPath))).ToLowerInvariant() } }
};
var manifestPath = Path.Combine(fixtureRoot, "pack.json");
File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
Check(HdCardPack.TryOpen(manifestPath, null, out var pack, out var error), "open synthetic pack: " + error);
var usablePack = pack ?? throw new InvalidOperationException("Pack is null");
Check(usablePack.TryGetImage(new CardArtKey(CardGame.Yugi, "test", "diagnostic", CardSide.Front), out var decoded), "decode source");
var image = decoded ?? throw new InvalidOperationException("Image is null");
Check(image.Pixels.SequenceEqual(rgb), "source RGB bytes unchanged");

var checks = 0;
var view = new HdViewport(1920, 1080);
Check(view.Left == 240 && view.Top == 0 && view.Width == 1440 && view.Height == 1080, "1080p pillarbox");
Check(!view.TryToLogical(239.999, 1, out _, out _), "left bar is outside");
Check(!view.TryToLogical(1680, 100, out _, out _), "right edge is outside");
Check(view.TryToLogical(960, 540, out var lx, out var ly) && lx == 400 && ly == 300, "inverse center");
Check(!view.TryToLogical(double.NaN, 0, out _, out _), "NaN input rejected");
var portrait = new HdViewport(1200, 1600);
Check(portrait.Left == 0 && portrait.Top == 350 && portrait.Height == 900, "portrait letterbox");

// At a genuine 4x target the original 200x290 footprint is exactly 800x1160 physical pixels.
var output = new byte[3200 * 2400 * 3];
var whole = new LogicalRect(0, 0, 800, 600);
Check(CardCompositor.Draw(output, 3200, 2400, image, new LogicalRect(300, 155, 200, 290), whole), "4x draw");
for (var y = 0; y < 1160; y++)
    for (var x = 0; x < 800; x++)
    {
        var destination = ((620 + y) * 3200 + 1200 + x) * 3;
        var source = (y * 800 + x) * 3;
        if (!output.AsSpan(destination, 3).SequenceEqual(rgb.AsSpan(source, 3)))
            throw new InvalidOperationException("4x lost source detail at " + x + "," + y);
        checks++;
    }
Check(output[((620 - 1) * 3200 + 1200) * 3] == 0 && output[((620 + 1160) * 3200 + 1200) * 3] == 0, "top/bottom footprint");
var proofPath = args.Length == 2 && args[0] == "--proof" ? Path.GetFullPath(args[1]) : null;
if (proofPath is not null) PngProof.Write(proofPath, 3200, 2400, output);

foreach (var rotation in Enum.GetValues<CardRotation>())
{
    var rotated = new byte[3200 * 2400 * 3];
    var swapped = rotation is CardRotation.Clockwise or CardRotation.Counterclockwise;
    var destination = new LogicalRect(100, 100, swapped ? 290 : 200, swapped ? 200 : 290);
    Check(CardCompositor.Draw(rotated, 3200, 2400, image, destination, whole, rotation: rotation), "rotation draws");
    var physicalWidth = swapped ? 1160 : 800;
    var physicalHeight = swapped ? 800 : 1160;
    for (var y = 0; y < physicalHeight; y += 13)
        for (var x = 0; x < physicalWidth; x += 11)
        {
            var (sx, sy) = rotation switch
            {
                CardRotation.Clockwise => (y, 1159 - x),
                CardRotation.UpsideDown => (799 - x, 1159 - y),
                CardRotation.Counterclockwise => (799 - y, x),
                _ => (x, y)
            };
            var actual = ((400 + y) * 3200 + 400 + x) * 3;
            Check(rotated.AsSpan(actual, 3).SequenceEqual(rgb.AsSpan((sy * 800 + sx) * 3, 3)), "rotation source identity");
            checks++;
        }
}

var small = Enumerable.Repeat((byte)37, 800 * 600 * 3).ToArray();
var before = small.ToArray();
Check(!CardCompositor.Draw(small, 800, 600, image, new LogicalRect(double.NaN, 0, 200, 290), whole), "invalid destination");
Check(small.AsSpan().SequenceEqual(before), "invalid draw doesn't mutate");
Check(!CardCompositor.Draw(small, 800, 600, image, new LogicalRect(-300, 0, 200, 290), whole), "outside draw");
Check(CardCompositor.Draw(small, 800, 600, image, new LogicalRect(10, 10, 200, 290), new LogicalRect(50, 50, 10, 10)), "clip draw");
for (var y = 0; y < 600; y++)
    for (var x = 0; x < 800; x++)
        if (x < 50 || x >= 60 || y < 50 || y >= 60)
            Check(small.AsSpan((y * 800 + x) * 3, 3).SequenceEqual(before.AsSpan((y * 800 + x) * 3, 3)), "outside clip retained");
Check(CardCompositor.Draw(small, 800, 600, image, new LogicalRect(10, 10, 50, 72), whole, CardFit.Contain), "mini contain");
Check(CardCompositor.Draw(small, 800, 600, image, new LogicalRect(10, 10, 50, 72), whole, CardFit.Cover), "mini cover");
Console.WriteLine(JsonSerializer.Serialize(new { success = true, exactSourcePixelsChecked = 800 * 1160,
    sampledRotationChecks = checks - 800 * 1160, source = "authored diagnostic only", proofPath,
    scope = "Actual high-resolution CPU raster, geometry and clipping; no native-game rendering claim" }));

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
}

static class PngProof
{
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> pixels)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            for (var y = 0; y < height; y++)
            {
                deflate.WriteByte(0);
                deflate.Write(pixels.Slice(y * width * 3, width * 3));
            }
        Chunk(output, "IDAT"u8, compressed.ToArray());
        Chunk(output, "IEND"u8, []);
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> bytes)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length);
        output.Write(number); output.Write(kind); output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (var value in kind) crc = Step(crc, value);
        foreach (var value in bytes) crc = Step(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xffffffff);
        output.Write(number);
    }

    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        return crc;
    }
}
