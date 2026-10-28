using System.Buffers.Binary;
using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

// All archive, filename-table and pixel fixtures here are authored synthetic data.
internal static class NativeCardArtworkChecks
{
    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        var root = Path.Combine(fixtures, "native-card-artwork");
        Directory.CreateDirectory(root);
        var sequence = 0;
        string Archive(params Entry[] entries)
        {
            var path = Path.Combine(root, $"archive-{sequence++}.dat");
            WriteArchive(path, entries);
            return path;
        }
        GameInstallation Game(string path) => new("joey", Path.Combine(root, "synthetic-unused.exe"), path,
            Path.Combine(root, "synthetic-unused-voice.dat"), root);
        var reader = new NativeCardArtworkReader();
        var bmp = Bitmap();

        await run("native minis follow slot ordinals and preserve opaque BGRA corners", async () =>
        {
            var names = Names();
            names[0] = "unrelated_zero.bmp";
            names[817] = "not_an_external_id.bmp";
            var path = Archive(new(@"mini\list_card.txt", Table(names)),
                new(@"MINI\UNRELATED_ZERO.BMP", bmp), new(@"mini\not_an_external_id.bmp", Bitmap(49, trailing: 2)));
            var result = await reader.LoadAsync(Game(path), [817, 0]);
            Require(result.RequestedCount == 2 && result.Images.Select(i => i.Slot).SequenceEqual(new[] { 817, 0 }), "Slot order changed.");
            Require(result.Images[0].Width == 49 && result.Images[0].Height == 72, "Narrow native mini was resized.");
            foreach (var image in result.Images)
            {
                Require(image.BgraPixels.Length == image.Width * 72 * 4, "BGRA buffer extent differs.");
                Pixel(image, 0, 0, 0, 255, 0);
                Pixel(image, image.Width - 1, 0, 15, 27, 213);
                Pixel(image, 0, 71, 198, 40, 6);
                Pixel(image, image.Width - 1, 71, 1, 2, 3);
                Require(image.BgraPixels.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255), "Card green became transparent.");
            }
        });

        await run("native minis accept raw and LZSS literal payloads including two trailing bytes", async () =>
        {
            var names = Names();
            var narrow = Bitmap(49, trailing: 2);
            var table = Table(names);
            var path = Archive(new(@"mini\list_card.txt", Literals(table), table.Length),
                new(@"mini\tile_2.bmp", Literals(narrow), narrow.Length), new(@"mini\tile_3.bmp", Bitmap(trailing: 2)));
            var result = await reader.LoadAsync(Game(path), [2, 3]);
            Require(result.Images.Count == 2 && result.Images[0].Width == 49 && result.Images[1].Width == 50,
                "Raw/compressed native sizes differ.");
            Pixel(result.Images[0], 0, 0, 0, 255, 0);
            Pixel(result.Images[1], 49, 71, 1, 2, 3);
        });

        await run("native minis omit only missing selected resources and never require unrelated images", async () =>
        {
            var names = Names();
            names[4] = names[3]; // Repeated native placeholder names are legal in the complete filename table.
            var path = Archive(new(@"mini\list_card.txt", Table(names)), new(@"mini\tile_3.bmp", bmp),
                new(@"mini\tile_100.bmp", "unrequested malformed image"u8.ToArray()));
            var result = await reader.LoadAsync(Game(path), [2, 3, 4]);
            Require(result.RequestedCount == 3 && result.Images.Select(i => i.Slot).SequenceEqual(new[] { 3, 4 }),
                "Missing-resource accounting or shared filenames differ.");
            Require(!ReferenceEquals(result.Images[0].BgraPixels, result.Images[1].BgraPixels), "Slot images share mutable pixels.");
            var empty = await reader.LoadAsync(Game(Path.Combine(root, "absent.dat")), []);
            Require(empty.RequestedCount == 0 && empty.Images.Count == 0, "Empty request read an archive.");
        });

        await run("native mini requests reject duplicate, invalid and excessive slots before IO", async () =>
        {
            var absent = Game(Path.Combine(root, "absent.dat"));
            await Expect<ArgumentNullException>(() => reader.LoadAsync(null!, [0]));
            await Expect<ArgumentNullException>(() => reader.LoadAsync(absent, null!));
            foreach (var slots in new[] { new[] { -1 }, new[] { 1115 }, new[] { int.MaxValue }, new[] { 0, 0 } })
                await Expect<ArgumentException>(() => reader.LoadAsync(absent, slots));
            await Expect<ArgumentOutOfRangeException>(() => reader.LoadAsync(absent, Enumerable.Range(0, 772).ToArray()));
            var path = Archive(new Entry(@"mini\list_card.txt", Table(Names())));
            var maximum = await reader.LoadAsync(Game(path), Enumerable.Range(0, 771).ToArray());
            Require(maximum.RequestedCount == 771 && maximum.Images.Count == 0, "Maximum request count differs.");
        });

        await run("native filename tables reject traversal, non-ASCII and malformed record counts", async () =>
        {
            foreach (var bad in new[] { "../tile.bmp", @"..\tile.bmp", @"C:\tile.bmp", "/tile.bmp", "tile:1.bmp", "tile..bmp",
                "tile.png", ".bmp", "tile name.bmp", "tile-é.bmp", "tile\0.bmp", new string('x', 65) + ".bmp" })
            {
                var names = Names(); names[1100] = bad;
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"mini\list_card.txt", Table(names)))), [0]));
            }
            foreach (var count in new[] { 0, 1114, 1116 })
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"mini\list_card.txt", Table(Enumerable.Range(0, count)
                    .Select(i => $"tile_{i}.bmp").ToArray())))), [0]));
            var wrongComment = Encoding.ASCII.GetBytes("// 1:[0]\n" + Encoding.ASCII.GetString(Table(Names(), comments: false)));
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"mini\list_card.txt", wrongComment))), [0]));
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"unrelated", [1]))), [0]));
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"mini\list_card.txt", new byte[128 * 1024 + 1]))), [0]));
        });

        await run("native mini decoder rejects malformed headers, dimensions and pixel extents", async () =>
        {
            var variants = new List<byte[]> { Array.Empty<byte>(), "not a bitmap"u8.ToArray(), Bitmap(48), Bitmap(51), Bitmap(50, 71), Bitmap(50, -72),
                Bitmap(trailing: 1), Bitmap(trailing: 3), bmp[..^1] };
            foreach (var change in new Action<byte[]>[] {
                bytes => bytes[0] = 0,
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), uint.MaxValue),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 53),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), uint.MaxValue),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 41),
                bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), int.MaxValue),
                bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), int.MinValue),
                bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 2),
                bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 32),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30), 1),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34), 1) })
            {
                var changed = bmp.ToArray(); change(changed); variants.Add(changed);
            }
            foreach (var variant in variants)
            {
                var path = Archive(new(@"mini\list_card.txt", Table(Names())), new(@"mini\tile_0.bmp", variant));
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(path), [0]));
            }
            var declared = bmp.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(declared.AsSpan(34), (uint)(bmp.Length - 54));
            Require((await reader.LoadAsync(Game(Archive(new(@"mini\list_card.txt", Table(Names())), new(@"mini\tile_0.bmp", declared))), [0]))
                .Images.Count == 1, "Explicit valid imageSize was rejected.");
        });

        await run("native archive rejects duplicate targets and invalid directory ranges", async () =>
        {
            var table = new Entry(@"mini\list_card.txt", Table(Names()));
            foreach (var duplicate in new[] { new Entry(@"MINI\LIST_CARD.TXT", Table(Names())), new Entry(@"MINI\TILE_0.BMP", bmp) })
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(table, new(@"mini\tile_0.bmp", bmp), duplicate)), [0]));
            foreach (var mutation in new Action<byte[]>[] {
                bytes => bytes[0] = 0,
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 100_000),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12 + 256), 12),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12 + 256), uint.MaxValue),
                bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12 + 264), uint.MaxValue),
                bytes => bytes.AsSpan(12, 256).Fill(0x11),
                bytes => bytes[12] = 0x08 })
            {
                var path = Archive(table, new(@"mini\tile_0.bmp", bmp));
                var bytes = await File.ReadAllBytesAsync(path); mutation(bytes); await File.WriteAllBytesAsync(path, bytes);
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(path), [0]));
            }
            foreach (var length in new[] { 0, 11, 100 })
            {
                var path = Path.Combine(root, $"short-{length}.dat"); await File.WriteAllBytesAsync(path, new byte[length]);
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(path), [0]));
            }
        });

        await run("batch archive enforces name, per-resource and aggregate byte budgets", async () =>
        {
            var exact = new byte[16 * 1024]; exact[0] = 17;
            var path = Archive(Enumerable.Range(0, 1115).Select(i => new Entry($"resource_{i}", [17])).ToArray());
            using (var batch = GameArchive.OpenBatch(path, default))
            {
                Require(batch.ReadSelected(Enumerable.Range(0, 1115).Select(i => $"resource_{i}").ToArray(), default).Length == 1115,
                    "Exact batch name cap failed.");
                await Expect<ArgumentOutOfRangeException>(() => { batch.ReadSelected(Enumerable.Range(0, 1116).Select(i => $"r{i}").ToArray(), default); return Task.CompletedTask; });
                await Expect<ArgumentException>(() => { batch.ReadSelected(["resource_0", "RESOURCE_0"], default); return Task.CompletedTask; });
                await Expect<ArgumentException>(() => { batch.ReadSelected([""], default); return Task.CompletedTask; });
            }
            path = Archive(Enumerable.Range(0, 1025).Select(i => new Entry($"large_{i}", exact)).ToArray());
            using (var batch = GameArchive.OpenBatch(path, default))
            {
                Require(batch.ReadSelected(Enumerable.Range(0, 1024).Select(i => $"large_{i}").ToArray(), default).Length == 1024,
                    "Exact 16 MiB batch budget failed.");
                await Expect<InvalidDataException>(() => { batch.ReadSelected(Enumerable.Range(0, 1025).Select(i => $"large_{i}").ToArray(), default); return Task.CompletedTask; });
            }
            path = Archive(new Entry("large", new byte[16 * 1024 + 1]));
            using (var batch = GameArchive.OpenBatch(path, default))
                await Expect<InvalidDataException>(() => { batch.ReadSelected(["large"], default); return Task.CompletedTask; });
        });

        await run("archive legacy reads retain 16-name limits and exact LZSS semantics", async () =>
        {
            var path = Archive(Enumerable.Range(0, 17).Select(i => new Entry($"resource_{i}", [17])).ToArray());
            Require(GameArchive.Read(path, Enumerable.Range(0, 16).Select(i => $"resource_{i}").ToArray(), 1, default)?.Length == 16,
                "Legacy 16-name boundary changed.");
            await Expect<ArgumentOutOfRangeException>(() => { GameArchive.Read(path, Enumerable.Range(0, 17).Select(i => $"resource_{i}").ToArray(), 1, default); return Task.CompletedTask; });
            Require(GameArchive.Read(path, ["absent"], 1, default) is null, "Legacy missing result changed.");
            path = Archive(new("zeroes", [0, 0, 6], 9), new("literal", Literals([4, 5, 6, 7]), 4));
            var resources = GameArchive.Read(path, ["zeroes", "literal"], 64, default)!;
            Require(resources[0].SequenceEqual(new byte[9]) && resources[1].SequenceEqual(new byte[] { 4, 5, 6, 7 }), "Legacy LZSS decoding changed.");
            foreach (var entry in new[] { new Entry("bad", [0], 9), new Entry("bad", [1], 9),
                new Entry("bad", [0, 0], 9), new Entry("bad", [0, 0, 15], 9), new Entry("bad", [1, 7, 9], 1) })
            {
                path = Archive(entry);
                await Expect<InvalidDataException>(() => { GameArchive.Read(path, ["bad"], 64, default); return Task.CompletedTask; });
                using var batch = GameArchive.OpenBatch(path, default);
                await Expect<InvalidDataException>(() => { batch.ReadSelected(["bad"], default); return Task.CompletedTask; });
            }
            path = Archive(new("duplicate", [1]), new("DUPLICATE", [2]));
            Require(GameArchive.Read(path, ["duplicate"], 64, default) is null, "Legacy duplicate target behavior changed.");
        });

        await run("native mini reads propagate cancellation through loading and indexed reads", async () =>
        {
            var path = Archive(new(@"mini\list_card.txt", Table(Names())), new(@"mini\tile_0.bmp", bmp));
            using var cancellation = new CancellationTokenSource();
            using var batch = GameArchive.OpenBatch(path, default);
            cancellation.Cancel();
            await Expect<OperationCanceledException>(() => reader.LoadAsync(Game(path), [0], cancellation.Token));
            await Expect<OperationCanceledException>(() => { GameArchive.OpenBatch(path, cancellation.Token).Dispose(); return Task.CompletedTask; });
            await Expect<OperationCanceledException>(() => { batch.ReadRequired(@"mini\list_card.txt", 128 * 1024, cancellation.Token); return Task.CompletedTask; });
            await Expect<OperationCanceledException>(() => { batch.ReadSelected([@"mini\tile_0.bmp"], cancellation.Token); return Task.CompletedTask; });
            var many = Archive(Enumerable.Range(0, 40_000).Select(i => new Entry($"directory_{i}", [1])).ToArray());
            using var duringScan = new CancellationTokenSource();
            duringScan.CancelAfter(1);
            await Expect<OperationCanceledException>(() => { GameArchive.OpenBatch(many, duringScan.Token).Dispose(); return Task.CompletedTask; });
        });

        await run("native mini IO stays read-only and rejects relative or directory paths", async () =>
        {
            var path = Archive(new(@"mini\list_card.txt", Table(Names())), new(@"mini\tile_0.bmp", bmp));
            var before = await File.ReadAllBytesAsync(path);
            var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                Require((await reader.LoadAsync(Game(path), [0])).Images.Count == 1, "Read-only archive could not be read.");
                Require((await File.ReadAllBytesAsync(path)).SequenceEqual(before), "Archive bytes changed.");
            }
            finally { File.SetAttributes(path, attributes); }
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game("relative.dat"), [0]));
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(root), [0]));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                await Expect<IOException>(() => reader.LoadAsync(Game(path), [0]));
            var link = Path.Combine(root, "linked-archive.dat");
            try { File.CreateSymbolicLink(link, path); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(link), [0]));
        });
    }

    public static async Task VerifyInstalled(IReadOnlyList<GameInstallation> games)
    {
        foreach (var game in games)
        {
            var tables = GameArchive.Read(game.DataPath, [@"bin#\card_pack.bin", @"bin#\card_id.bin"], 4096, default)
                ?? throw new InvalidDataException("Installed synthetic-check card tables unavailable.");
            Require(tables.All(t => t.Length == 2230), "Installed card table length differs.");
            var mask = game.GameId switch { "yugi" => 1, "kaiba" => 3, "joey" => 7, _ => throw new InvalidDataException("Unknown game.") };
            var slots = Enumerable.Range(0, 1115).Where(slot => (BinaryPrimitives.ReadUInt16LittleEndian(tables[0].AsSpan(slot * 2)) & mask) != 0).ToArray();
            var batch = await new NativeCardArtworkReader().LoadAsync(game, slots);
            Require(batch.RequestedCount == slots.Length && batch.Images.Count == slots.Length, "Installed native minis are missing.");
            Require(batch.Images.All(i => i.Width is 49 or 50 && i.Height == 72 && i.BgraPixels.Length == i.Width * 72 * 4),
                "Installed native mini dimensions differ.");
        }
    }

    private sealed record Entry(string Name, byte[] Data, int? Expanded = null);

    private static string[] Names() => Enumerable.Range(0, 1115).Select(i => $"tile_{i}.bmp").ToArray();

    private static byte[] Table(string[] names, bool comments = true)
        => Encoding.Latin1.GetBytes(string.Join("\r\n", names.Select((name, slot) => comments ? $"// {slot}:[{slot}]\r\n{name}\r\n" : name)));

    private static void WriteArchive(string path, Entry[] entries)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("KCEJYUGI"u8);
        writer.Write(entries.Length);
        var offset = 12 + entries.Length * 268;
        foreach (var entry in entries)
        {
            var name = new byte[256]; Encoding.ASCII.GetBytes(entry.Name).CopyTo(name, 0);
            for (var index = 0; index < name.Length; index++) name[index] = (byte)(name[index] << 4 | name[index] >> 4);
            writer.Write(name); writer.Write(offset); writer.Write(entry.Expanded ?? entry.Data.Length); writer.Write(entry.Data.Length);
            offset += entry.Data.Length;
        }
        foreach (var entry in entries) writer.Write(entry.Data);
    }

    private static byte[] Literals(byte[] bytes)
    {
        using var stream = new MemoryStream();
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var count = Math.Min(8, bytes.Length - offset);
            stream.WriteByte((byte)((1 << count) - 1));
            stream.Write(bytes.AsSpan(offset, count));
        }
        return stream.ToArray();
    }

    private static byte[] Bitmap(int width = 50, int height = 72, int trailing = 0)
    {
        var rows = Math.Abs(height);
        var stride = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + stride * rows + trailing];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        for (var y = 0; y < rows; y++)
        {
            var row = 54 + (rows - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                bytes[row + x * 3] = 31; bytes[row + x * 3 + 1] = 47; bytes[row + x * 3 + 2] = 63;
            }
            bytes.AsSpan(row + width * 3, stride - width * 3).Fill(0xA5);
        }
        Put(0, 0, 0, 255, 0); Put(width - 1, 0, 15, 27, 213); Put(0, rows - 1, 198, 40, 6); Put(width - 1, rows - 1, 1, 2, 3);
        return bytes;
        void Put(int x, int y, byte blue, byte green, byte red)
        {
            var offset = 54 + (rows - 1 - y) * stride + x * 3;
            bytes[offset] = blue; bytes[offset + 1] = green; bytes[offset + 2] = red;
        }
    }

    private static void Pixel(NativeCardImage image, int x, int y, byte blue, byte green, byte red)
        => Require(image.BgraPixels.AsSpan((y * image.Width + x) * 4, 4).SequenceEqual(new byte[] { blue, green, red, 255 }),
            "Native BGRA channels, row order or opacity differ.");

    private static async Task Expect<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
