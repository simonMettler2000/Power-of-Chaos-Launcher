using System.Buffers.Binary;
using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;

namespace PowerOfChaos.Launcher.Tests;

internal static class PortraitChecks
{
    private const string Kaiba = @"k\bust_up\kaiba_bu\start_2#.bmp";
    private const string Joey = @"j\bust_up\start\reaction16.bmp";
    private const string Yugi = @"y\bust_up\yugi_bu\start_2#.bmp";
    private const string Eyes = @"y\bust_up\yugi_bu\kaisi_00_2.bmp";
    private const int OverlayX = 277;
    private const int OverlayY = 238;

    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        var folder = Path.Combine(fixtures, "portrait-fixtures");
        Directory.CreateDirectory(folder);
        var loader = new GamePortraitLoader();

        await run("portrait raw BMP preserves channels, padded rows, orientation, and exact green key", async () =>
        {
            var bmp = Bitmap(3, 2, false, (x, y) => (x, y) switch
            {
                (0, 0) => (0, 255, 0),
                (1, 0) => (1, 255, 0),
                (2, 0) => (0, 254, 0),
                (0, 1) => (0, 255, 1),
                _ => (25, 50, 75)
            });
            var game = WriteArchive(folder, "raw", "kaiba", [new(Kaiba, bmp)]);
            var before = await File.ReadAllBytesAsync(game.DataPath);
            var result = await loader.LoadAsync(game) ?? throw new InvalidOperationException("Raw portrait was rejected.");
            Check(result.Width == 3 && result.Height == 2, "Original BMP dimensions were changed.");
            Pixel(result, 0, 0, 0, 255, 0, 0);
            Pixel(result, 1, 0, 0, 255, 1, 255);
            Pixel(result, 2, 0, 0, 254, 0, 255);
            Pixel(result, 0, 1, 1, 255, 0, 255);
            Pixel(result, 2, 1, 75, 50, 25, 255);
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(game.DataPath)), "Portrait loading changed its archive.");
            Check(Directory.GetFiles(Path.GetDirectoryName(game.DataPath)!).Length == 1, "Portrait loading wrote an extracted file or cache.");
        });

        await run("portrait compressed LZSS literals and zero-window pairs decode a top-down BMP", async () =>
        {
            var bmp = Bitmap(1, 2, true, (_, y) => y == 0 ? (7, 8, 9) : (10, 11, 12));
            var packed = CompressWithZeroPairs(bmp);
            Check(packed.Length != bmp.Length, "Synthetic fixture must take the compressed path.");
            var game = WriteArchive(folder, "compressed", "joey", [new(Joey.ToUpperInvariant(), packed, bmp.Length)]);
            var result = await loader.LoadAsync(game) ?? throw new InvalidOperationException("Compressed portrait was rejected.");
            Pixel(result, 0, 0, 9, 8, 7, 255);
            Pixel(result, 0, 1, 12, 11, 10, 255);
        });

        await run("Joey uses exact RGB 0,255,6 transparency and preserves every near-key color", async () =>
        {
            var bmp = Bitmap(3, 2, false, (x, y) => (x, y) switch
            {
                (0, 0) => (0, 255, 6),
                (1, 0) => (0, 255, 0),
                (2, 0) => (0, 255, 5),
                (0, 1) => (0, 255, 7),
                (1, 1) => (1, 255, 6),
                _ => (0, 254, 6)
            });
            var game = WriteArchive(folder, "joey-exact-key", "joey", [new(Joey, bmp)]);
            var result = await loader.LoadAsync(game) ?? throw new InvalidOperationException("Joey's portrait was rejected.");
            Pixel(result, 0, 0, 6, 255, 0, 0);
            Pixel(result, 1, 0, 0, 255, 0, 255);
            Pixel(result, 2, 0, 5, 255, 0, 255);
            Pixel(result, 0, 1, 7, 255, 0, 255);
            Pixel(result, 1, 1, 6, 255, 1, 255);
            Pixel(result, 2, 1, 6, 254, 0, 255);
        });

        await run("portrait LZSS supports an overlapping back-reference after its ring cursor wraps", async () =>
        {
            var bmp = Bitmap(3, 1, false, (_, _) => (77, 77, 77));
            var tokens = bmp[..55].Select(value => new byte[] { value }).ToList();
            var start = (0xFEE + 54) & 4095;
            tokens.Add([(byte)start, (byte)(((start >> 4) & 0xF0) | 5)]);
            tokens.AddRange(bmp[63..].Select(value => new byte[] { value }));
            var packed = PackTokens(tokens);
            var game = WriteArchive(folder, "overlapping-pair", "kaiba", [new(Kaiba, packed, bmp.Length)]);
            var result = await loader.LoadAsync(game) ?? throw new InvalidOperationException("Overlapping LZSS pair was rejected.");
            Pixel(result, 0, 0, 77, 77, 77, 255);
            Pixel(result, 2, 0, 77, 77, 77, 255);
        });

        await run("Yugi eyes overlay replaces opaque pixels and preserves green-key base pixels", async () =>
        {
            var portrait = Bitmap(591, 560, false, (x, y) => (x, y) == (590, 559) ? (0, 255, 0) : (20, 40, 60));
            var eyes = Bitmap(144, 48, false, (x, y) => (x, y) switch
            {
                (0, 0) => (90, 80, 70),
                (143, 47) => (10, 30, 50),
                _ => (0, 255, 0)
            });
            var game = WriteArchive(folder, "yugi-overlay", "yugi", [new(Yugi, portrait), new(Eyes, eyes)]);
            var result = await loader.LoadAsync(game) ?? throw new InvalidOperationException("Composed Yugi portrait was rejected.");
            Check(result.Width == 591 && result.Height == 560, "Composition resized Yugi's canvas.");
            Pixel(result, OverlayX, OverlayY, 70, 80, 90, 255);
            Pixel(result, OverlayX + 1, OverlayY, 60, 40, 20, 255);
            Pixel(result, OverlayX + 143, OverlayY + 47, 50, 30, 10, 255);
            Pixel(result, 590, 559, 0, 255, 0, 0);
        });

        await run("portrait names are exact resource matches and never fall back to another game's art", async () =>
        {
            var bmp = Bitmap(1, 1);
            foreach (var (id, names) in new[]
            {
                ("kaiba", new[] { Joey, "prefix" + Kaiba, Kaiba.Replace('\\', '/') }),
                ("joey", new[] { Kaiba }),
                ("yugi", new[] { Yugi }),
                ("unknown", new[] { Kaiba, Joey, Yugi, Eyes })
            })
            {
                var game = WriteArchive(folder, "wrong-name-" + id, id, names.Select(name => new Entry(name, bmp)).ToArray());
                Check(await loader.LoadAsync(game) is null, "Missing exact requested resource unexpectedly produced artwork.");
            }
            var missing = new GameInstallation("kaiba", "unused", Path.Combine(folder, "absent.dat"), "unused", folder);
            Check(await loader.LoadAsync(missing) is null, "Missing archive should produce no portrait.");
        });

        await run("portrait archive rejects duplicate targets and malformed directory extents", async () =>
        {
            var bmp = Bitmap(1, 1);
            var duplicate = WriteArchive(folder, "duplicates", "kaiba", [new(Kaiba, bmp), new(Kaiba.ToUpperInvariant(), bmp)]);
            Check(await loader.LoadAsync(duplicate) is null, "Case-insensitive duplicate target escaped validation.");
            var mutations = new Action<byte[]>[]
            {
                bytes => bytes[0] = 0,
                bytes => UInt32(bytes, 8, 0),
                bytes => UInt32(bytes, 8, 100_000),
                bytes => UInt32(bytes, 8, 2),
                bytes => UInt32(bytes, 12 + 256, 0),
                bytes => UInt32(bytes, 12 + 256, uint.MaxValue),
                bytes => UInt32(bytes, 12 + 264, uint.MaxValue),
                bytes => UInt32(bytes, 12 + 260, 8_000_001),
                bytes => UInt32(bytes, 12 + 260, 0),
                bytes => Array.Fill(bytes, (byte)0x14, 12, 256),
                bytes => bytes[12] = 0x08
            };
            for (var index = 0; index < mutations.Length; index++)
            {
                var game = WriteArchive(folder, "bad-directory-" + index, "kaiba", [new(Kaiba, bmp)]);
                var bytes = await File.ReadAllBytesAsync(game.DataPath);
                mutations[index](bytes);
                await File.WriteAllBytesAsync(game.DataPath, bytes);
                Check(await loader.LoadAsync(game) is null, $"Malformed directory case {index} produced a portrait.");
            }
            var shortGame = WriteArchive(folder, "short-header", "kaiba", []);
            await File.WriteAllBytesAsync(shortGame.DataPath, Encoding.ASCII.GetBytes("KCEJ"));
            Check(await loader.LoadAsync(shortGame) is null, "Truncated archive header escaped validation.");
        });

        await run("portrait rejects truncated, overflowing, and trailing compressed streams", async () =>
        {
            var bmp = Bitmap(1, 1);
            var packed = CompressWithZeroPairs(bmp);
            var cases = new[]
            {
                new Entry(Kaiba, [1], bmp.Length),
                new Entry(Kaiba, [0, 0], bmp.Length),
                new Entry(Kaiba, [0, 0, 15], 1),
                new Entry(Kaiba, packed[..^1], bmp.Length),
                new Entry(Kaiba, packed.Concat(new byte[] { 0 }).ToArray(), bmp.Length)
            };
            for (var index = 0; index < cases.Length; index++)
            {
                var game = WriteArchive(folder, "bad-compressed-" + index, "kaiba", [cases[index]]);
                Check(await loader.LoadAsync(game) is null, $"Malformed compressed case {index} produced a portrait.");
            }
        });

        await run("portrait BMP validation rejects unsupported formats and unsafe dimensions or offsets", async () =>
        {
            var mutations = new Action<byte[]>[]
            {
                bytes => bytes[0] = 0,
                bytes => UInt32(bytes, 2, uint.MaxValue),
                bytes => UInt32(bytes, 10, 0),
                bytes => UInt32(bytes, 10, uint.MaxValue),
                bytes => UInt32(bytes, 14, uint.MaxValue),
                bytes => UInt32(bytes, 14, 12),
                bytes => UInt32(bytes, 18, 0),
                bytes => UInt32(bytes, 18, uint.MaxValue),
                bytes => UInt32(bytes, 18, 4097),
                bytes => UInt32(bytes, 22, 0x80000000),
                bytes => UInt32(bytes, 22, 0),
                bytes => UInt32(bytes, 22, 4097),
                bytes => bytes[26] = 2,
                bytes => bytes[28] = 32,
                bytes => UInt32(bytes, 30, 1),
                bytes => UInt32(bytes, 34, 1)
            };
            for (var index = 0; index < mutations.Length; index++)
            {
                var bmp = Bitmap(2, 2);
                mutations[index](bmp);
                var game = WriteArchive(folder, "bad-bmp-" + index, "kaiba", [new(Kaiba, bmp)]);
                Check(await loader.LoadAsync(game) is null, $"Unsafe BMP case {index} produced a portrait.");
            }
            var truncated = Bitmap(2, 2)[..^1];
            UInt32(truncated, 2, (uint)truncated.Length);
            var shortGame = WriteArchive(folder, "short-pixels", "kaiba", [new(Kaiba, truncated)]);
            Check(await loader.LoadAsync(shortGame) is null, "Truncated padded pixel extent escaped validation.");
        });

        await run("portrait cancellation propagates and loading returns promptly while I/O runs on a worker", async () =>
        {
            var game = WriteArchive(folder, "cancel", "kaiba", [new(Kaiba, Bitmap(1, 1))]);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ExpectCancellation(() => loader.LoadAsync(game, cancelled.Token));

            // A near-limit synthetic directory makes an uncancelled parse substantial without allocating game data.
            var entries = Enumerable.Repeat(new Entry("unrelated-resource", []), 99_998)
                .Append(new Entry(Kaiba, Bitmap(1, 1))).ToArray();
            var large = WriteArchive(folder, "async-cancel", "kaiba", entries);
            using var token = new CancellationTokenSource();
            var load = loader.LoadAsync(large, token.Token);
            Check(!load.IsCompleted, "Archive parsing blocked the caller until completion.");
            token.Cancel();
            await ExpectCancellation(() => load);
        });
    }

    private static GameInstallation WriteArchive(string root, string fixture, string gameId, Entry[] entries)
    {
        var directory = Path.Combine(root, fixture);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.dat");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("KCEJYUGI"));
        writer.Write(entries.Length);
        var offset = 12 + entries.Length * 268;
        foreach (var entry in entries)
        {
            var name = new byte[256];
            var ascii = Encoding.ASCII.GetBytes(entry.Name);
            if (ascii.Length >= name.Length) throw new InvalidOperationException("Synthetic resource name is too long.");
            ascii.CopyTo(name, 0);
            for (var index = 0; index < name.Length; index++) name[index] = (byte)((name[index] << 4) | (name[index] >> 4));
            writer.Write(name);
            writer.Write(offset);
            writer.Write(entry.Expanded ?? entry.Payload.Length);
            writer.Write(entry.Payload.Length);
            offset += entry.Payload.Length;
        }
        foreach (var entry in entries) writer.Write(entry.Payload);
        return new GameInstallation(gameId, Path.Combine(directory, "unused.exe"), path, "unused", directory);
    }

    private static byte[] Bitmap(int width, int height, bool topDown = false,
        Func<int, int, (int Red, int Green, int Blue)>? color = null)
    {
        var stride = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + stride * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        UInt32(bytes, 2, (uint)bytes.Length);
        UInt32(bytes, 10, 54);
        UInt32(bytes, 14, 40);
        UInt32(bytes, 18, (uint)width);
        UInt32(bytes, 22, unchecked((uint)(topDown ? -height : height)));
        bytes[26] = 1;
        bytes[28] = 24;
        UInt32(bytes, 34, (uint)(stride * height));
        for (var y = 0; y < height; y++)
        {
            var storedY = topDown ? y : height - y - 1;
            for (var x = 0; x < width; x++)
            {
                var pixel = color?.Invoke(x, y) ?? (12, 34, 56);
                var offset = 54 + storedY * stride + x * 3;
                bytes[offset] = (byte)pixel.Item3;
                bytes[offset + 1] = (byte)pixel.Item2;
                bytes[offset + 2] = (byte)pixel.Item1;
            }
        }
        return bytes;
    }

    private static byte[] CompressWithZeroPairs(byte[] input)
    {
        if (input.Length >= 1024) throw new InvalidOperationException("Zero-window fixture encoder accepts small input only.");
        var encoded = new List<byte>();
        var position = 0;
        while (position < input.Length)
        {
            var flagPosition = encoded.Count;
            encoded.Add(0);
            for (var bit = 0; bit < 8 && position < input.Length; bit++)
            {
                var zeroCount = 0;
                while (position + zeroCount < input.Length && input[position + zeroCount] == 0 && zeroCount < 18) zeroCount++;
                if (zeroCount >= 3)
                {
                    // Offset 0x400 remains in the untouched, initially zero part of this small fixture's window.
                    encoded.Add(0);
                    encoded.Add((byte)(0x40 | (zeroCount - 3)));
                    position += zeroCount;
                }
                else
                {
                    encoded[flagPosition] |= (byte)(1 << bit);
                    encoded.Add(input[position++]);
                }
            }
        }
        return encoded.ToArray();
    }

    private static byte[] PackTokens(List<byte[]> tokens)
    {
        var encoded = new List<byte>();
        for (var index = 0; index < tokens.Count; index += 8)
        {
            var flags = 0;
            for (var bit = 0; bit < 8 && index + bit < tokens.Count; bit++)
                if (tokens[index + bit].Length == 1) flags |= 1 << bit;
            encoded.Add((byte)flags);
            encoded.AddRange(tokens.Skip(index).Take(8).SelectMany(token => token));
        }
        return encoded.ToArray();
    }

    private static void UInt32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void Pixel(PortraitPixels pixels, int x, int y, byte blue, byte green, byte red, byte alpha)
    {
        Check(pixels.BgraPixels.AsSpan((y * pixels.Width + x) * 4, 4).SequenceEqual(new byte[] { blue, green, red, alpha }),
            $"Unexpected BGRA pixel at {x}, {y}.");
    }

    private static async Task ExpectCancellation(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected portrait cancellation to propagate.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Entry(string Name, byte[] Payload, int? Expanded = null);
}
