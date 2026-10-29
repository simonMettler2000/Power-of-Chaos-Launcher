using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using System.Numerics;
using Microsoft.Win32;
using PowerOfChaos.Launcher.TestFixtures;
using System.Buffers.Binary;

namespace PowerOfChaos.Launcher.Tests;

internal static class CollectionChecks
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> run)
    {
        var directory = Path.Combine(root, "collection");
        Directory.CreateDirectory(directory);
        var games = new List<GameInstallation>();
        foreach (var game in GameCatalog.All)
        {
            var folder = Path.Combine(directory, game.Id);
            Directory.CreateDirectory(folder);
            var archive = Path.Combine(folder, "data.dat");
            CollectionFixtures.WriteArchive(archive);
            games.Add(new(game.Id, Path.Combine(folder, game.ExecutableName), archive, Path.Combine(folder, "voice.dat"), directory));
        }
        var savePath = Path.Combine(directory, "system.dat");
        var selected = new[] { 0, 1, 154, 155, 465, 466, 770 };
        var save = CollectionFixtures.Save(selected.Select(card => (CollectionFixtures.Slot(card), (byte)3)),
            deckFlags: [CollectionFixtures.Slot(17)]);
        await File.WriteAllBytesAsync(savePath, save);
        var reader = new CardCollectionReader(_ => []);
        await run("collection codec matches independent small golden vectors", () =>
        {
            Require(save.AsSpan(4, 9).SequenceEqual(Convert.FromHexString("00028098F7E790801F")), "00..07 golden differs.");
            var allBytes = CollectionFixtures.Save([], Enumerable.Repeat((byte)255, 8).ToArray());
            Require(allBytes.AsSpan(4, 9).SequenceEqual(Convert.FromHexString("D8B061C3860D1B366C")), "FF golden differs.");
            Require(CollectionSave.ReadQuantities(allBytes).All(q => q == 0), "Valid zero collection differs.");
            var decoded = CollectionSave.ReadQuantities(save);
            Require(selected.All(card => decoded[CollectionFixtures.Slot(card)] == 3), "Sparse ownership differs.");
            return Task.CompletedTask;
        });
        await run("shared collection uses cumulative pools and unique union, including external ID zero", async () =>
        {
            var snapshot = await reader.ReadAsync(games, savePath);
            Require(snapshot.UniqueCards == 7, "Copies or overlapping games were counted twice.");
            Require(snapshot.Games.Select(g => g.OwnedCards).SequenceEqual(new int?[] { 3, 5, 7 }), "Cumulative owned counts differ.");
            Require(snapshot.Games.Select(g => g.TotalCards).SequenceEqual(new[] { 155, 466, 771 }), "Pool totals differ.");
            Require((await File.ReadAllBytesAsync(savePath)).SequenceEqual(save), "Reader changed the save.");
        });
        await run("collection metadata partitions all native slots into exact owned and missing pools", async () =>
        {
            var snapshot = await reader.ReadAsync(games, savePath);
            var totals = new[] { 155, 466, 771 };
            var expectedOwned = new[] { 3, 5, 7 };
            var expectedMissing = new[] { 152, 461, 764 };
            for (var index = 0; index < snapshot.Games.Count; index++)
            {
                var progress = snapshot.Games[index];
                var owned = progress.Cards.Where(card => card.Quantity > 0).ToArray();
                var missing = progress.Cards.Where(card => card.Quantity == 0).ToArray();
                Require(progress.Cards.Count == totals[index] && owned.Length == expectedOwned[index]
                    && missing.Length == expectedMissing[index] && progress.OwnedCards == owned.Length,
                    "Metadata does not completely partition the cumulative pool.");
                Require(progress.Cards.Select(card => (int)card.CardId).SequenceEqual(Enumerable.Range(0, totals[index])),
                    "Metadata identities are missing, duplicated or out of order.");
                Require(progress.Cards.Select(card => card.Slot).Distinct().Count() == totals[index], "Metadata repeats a native slot.");
                foreach (var card in progress.Cards)
                {
                    Require(card.Slot == CollectionFixtures.Slot(card.CardId), "Metadata uses an external ID as a save/artwork slot.");
                    Require(card.Quantity == (selected.Contains(card.CardId) ? 3 : 0), "Metadata quantity differs from the forward save slot.");
                    Require(card.OriginGameId == (card.CardId < 155 ? "yugi" : card.CardId < 466 ? "kaiba" : "joey"),
                        "Metadata origin differs from earliest pool membership.");
                }
                var zero = progress.Cards.Single(card => card.CardId == 0);
                Require(zero.Slot == CollectionFixtures.Slot(0) && zero.Slot != zero.CardId && zero.Quantity == 3,
                    "External ID zero was omitted or confused with its internal slot.");
                Require(progress.Cards.Single(card => card.CardId == 17).Quantity == 0, "Deck flags fabricated an owned card.");
            }
            foreach (var card in snapshot.Games[2].Cards)
            {
                var shared = snapshot.Games.SelectMany(game => game.Cards).Where(other => other.CardId == card.CardId).ToArray();
                Require(shared.All(other => other.Slot == card.Slot && other.Quantity == card.Quantity
                    && other.OriginGameId == card.OriginGameId), "Shared identities have inconsistent per-game metadata.");
            }
        });
        await run("collection origins use earliest availability bits rather than cumulative mask values", async () =>
        {
            var tables = CollectionFixtures.CardTables();
            var pack = tables[0].Data;
            foreach (var (card, flags) in new[] { (0, 1), (1, 3), (2, 5), (3, 7), (155, 2), (156, 6), (466, 4) })
                BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(CollectionFixtures.Slot(card) * 2), (ushort)flags);
            var mixedGames = new List<GameInstallation>();
            foreach (var game in games)
            {
                var path = Path.Combine(directory, $"mixed-flags-{game.GameId}.dat");
                CollectionFixtures.WriteArchive(path, tables);
                mixedGames.Add(game with { DataPath = path });
            }
            var snapshot = await reader.ReadAsync(mixedGames, savePath);
            Require(snapshot.Games.Select(game => game.TotalCards).SequenceEqual(new[] { 155, 466, 771 })
                && snapshot.Games.Select(game => game.Cards.Count).SequenceEqual(new[] { 155, 466, 771 }),
                "Mixed pack flags changed valid cumulative pools.");
            var cards = snapshot.Games[2].Cards;
            Require(new ushort[] { 0, 1, 2, 3 }.All(id => cards.Single(card => card.CardId == id).OriginGameId == "yugi"),
                "Flags 1/3/5/7 must retain Yugi as earliest availability.");
            Require(new ushort[] { 155, 156 }.All(id => cards.Single(card => card.CardId == id).OriginGameId == "kaiba"),
                "Flags 2/6 must retain Kaiba as earliest availability.");
            Require(cards.Single(card => card.CardId == 466).OriginGameId == "joey", "Flag 4 did not map to Joey availability.");
            Require(cards.Count(card => card.OriginGameId == "yugi") == 155
                && cards.Count(card => card.OriginGameId == "kaiba") == 311
                && cards.Count(card => card.OriginGameId == "joey") == 305, "Origin partition totals differ.");
        });
        await run("valid empty collections retain every missing card while unavailable saves expose no metadata", async () =>
        {
            var emptyPath = Path.Combine(directory, "metadata-empty.dat");
            var emptySave = CollectionFixtures.Save([]);
            await File.WriteAllBytesAsync(emptyPath, emptySave);
            var empty = await reader.ReadAsync(games, emptyPath);
            Require(empty.UniqueCards == 0 && empty.Games.All(game => game.OwnedCards == 0
                && game.Cards.Count == game.TotalCards && game.Cards.All(card => card.Quantity == 0)),
                "A valid empty collection lost its complete missing-card metadata.");
            Require(empty.Games.Select(game => game.Cards.Count).SequenceEqual(new[] { 155, 466, 771 }),
                "Valid empty metadata pool totals differ.");
            Require((await File.ReadAllBytesAsync(emptyPath)).SequenceEqual(emptySave), "Metadata reading changed the empty save.");
            RequireUnavailable(await reader.ReadAsync(games), "Unknown save exposed card metadata.");
            RequireUnavailable(await reader.ReadAsync(games, Path.Combine(directory, "metadata-absent.dat")),
                "Missing selected save exposed card metadata.");
        });
        await run("one added game reports a partial unique total honestly", async () =>
        {
            var snapshot = await reader.ReadAsync([games[0]], savePath);
            Require(snapshot.UniqueCards == 3 && snapshot.Message.Contains("readable games", StringComparison.Ordinal), "Partial union is misleading.");
            Require(snapshot.Games[1].OwnedCards is null && snapshot.Games[1].Message == "Game not added", "Missing game became zero.");
            Require(snapshot.Games.Skip(1).All(game => game.Cards.Count == 0), "Unadded games expose metadata.");
        });
        await run("missing save, empty library and ambiguous saves never become zero", async () =>
        {
            RequireUnavailable(await reader.ReadAsync(games), "Missing save became zero or exposed metadata.");
            RequireUnavailable(await reader.ReadAsync([]), "Empty library became zero or exposed metadata.");
            var other = Path.Combine(directory, "other.dat");
            await File.WriteAllBytesAsync(other, save);
            var ambiguous = new CardCollectionReader(_ => [savePath, other]);
            RequireUnavailable(await ambiguous.ReadAsync(games), "Ambiguous collection was guessed or exposed metadata.");
            Require((await ambiguous.ReadAsync(games, savePath)).UniqueCards == 7, "Explicit save did not resolve ambiguity.");
            RequireUnavailable(await ambiguous.ReadAsync(games, Path.Combine(directory, "missing.dat")), "Missing selected save silently fell back or exposed metadata.");
        });
        await run("collection rejects outer checksum, inner checksum, invalid codes and unsupported lengths", async () =>
        {
            var variants = new List<byte[]>();
            var outer = (byte[])save.Clone(); outer[0] ^= 1; variants.Add(outer);
            variants.Add(CollectionFixtures.Save([], validInnerChecksum: false));
            var code = (byte[])save.Clone(); code[4] = 0xFF; code[5] |= 1;
            CollectionFixtures.SetOuterChecksum(code); variants.Add(code);
            var unused = Enumerable.Range(0, 299).First(value => BigInteger.ModPow(value, 53, 299) > 255);
            var alias = (byte[])save.Clone(); alias[4] = (byte)unused; alias[5] = (byte)((alias[5] & 254) | unused >> 8);
            CollectionFixtures.SetOuterChecksum(alias); variants.Add(alias);
            variants.Add(new byte[5061]); variants.Add(new byte[5063]);
            foreach (var variant in variants)
            {
                var bad = Path.Combine(directory, "invalid.dat");
                await File.WriteAllBytesAsync(bad, variant);
                var snapshot = await reader.ReadAsync(games, bad);
                RequireUnavailable(snapshot, "Invalid save produced counts or card metadata.");
            }
        });
        await run("collection changes are reread without caching or modifying saves", async () =>
        {
            var path = Path.Combine(directory, "changing.dat");
            await File.WriteAllBytesAsync(path, CollectionFixtures.Save([]));
            Require((await reader.ReadAsync(games, path)).UniqueCards == 0, "Valid empty save is unavailable.");
            await File.WriteAllBytesAsync(path, save);
            Require((await reader.ReadAsync(games, path)).UniqueCards == 7, "Save change was not reread.");
        });
        await run("new card types update each eligible game while extra copies keep the unique total unchanged", async () =>
        {
            var path = Path.Combine(directory, "rewards.dat");
            var cards = selected.ToList();
            foreach (var (card, expected) in new[]
            {
                (100, new int?[] { 4, 6, 8 }), (300, new int?[] { 4, 7, 9 }), (600, new int?[] { 4, 7, 10 })
            })
            {
                cards.Add(card);
                await File.WriteAllBytesAsync(path, CollectionFixtures.Save(cards.Select(c => (CollectionFixtures.Slot(c), (byte)1))));
                var snapshot = await reader.ReadAsync(games, path);
                Require(snapshot.Games.Select(g => g.OwnedCards).SequenceEqual(expected) && snapshot.UniqueCards == cards.Count,
                    "A newly owned card did not update the correct cumulative pools.");
            }
            await File.WriteAllBytesAsync(path, CollectionFixtures.Save(cards.Select(c => (CollectionFixtures.Slot(c), (byte)9))));
            var duplicate = await reader.ReadAsync(games, path);
            Require(duplicate.UniqueCards == 10 && duplicate.Games.Select(g => g.OwnedCards).SequenceEqual(new int?[] { 4, 7, 10 }),
                "Extra copies changed distinct-type counts.");
            Require(duplicate.Games.All(game => game.Cards.Count == game.TotalCards
                && game.Cards.All(card => card.Quantity == (cards.Contains(card.CardId) ? 9 : 0))
                && game.Cards.Count(card => card.Quantity > 0) == game.OwnedCards),
                "Duplicate-copy metadata lost quantities, missing cards or distinct ownership.");
        });
        await run("configured CommonDir takes precedence over stale guessed saves and missing configuration never falls back", async () =>
        {
            var configured = Path.Combine(directory, "configured");
            Directory.CreateDirectory(configured);
            var active = Path.Combine(configured, "system.dat");
            await File.WriteAllBytesAsync(active, save);
            var discovered = new CardCollectionReader(installs => CardCollectionReader.FindSaves(installs, [configured], directory, path => path));
            var snapshot = await discovered.ReadAsync(games);
            Require(snapshot.SavePath == active && snapshot.UniqueCards == 7, "An old sibling save overrode the configured collection.");
            File.Delete(active); // Only this authored fixture is removed.
            Require((await discovered.ReadAsync(games)).UniqueCards is null, "Missing configured save silently selected a stale fallback.");
            await File.WriteAllBytesAsync(active, save);
            var candidates = CardCollectionReader.FindSaves(games, [configured, directory], null, path => path);
            Require((await new CardCollectionReader(_ => candidates).ReadAsync(games)).UniqueCards is null,
                "Distinct configured saves were guessed instead of requiring selection.");
        });
        await run("fallback candidates apply file virtualization and remain within the selected discovery root", async () =>
        {
            var virtualized = Path.Combine(directory, "virtualized.dat");
            await File.WriteAllBytesAsync(virtualized, save);
            var resolved = new List<string>();
            var installation = games[0];
            var candidates = CardCollectionReader.FindSaves([installation], [], null, path =>
            {
                resolved.Add(path);
                return path == Path.Combine(Path.GetDirectoryName(installation.ExecutablePath)!, "system.dat") ? virtualized : path;
            }).ToArray();
            Require(resolved.All(path => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
                "Fallback searched above its discovery root.");
            var snapshot = await new CardCollectionReader(_ => candidates.Where(path => path == virtualized)).ReadAsync(games);
            Require(snapshot.UniqueCards == 7 && snapshot.SavePath == virtualized, "A virtualized local save was ignored.");
        });
        if (OperatingSystem.IsWindows())
        {
            await run("per-user registry virtualization shadows machine CommonDir in both legacy views", () =>
            {
                if (!OperatingSystem.IsWindows()) throw new Program.SkipCheckException("Windows registry fixture requires Windows.");
                const string machine = @"SOFTWARE\KONAMI\Yu-Gi-Oh! Power Of Chaos\system";
                const string prefix = @"SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE\";
                const string tail = @"KONAMI\Yu-Gi-Oh! Power Of Chaos\system";
                var current = Path.Combine(directory, "current");
                var old = Path.Combine(directory, "old");
                var values = new Dictionary<(RegistryHive, RegistryView, string), object>
                {
                    [(RegistryHive.LocalMachine, RegistryView.Registry32, machine)] = old,
                    [(RegistryHive.LocalMachine, RegistryView.Registry64, machine)] = old,
                    [(RegistryHive.CurrentUser, RegistryView.Registry64, prefix + @"WOW6432Node\" + tail)] = current,
                    [(RegistryHive.CurrentUser, RegistryView.Registry64, prefix + tail)] = current.ToUpperInvariant()
                };
                object? ReadValue(RegistryHive hive, RegistryView view, string key)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    return values.GetValueOrDefault((hive, view, key));
                }
                var directories = CardCollectionReader.ReadCommonDirectories(ReadValue, is64BitOperatingSystem: true);
                Require(directories.Count == 1 && directories[0] == current, "Virtualized CommonDir was omitted, duplicated or combined with its shadowed value.");
                values.Remove((RegistryHive.CurrentUser, RegistryView.Registry64, prefix + @"WOW6432Node\" + tail));
                directories = CardCollectionReader.ReadCommonDirectories(ReadValue, is64BitOperatingSystem: true);
                Require(directories.Contains(old) && directories.Contains(current.ToUpperInvariant()), "Absent virtual value prevented machine fallback.");
                directories = CardCollectionReader.ReadCommonDirectories(ReadValue, is64BitOperatingSystem: false);
                Require(directories.Count == 1 && directories[0] == current.ToUpperInvariant(), "32-bit Windows used a WOW6432Node physical path.");
                return Task.CompletedTask;
            });
        }
        await run("damaged game card tables do not fabricate that game's count", async () =>
        {
            var badArchive = Path.Combine(directory, "invalid-data.dat");
            await File.WriteAllBytesAsync(badArchive, "synthetic invalid archive"u8.ToArray());
            var installations = games.Select(g => g.GameId == "joey" ? g with { DataPath = badArchive } : g).ToArray();
            var snapshot = await reader.ReadAsync(installations, savePath);
            Require(snapshot.UniqueCards == 5 && snapshot.Games[2].OwnedCards is null, "Invalid game data became an owned count.");
            Require(snapshot.Games[2].Cards.Count == 0 && snapshot.Games.Take(2).All(game => game.Cards.Count == game.TotalCards),
                "A damaged game fabricated metadata or removed readable games' metadata.");
        });
        await run("collection respects cancellation", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await reader.ReadAsync(games, savePath, cancellation.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Cancelled collection read completed.");
        });
        await run("collection reading does not exclude an active save writer", async () =>
        {
            using var writer = new FileStream(savePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            Require((await reader.ReadAsync(games, savePath)).UniqueCards == 7, "Reader excluded a game-side write handle.");
        });
    }

    public static async Task VerifyInstalled(string root, IReadOnlyList<GameInstallation> games)
    {
        var path = Path.Combine(root, "synthetic-full-collection.dat");
        await File.WriteAllBytesAsync(path, CollectionFixtures.Save(Enumerable.Range(0, 1115).Select(index => (index, (byte)1))));
        var snapshot = await new CardCollectionReader(_ => []).ReadAsync(games, path);
        Require(snapshot.UniqueCards == 771 && snapshot.Games.All(g => g.OwnedCards == g.TotalCards),
            "Installed pools differ from the supported cumulative 155/466/771 pools.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireUnavailable(CollectionSnapshot snapshot, string message)
        => Require(snapshot.UniqueCards is null && snapshot.Games.All(game => game.OwnedCards is null && game.Cards.Count == 0), message);
}
