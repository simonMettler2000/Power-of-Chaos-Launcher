using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

// Every archive payload and name in these fixtures is authored synthetic data.
internal static class NativeCardNameChecks
{
    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(fixtures, "native-card-names");
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
        var reader = new NativeCardNameReader();

        await run("card-name language preferences normalize to six stable choices", () =>
        {
            Require(CardLanguages.All.Select(language => language.Id).SequenceEqual(new[] { "en", "de", "fr", "it", "es", "ja" }),
                "Language IDs or presentation order changed.");
            Require(CardLanguages.All.Select(language => language.ResourceCode).SequenceEqual(new[] { "eng", "ger", "fra", "ita", "spa", "jpn" }),
                "Language resources differ.");
            Require(CardLanguages.All.All(language => language.CodePage == (language.Id == "ja" ? 932 : 1252)), "Language encodings differ.");
            foreach (var language in CardLanguages.All)
            {
                Require(!string.IsNullOrWhiteSpace(language.DisplayName), "Language label is empty.");
                Require(CardLanguages.Normalize(" " + language.Id.ToUpperInvariant() + " ") == language.Id, "Known preference did not normalize.");
                Require(CardLanguages.Get(language.Id) == language, "Language lookup differs.");
            }
            foreach (var unknown in new string?[] { null, "", " ", "xx", "eng", "en-US", "../eng" })
                Require(CardLanguages.Normalize(unknown) == "en" && CardLanguages.Get(unknown).Id == "en", "Unknown preference did not use English.");
            return Task.CompletedTask;
        });

        await run("native names follow save slots, preserve duplicate text and omit only empty records", async () =>
        {
            var table = Table(1252, (816, "Synthetic zero identity"), (817, "Synthetic shared label"), (934, ""), (1114, "Synthetic shared label"));
            var result = await reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", table))), [1114, 934, 816, 817, 0], "en");
            Require(result.RequestedCount == 5 && result.Names.Count == 3, "Empty records affected other names or request accounting.");
            Require(result.Names[816] == "Synthetic zero identity" && result.Names[817] == result.Names[1114], "Names were mapped by identity or deduplicated by text.");
            Require(!result.Names.ContainsKey(934) && !result.Names.ContainsKey(0), "Empty names were fabricated.");
            await Expect<NotSupportedException>(() => { ((IDictionary<int, string>)result.Names).Add(1, "Changed"); return Task.CompletedTask; });
        });

        await run("native names choose one language resource and keep installation-specific text", async () =>
        {
            var entries = CardLanguages.All.Select(language => new Entry(@"bin#\card_name" + language.ResourceCode + ".bin",
                Table(language.CodePage, (12, "Synthetic " + language.Id)))).ToArray();
            var first = Game(Archive(entries));
            foreach (var language in CardLanguages.All)
            {
                var result = await reader.LoadAsync(first, [12], " " + language.Id.ToUpperInvariant() + " ");
                Require(result.Names[12] == "Synthetic " + language.Id, "Wrong localized resource was selected.");
            }
            var second = Game(Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252, (12, "Different synthetic installation")))));
            Require((await reader.LoadAsync(second, [12], "en")).Names[12] == "Different synthetic installation", "Names leaked across installations.");
            Require((await reader.LoadAsync(first, [12], "en")).Names[12] == "Synthetic en", "A later installation overwrote an earlier name.");
        });

        await run("native names decode Windows Western and Japanese encodings including punctuation", async () =>
        {
            const string western = "Épreuve – Œuvre œuf „inventée“";
            const string japanese = "試験・合成～";
            var westernTable = Table(1252, (9, western));
            var japaneseTable = Table(932, (9, japanese));
            var path = Archive(new(@"bin#\card_namefra.bin", Literals(westernTable), westernTable.Length),
                new(@"bin#\card_namejpn.bin", japaneseTable));
            Require((await reader.LoadAsync(Game(path), [9], "fr")).Names[9] == western, "Windows-1252 punctuation or accents changed.");
            Require((await reader.LoadAsync(Game(path), [9], "ja")).Names[9] == japanese, "Windows-932 glyphs changed.");
        });

        await run("native name requests reject invalid arguments before IO and allow the exact slot budget", async () =>
        {
            var absent = Game(Path.Combine(root, "absent.dat"));
            await Expect<ArgumentNullException>(() => reader.LoadAsync(null!, [0], "en"));
            await Expect<ArgumentNullException>(() => reader.LoadAsync(absent, null!, "en"));
            await Expect<ArgumentNullException>(() => reader.LoadAsync(absent, [0], null!));
            foreach (var language in new[] { "", " ", "xx", "eng", "en-US", @"..\eng" })
                await Expect<ArgumentException>(() => reader.LoadAsync(absent, [0], language));
            foreach (var slots in new[] { new[] { -1 }, new[] { 1115 }, new[] { int.MaxValue }, new[] { 0, 0 } })
                await Expect<ArgumentException>(() => reader.LoadAsync(absent, slots, "en"));
            await Expect<ArgumentOutOfRangeException>(() => reader.LoadAsync(absent, Enumerable.Range(0, 772).ToArray(), "en"));
            var empty = await reader.LoadAsync(absent, [], "en");
            Require(empty.RequestedCount == 0 && empty.Names.Count == 0, "An empty request read an archive.");
            var maximum = await reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252)))), Enumerable.Range(0, 771).ToArray(), "en");
            Require(maximum.RequestedCount == 771 && maximum.Names.Count == 0, "Maximum request count differs.");
        });

        await run("native name tables validate their complete fixed record extent and padding", async () =>
        {
            foreach (var length in new[] { 0, 64, 1115 * 64 - 1, 1115 * 64 + 1, 128 * 1024 })
            {
                var bytes = new byte[length];
                // The archive reader treats an empty named resource as unavailable.
                var game = Game(Archive(new Entry(@"bin#\card_nameeng.bin", bytes)));
                if (length == 0) Require((await reader.LoadAsync(game, [1], "en")).Names.Count == 0, "Empty resource fallback changed.");
                else await Expect<InvalidDataException>(() => reader.LoadAsync(game, [1], "en"));
            }
            foreach (var mutate in new Action<byte[]>[] {
                bytes => bytes.AsSpan(1114 * 64, 64).Fill((byte)'x'),
                bytes => bytes[1114 * 64 + 63] = (byte)'x',
                bytes => { bytes[1114 * 64] = (byte)'x'; bytes[1114 * 64 + 2] = (byte)'y'; } })
            {
                var bytes = Table(1252, (1, "Valid synthetic name")); mutate(bytes);
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", bytes))), [1], "en"));
            }
            var longest = new string('s', 63);
            Require((await reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252, (1, longest))))), [1], "en")).Names[1] == longest,
                "A 63-byte terminated name was rejected.");
        });

        await run("native names reject malformed selected encodings and control characters", async () =>
        {
            foreach (var control in new[] { (byte)1, (byte)9, (byte)10, (byte)13, (byte)127, (byte)129 })
            {
                var table = Table(1252); table[64] = control;
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", table))), [1], "en"));
            }
            var brokenJapanese = Table(932); brokenJapanese[64] = 0x81;
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_namejpn.bin", brokenJapanese))), [1], "ja"));
            // Unrequested records are structurally checked without decoding every localized name.
            Require((await reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_namejpn.bin", brokenJapanese))), [2], "ja")).Names.Count == 0,
                "An unrequested name was decoded.");
            var spaced = await reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252, (1, "  Authored label  "), (2, "   "))))), [1, 2], "en");
            Require(spaced.Names[1] == "Authored label" && !spaced.Names.ContainsKey(2), "Whitespace-only fallback or boundary trimming differs.");
        });

        await run("missing native name resources fall back without reading unrelated payloads", async () =>
        {
            var path = Archive(new Entry("unrelated", "synthetic unrelated payload"u8.ToArray()));
            var missing = await reader.LoadAsync(Game(path), [1, 2], "en");
            Require(missing.RequestedCount == 2 && missing.Names.Count == 0, "Missing resource accounting differs.");
            path = Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252, (1, "Kept synthetic name"))), new Entry("unrelated", [0], int.MaxValue));
            Require((await reader.LoadAsync(Game(path), [1], "en")).Names[1] == "Kept synthetic name", "Unrelated payload was loaded.");
            var bytes = await File.ReadAllBytesAsync(path);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12 + 268 + 256), uint.MaxValue);
            await File.WriteAllBytesAsync(path, bytes);
            Require((await reader.LoadAsync(Game(path), [1], "en")).Names.Count == 0, "Invalid unrelated archive extent was ignored.");
            await Expect<FileNotFoundException>(() => reader.LoadAsync(Game(Path.Combine(root, "missing-archive.dat")), [1], "en"));
        });

        await run("native name decompression rejects malformed streams and honors the payload cap", async () =>
        {
            foreach (var entry in new[] { new Entry(@"bin#\card_nameeng.bin", [0], 1115 * 64),
                new Entry(@"bin#\card_nameeng.bin", [1], 1115 * 64),
                new Entry(@"bin#\card_nameeng.bin", [0, 0], 1115 * 64) })
                await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Archive(entry)), [1], "en"));
            var tooLarge = await reader.LoadAsync(Game(Archive(new Entry(@"bin#\card_nameeng.bin", [1], 128 * 1024 + 1))), [1], "en");
            Require(tooLarge.Names.Count == 0, "Oversized named resource escaped the archive cap.");
        });

        await run("native name reads remain cancellable and read-only", async () =>
        {
            var path = Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252, (1, "Read-only authored label"))));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Expect<OperationCanceledException>(() => reader.LoadAsync(Game(path), [1], "en", cancellation.Token));
            await Expect<OperationCanceledException>(() => reader.LoadAsync(Game(path), [], "en", cancellation.Token));
            var many = Archive(Enumerable.Range(0, 40_000).Select(index => new Entry($"unrelated_{index}", [1])).ToArray());
            using var duringScan = new CancellationTokenSource();
            duringScan.CancelAfter(1);
            await Expect<OperationCanceledException>(() => reader.LoadAsync(Game(many), [1], "en", duringScan.Token));
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                Require((await reader.LoadAsync(Game(path), [1], "en")).Names.Count == 1, "A read-only archive could not be read.");
                Require(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(before), "The source archive changed.");
            }
            finally { File.SetAttributes(path, attributes); }
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game("relative.dat"), [1], "en"));
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(root), [1], "en"));
            // Windows rejects an existing writer despite its sharing flags. Unix needs an exclusive advisory lock.
            var writerSharing = OperatingSystem.IsWindows() ? FileShare.ReadWrite : FileShare.None;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, writerSharing))
                await Expect<IOException>(() => reader.LoadAsync(Game(path), [1], "en"));
        });

        await run("native name reads reject file links and linked ancestor directories", async () =>
        {
            var path = Archive(new Entry(@"bin#\card_nameeng.bin", Table(1252, (1, "Linked authored label"))));
            var link = Path.Combine(root, "linked-file.dat");
            var directoryLink = Path.Combine(root, "linked-directory");
            try
            {
                File.CreateSymbolicLink(link, path);
                Directory.CreateSymbolicLink(directoryLink, root);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw new Program.SkipCheckException("Symbolic-link setup unavailable: " + exception.GetType().Name);
            }
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(link), [1], "en"));
            await Expect<InvalidDataException>(() => reader.LoadAsync(Game(Path.Combine(directoryLink, Path.GetFileName(path))), [1], "en"));
        });
    }

    public static async Task VerifyInstalled(IReadOnlyList<GameInstallation> games)
    {
        var reader = new NativeCardNameReader();
        foreach (var game in games)
        {
            var tables = GameArchive.Read(game.DataPath, [@"bin#\card_pack.bin", @"bin#\card_id.bin"], 4096, default)
                ?? throw new InvalidDataException("Installed card tables unavailable.");
            Require(tables.All(table => table.Length == 2230), "Installed card table length differs.");
            var (mask, count) = game.GameId switch { "yugi" => (1, 155), "kaiba" => (3, 466), "joey" => (7, 771), _ => throw new InvalidDataException("Unknown game.") };
            var slots = Enumerable.Range(0, 1115).Where(slot => (BinaryPrimitives.ReadUInt16LittleEndian(tables[0].AsSpan(slot * 2)) & mask) != 0).ToArray();
            Require(slots.Length == count, "Installed card pool differs.");
            foreach (var language in CardLanguages.All)
            {
                var batch = await reader.LoadAsync(game, slots, language.Id);
                var hasBlank = game.GameId == "kaiba" && language.Id == "it";
                Require(batch.RequestedCount == count && batch.Names.Count == count - (hasBlank ? 1 : 0), "Installed localized name count differs.");
                Require(batch.Names.Keys.All(slots.Contains), "Installed names used a different slot mapping.");
                Require(batch.Names.Values.All(name => name.Length is > 0 and <= 128 && !name.Any(value => char.IsControl(value)
                    || char.GetUnicodeCategory(value) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)),
                    "Installed name contains unsupported display characters.");
                if (hasBlank)
                {
                    Require(slots.Contains(934) && !batch.Names.ContainsKey(934)
                        && BinaryPrimitives.ReadUInt16LittleEndian(tables[1].AsSpan(934 * 2)) == 248, "Known blank Italian record differs.");
                }
                var zeroIdentitySlot = slots.SingleOrDefault(slot => BinaryPrimitives.ReadUInt16LittleEndian(tables[1].AsSpan(slot * 2)) == 0, -1);
                if (zeroIdentitySlot >= 0) Require(batch.Names.ContainsKey(zeroIdentitySlot), "Valid external identity zero lost its name.");
            }
        }
    }

    private sealed record Entry(string Name, byte[] Data, int? Expanded = null);

    private static byte[] Table(int codePage, params (int Slot, string Name)[] names)
    {
        var table = new byte[1115 * 64];
        var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var (slot, name) in names)
        {
            var bytes = encoding.GetBytes(name);
            Require(slot is >= 0 and < 1115 && bytes.Length < 64, "Invalid synthetic name fixture.");
            bytes.CopyTo(table, slot * 64);
        }
        return table;
    }

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
