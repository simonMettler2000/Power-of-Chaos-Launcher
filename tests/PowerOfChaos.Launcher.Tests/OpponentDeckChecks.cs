using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Decks;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;
using PowerOfChaos.Launcher.TestFixtures;

namespace PowerOfChaos.Launcher.Tests;

/// <summary>Custom opponent decks with authored synthetic cards, decks and archives. No game or component is started.</summary>
internal static class OpponentDeckChecks
{
    internal static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        await run("deck files keep header, three lists and their order, and anything else is not a deck", () =>
        {
            ushort[] main = [.. Enumerable.Range(0, 41).Select(card => (ushort)(card * 3))];
            ushort[] fusion = [7, 9];
            ushort[] side = [500, 501, 502];
            var file = OpponentDeckFile.Encode(DeckFixtures.Header, main, fusion, side);
            Require(file.SequenceEqual(DeckFixtures.DeckFile(main, fusion, side)), "The deck file differs from the games' layout.");
            var decoded = OpponentDeckFile.Decode(file) ?? throw new InvalidOperationException("A valid deck file was refused.");
            Require(decoded.Header.SequenceEqual(DeckFixtures.Header) && decoded.Main.SequenceEqual(main) && decoded.Fusion.SequenceEqual(fusion)
                && decoded.Side.SequenceEqual(side), "A deck file did not come back as it was written.");
            Require(OpponentDeckFile.Decode(OpponentDeckFile.Encode(DeckFixtures.Header, [], [], [])) is { Main.Length: 0 }, "An empty deck file was refused.");
            Require(OpponentDeckFile.Decode(file.AsSpan(0, file.Length - 1)) is null && OpponentDeckFile.Decode(file.Concat(new byte[] { 0 }).ToArray()) is null
                && OpponentDeckFile.Decode(file.AsSpan(0, 13)) is null && OpponentDeckFile.Decode([]) is null, "A cut or padded deck file was accepted.");
            var tooLong = DeckFixtures.DeckFile([.. Enumerable.Repeat((ushort)1, 81)], [], []);
            var tooManyFusion = DeckFixtures.DeckFile(main, [.. Enumerable.Repeat((ushort)1, 16)], []);
            var lyingCount = file.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(lyingCount.AsSpan(8), 60000);
            Require(OpponentDeckFile.Decode(tooLong) is null && OpponentDeckFile.Decode(tooManyFusion) is null && OpponentDeckFile.Decode(lyingCount) is null,
                "A deck larger than the games' deck storage was accepted.");
            Expect<ArgumentException>(() => OpponentDeckFile.Encode(DeckFixtures.Header, [.. Enumerable.Repeat((ushort)1, 81)], [], []));
            Expect<ArgumentException>(() => OpponentDeckFile.Encode(new byte[7], main, [], []));
            return Task.CompletedTask;
        });

        await run("deck rules require 40 to 80 main cards, three copies at most, known cards and Fusion monsters in their own deck", async () =>
        {
            var catalog = await Catalog(Path.Combine(fixtures, "dr"), "kaiba");
            var main = DeckFixtures.MainCards("kaiba");
            var fusion = DeckFixtures.FusionCards("kaiba");
            IReadOnlyList<DeckProblem> Check(IEnumerable<ushort> first, IEnumerable<ushort>? second = null, IEnumerable<ushort>? third = null)
                => OpponentDeckRules.Check(first.ToArray(), (second ?? []).ToArray(), (third ?? []).ToArray(), catalog.ById);
            Require(Check(main.Take(40)).Count == 0 && Check(main.Take(80), fusion.Take(15), main.Skip(80).Take(15)).Count == 0, "A valid deck was refused.");
            Require(Check(main.Take(39)).Single().ToString() == "The main deck needs at least 40 cards; it has 39.", "A 39-card deck was accepted.");
            Require(Check(main.Take(81)).Single().Message.StartsWith("The main deck can hold at most", StringComparison.Ordinal)
                && Check(main.Take(40), fusion.Take(16)).Single().Message.StartsWith("The Fusion deck can hold at most", StringComparison.Ordinal)
                && Check(main.Take(40), null, main.Skip(40).Take(16)).Single().Message.StartsWith("The side deck can hold at most", StringComparison.Ordinal),
                "An oversized part of a deck was accepted.");
            Require(Check(main.Take(39).Append(fusion[0])).Single().Message == "Fusion monsters belong in the Fusion deck."
                && Check(main.Take(40), null, [fusion[0]]).Single().Message == "Fusion monsters belong in the Fusion deck."
                && Check(main.Take(40), [main[50]]).Single().Message == "Only Fusion monsters belong in the Fusion deck.", "Fusion monsters were accepted in the wrong deck.");
            Require(Check(main.Take(37).Concat(Enumerable.Repeat(main[0], 3))).Single().ToString() == "A deck can hold at most 3 copies of " + catalog.ById[main[0]].Name + "."
                && Check(main.Take(38).Append(main[0]).Append(main[0])).Count == 0
                && Check(main.Take(39).Append(main[0]), null, [main[0], main[0]]).Count == 1, "The copy limit does not count all three parts together.");
            Require(Check(main.Take(38).Append((ushort)60000).Append((ushort)60001)).Single().ToString() == "2 cards of this deck do not exist in this game.",
                "Unknown cards were accepted.");
            Require(OpponentDeckRules.CleanName("  My deck \n") == "My deck" && OpponentDeckRules.CleanName("") is null && OpponentDeckRules.CleanName(" \t ") is null
                && OpponentDeckRules.CleanName(null) is null && OpponentDeckRules.CleanName(new string('x', 40)) is not null
                && OpponentDeckRules.CleanName(new string('x', 41)) is null, "Deck names are cleaned wrongly.");
        });

        await run("a deck in the making keeps Fusion monsters apart, counts copies across its parts and reports what is missing", async () =>
        {
            var catalog = await Catalog(Path.Combine(fixtures, "dd"), "yugi");
            var main = DeckFixtures.MainCards("yugi");
            var fusion = DeckFixtures.FusionCards("yugi");
            var draft = new OpponentDeckDraft(catalog.ById);
            Require(!draft.Changed && draft.Problems.Count == 1 && draft.Main.Count == 0, "An empty draft is wrong.");
            Require(draft.Add(main[0]) && draft.Add(main[0]) && draft.Add(main[0], DeckSection.Side) && !draft.CanAdd(main[0]) && !draft.Add(main[0])
                && draft.Copies(main[0]) == 3 && draft.Copies(main[0], DeckSection.Main) == 2 && draft.Side.Count == 1 && draft.Changed,
                "Copies are not counted across the parts of a deck.");
            Require(draft.SectionFor(fusion[0]) == DeckSection.Fusion && draft.SectionFor(fusion[0], DeckSection.Side) == DeckSection.Fusion
                && draft.Add(fusion[0], DeckSection.Side) && draft.Fusion.SequenceEqual(new[] { fusion[0] }) && draft.Side.Count == 1,
                "A Fusion monster did not go to the Fusion deck.");
            Require(!draft.Add(60000) && !draft.CanAdd(60000), "A card outside the game was added.");
            Require(draft.Remove(main[0], DeckSection.Side) && !draft.Remove(main[0], DeckSection.Side) && draft.Copies(main[0]) == 2
                && !draft.Remove(fusion[0], DeckSection.Main) && draft.Remove(fusion[0], DeckSection.Fusion), "Removing cards is wrong.");
            foreach (var card in main.Skip(1).Take(38)) Require(draft.Add(card), "A card could not be added.");
            Require(draft.Main.Count == 40 && draft.Problems.Count == 0, "Forty cards do not make a deck.");
            draft.MarkSaved();
            Require(!draft.Changed && draft.Remove(main[1], DeckSection.Main) && draft.Changed && draft.Problems.Count == 1, "Changes are not tracked.");
            var full = new OpponentDeckDraft(catalog.ById, main.Take(80), fusion.Take(15), main.Skip(80).Take(15));
            Require(!full.CanAdd(main[100]) && !full.CanAdd(main[100], DeckSection.Side) && !full.CanAdd(fusion[20]) && full.Problems.Count == 0,
                "A full deck took another card.");
            var oversized = new OpponentDeckDraft(catalog.ById, Enumerable.Repeat(main[0], 200), Enumerable.Repeat(fusion[0], 200), null);
            Require(oversized.Main.Count == 80 && oversized.Fusion.Count == 15, "A draft grew past the games' deck storage.");
        });

        await run("the user's decks are stored beside the settings, survive a restart and are cleaned on the way", async () =>
        {
            var directory = Path.Combine(fixtures, "ds");
            var store = new OpponentDeckStore(directory);
            Require((await store.LoadAsync()).Count == 0 && !Directory.Exists(directory), "A missing deck file is not an empty list.");
            ushort[] main = [.. Enumerable.Range(0, 40).Select(card => (ushort)card)];
            await store.SaveAsync(
            [
                new OpponentDeck("first", "yugi", "  First deck ", main, [2], [], true),
                new OpponentDeck("first", "kaiba", "Second", main, [], [5, 6], false),
                new OpponentDeck("bad id!", "joey", "Third", [.. Enumerable.Repeat((ushort)1, 200)], [.. Enumerable.Repeat((ushort)2, 200)], [.. Enumerable.Repeat((ushort)3, 200)], true),
                new OpponentDeck("x", "unknown", "Other game", main, [], [], true),
                new OpponentDeck("y", "yugi", " ", main, [], [], true)
            ]);
            var loaded = await new OpponentDeckStore(directory).LoadAsync();
            Require(loaded.Count == 3 && loaded[0] is { Id: "first", GameId: "yugi", Name: "First deck", Enabled: true } && loaded[0].Main.SequenceEqual(main)
                && loaded[0].Fusion.SequenceEqual(new ushort[] { 2 }), "The first deck did not come back.");
            Require(loaded[1].Id != "first" && loaded[1] is { GameId: "kaiba", Enabled: false } && loaded[1].Side.SequenceEqual(new ushort[] { 5, 6 }),
                "A repeated identifier was kept.");
            Require(loaded[2].Id != "bad id!" && loaded[2].Main.Count == 80 && loaded[2].Fusion.Count == 15 && loaded[2].Side.Count == 15, "An oversized deck was stored.");
            Require(Directory.GetFiles(directory).Select(Path.GetFileName).SequenceEqual(new[] { OpponentDeckStore.FileName }), "Temporary files were left behind.");
            await File.WriteAllTextAsync(Path.Combine(directory, OpponentDeckStore.FileName), "{ not json");
            await ExpectAsync<InvalidDataException>(() => new OpponentDeckStore(directory).LoadAsync());
            await File.WriteAllTextAsync(Path.Combine(directory, OpponentDeckStore.FileName), "{\"SchemaVersion\":2,\"Decks\":[]}");
            await ExpectAsync<InvalidDataException>(() => new OpponentDeckStore(directory).LoadAsync());
            await File.WriteAllTextAsync(Path.Combine(directory, OpponentDeckStore.FileName), "{\"SchemaVersion\":1,\"Decks\":[null,{\"GameId\":\"yugi\",\"Name\":\"Bare\"}]}");
            Require(await new OpponentDeckStore(directory).LoadAsync() is [{ Name: "Bare", Main.Count: 0, Enabled: false }], "A bare stored deck was not read.");
        });

        await run("a game's cards and its own decks are read from its archive: pool, kinds, values, names and texts in the chosen language", async () =>
        {
            foreach (var (gameId, pool) in new[] { ("yugi", 155), ("kaiba", 466), ("joey", 771) })
            {
                var catalog = await Catalog(Path.Combine(fixtures, "dc-" + gameId), gameId);
                Require(catalog.GameId == gameId && catalog.Cards.Count == pool && catalog.ById.Count == pool, gameId + " has the wrong pool.");
                Require(catalog.StockDecks.Count == 2 && catalog.StockDecks[0] is { Name: "alpha", Main.Count: 40, Fusion.Count: 2, Side.Count: 0 }
                    && catalog.StockDecks[1] is { Name: "BETA", Main.Count: 43, Side.Count: 5 } && catalog.Header.SequenceEqual(DeckFixtures.Header),
                    gameId + " lists the wrong stock decks.");
                Require(catalog.ById[DeckFixtures.MainCards(gameId)[0]].UsedByGame && catalog.ById[DeckFixtures.FusionCards(gameId)[1]].UsedByGame
                    && !catalog.ById[DeckFixtures.FusionCards(gameId)[2]].UsedByGame && !catalog.ById[(ushort)(pool - 1)].UsedByGame,
                    gameId + " marks the wrong cards as used by its own decks.");
            }
            var joey = await Catalog(Path.Combine(fixtures, "dc-ja"), "joey", "ja");
            for (var card = 0; card < 12; card++)
            {
                var known = joey.ById[(ushort)card];
                var slot = CollectionFixtures.Slot(card);
                var kind = DeckFixtures.KindOf(card);
                Require(known.Slot == slot && known.Name == CollectionFixtures.CardName("ja", slot) && known.Text == DeckFixtures.Text("ja", slot)
                    && (int)known.Kind == kind, "Card " + card + " was read wrongly.");
                Require(known.IsFusion == (kind == 2) && known.IsMonster == (kind < 4)
                    && (kind < 4 ? known.Level == card % 8 + 1 && known.Attack == card % 300 * 10 && known.Defense == card % 40 * 10
                        : known is { Level: 0, Attack: 0, Defense: 0 }), "Card " + card + " has the wrong values.");
            }
            // A launch reads no names or texts and so does not depend on any language table.
            var bare = await SplashChecks.Installation(Path.Combine(fixtures, "dc-bare"), "kaiba", SplashChecks.SyntheticImage());
            File.Delete(bare.DataPath);
            var withoutLanguages = DeckFixtures.Tables().Where(entry => !entry.Name.Contains("card_name", StringComparison.Ordinal)
                && !entry.Name.Contains("card_indx", StringComparison.Ordinal) && !entry.Name.Contains("card_desc", StringComparison.Ordinal)).ToList();
            withoutLanguages.AddRange(DeckFixtures.StockDecks("kaiba"));
            CollectionFixtures.WriteArchive(bare.DataPath, withoutLanguages);
            var forLaunch = await new OpponentDeckCatalogReader().LoadForLaunchAsync(bare);
            Require(forLaunch.Cards.Count == 466 && forLaunch.StockDecks.Count == 2 && forLaunch.Header.SequenceEqual(DeckFixtures.Header)
                && forLaunch.ById[2] is { IsFusion: true, Name: "#2", Text: "" }, "The launch catalog needs language tables or lost its card kinds.");
            await ExpectAsync<InvalidDataException>(() => new OpponentDeckCatalogReader().LoadAsync(bare, "en"));
            Require(OpponentDeckCatalogReader.Kind(21u << 20) == DeckCardKind.Trap && OpponentDeckCatalogReader.Kind(22u << 20 | 3u << 18) == DeckCardKind.Spell
                && OpponentDeckCatalogReader.Kind(15u << 20 | 3u << 18) == DeckCardKind.RitualMonster, "Card kinds are decoded wrongly.");
        });

        await run("decks elsewhere in the archive, damaged decks and missing card tables are handled without guessing", async () =>
        {
            var game = await SplashChecks.Installation(Path.Combine(fixtures, "dx"), "yugi", SplashChecks.SyntheticImage());
            var main = DeckFixtures.MainCards("yugi").Take(40).ToArray();
            DeckFixtures.WriteArchive(game,
            [
                (@"y\file\sub\deep.ydc", DeckFixtures.DeckFile(main, [], [])),
                (@"k\file\other.ydc", DeckFixtures.DeckFile(main, [], [])),
                (@"y\file\notes.txt", Encoding.ASCII.GetBytes("not a deck")),
                (@"y\file\broken.ydc", [1, 2, 3]),
                (@"y\file\huge.ydc", new byte[20000]),
                (@"y\file\aaa.ydc", DeckFixtures.DeckFile(main, [], [], [9, 9, 9, 9, 9, 9, 9, 9]))
            ]);
            var catalog = await new OpponentDeckCatalogReader().LoadAsync(game, "en");
            Require(catalog.StockDecks.Select(deck => deck.Name).SequenceEqual(new[] { "aaa", "alpha", "BETA" }), "The wrong files count as the game's decks.");
            Require(catalog.Header.SequenceEqual(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }), "The pattern header is not that of the first deck.");
            // A text table without terminators gives bounded texts, not one table-sized text for every card.
            var endless = DeckFixtures.Tables().Where(entry => !entry.Name.EndsWith("card_desceng.bin", StringComparison.Ordinal)
                && !entry.Name.EndsWith("card_indxeng.bin", StringComparison.Ordinal)).ToList();
            endless.Add((@"bin#\card_desceng.bin", Enumerable.Repeat((byte)'x', 100_000).ToArray()));
            endless.Add((@"bin#\card_indxeng.bin", new byte[2048 * 4]));
            File.Delete(game.DataPath);
            CollectionFixtures.WriteArchive(game.DataPath, endless);
            var bounded = await new OpponentDeckCatalogReader().LoadAsync(game, "en");
            Require(bounded.Cards.Count == 155 && bounded.Cards.All(card => card.Text.Length == 4096), "Card texts are not bounded.");
            DeckFixtures.WriteArchive(game, stockDecks: false);
            var bare = await new OpponentDeckCatalogReader().LoadAsync(game, "en");
            Require(bare.StockDecks.Count == 0 && bare.Header.Length == 0 && bare.Cards.Count == 155, "An archive without decks was read wrongly.");
            File.Delete(game.DataPath);
            CollectionFixtures.WriteArchive(game.DataPath);
            await ExpectAsync<InvalidDataException>(() => new OpponentDeckCatalogReader().LoadAsync(game, "en"));
            await ExpectAsync<ArgumentException>(() => new OpponentDeckCatalogReader().LoadAsync(game with { GameId = "other" }, "en"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => new OpponentDeckCatalogReader().LoadAsync(game, "en", cancelled.Token));
        });

        await run("Custom opponent decks hands the ticked decks of that game to the component and nothing else", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "dl"));
            var main = DeckFixtures.MainCards("kaiba");
            var fusion = DeckFixtures.FusionCards("kaiba");
            var options = new GameModOptions(CustomOpponentDecks: true);
            // With no deck ticked the component is there but has nothing to do.
            var idle = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options))!;
            Require(await File.ReadAllTextAsync(Path.Combine(idle, "pocgp.ini")) == "new_card_rewards=0\n" && !Directory.EnumerateFiles(idle, "*.ydc").Any(),
                "Without ticked decks something was handed over.");
            await new OpponentDeckStore(fixture.Data).SaveAsync(
            [
                new OpponentDeck("a", "kaiba", "First", main.Take(40).ToArray(), fusion.Take(1).ToArray(), [], true),
                new OpponentDeck("b", "kaiba", "Not ticked", main.Take(41).ToArray(), [], [], false),
                new OpponentDeck("c", "yugi", "Other game", main.Take(40).ToArray(), [], [], true),
                new OpponentDeck("d", "kaiba", "Second", main.Skip(5).Take(60).ToArray(), [], main.Take(3).ToArray(), true)
            ]);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var directory = Path.GetDirectoryName(path)!;
            Require(directory != idle && Hash(await File.ReadAllBytesAsync(path)) == Hash(fixture.Original), "The decks did not get their own private directory.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "pocgp.ini"))
                == "new_card_rewards=0\nopponent_deck=opponent-deck-01.ydc\nopponent_deck=opponent-deck-02.ydc\n", "Wrong component settings.");
            Require((await File.ReadAllBytesAsync(Path.Combine(directory, "opponent-deck-01.ydc"))).SequenceEqual(DeckFixtures.DeckFile(main.Take(40).ToArray(), fusion.Take(1).ToArray(), []))
                && (await File.ReadAllBytesAsync(Path.Combine(directory, "opponent-deck-02.ydc"))).SequenceEqual(DeckFixtures.DeckFile(main.Skip(5).Take(60).ToArray(), [], main.Take(3).ToArray()))
                && Directory.EnumerateFiles(directory, "*.ydc").Count() == 2, "The deck files are not the ticked decks in the games' layout.");
            Require(Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "msacm32.dll"))) == fixture.Library.Sha256, "The component is missing beside the decks.");
            Require(!Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Game.ExecutablePath)!, "*.ydc").Any()
                && !File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Game.ExecutablePath)!, "msacm32.dll")), "The installation was changed.");
            // A deck file changed in the cache is put right; both mods together share one component and one settings file.
            await File.WriteAllBytesAsync(Path.Combine(directory, "opponent-deck-01.ydc"), [1, 2, 3]);
            Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                && (await File.ReadAllBytesAsync(Path.Combine(directory, "opponent-deck-01.ydc"))).Length > 80, "A changed deck file was launched.");
            var both = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { NewCardRewards = true }))!;
            Require(both != directory && (await File.ReadAllTextAsync(Path.Combine(both, "pocgp.ini"))).StartsWith("new_card_rewards=1\nopponent_deck=", StringComparison.Ordinal),
                "Rewards and decks do not combine.");
            var rewardsOnly = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(NewCardRewards: true)))!;
            Require(rewardsOnly != both && !Directory.EnumerateFiles(rewardsOnly, "*.ydc").Any(), "Decks were handed over without the mod.");
            // With a display mod the decks ride in the display cache, which holds exactly the expected files.
            var display = Path.GetDirectoryName(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { RestoreOriginalAspectRatio = true }))!;
            Require(File.Exists(Path.Combine(display, "ddraw.dll")) && Directory.EnumerateFiles(display, "*.ydc").Count() == 2
                && (await File.ReadAllTextAsync(Path.Combine(display, "pocgp.ini"))).Contains("opponent_deck=opponent-deck-02.ydc\n", StringComparison.Ordinal),
                "The display cache does not hold the decks.");
            await File.WriteAllBytesAsync(Path.Combine(display, "opponent-deck-03.ydc"), [1]);
            await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { RestoreOriginalAspectRatio = true });
            Require(!File.Exists(Path.Combine(display, "opponent-deck-03.ydc")), "A stray deck file survived in the display cache.");
            var plan = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { CustomOpponentDecks = true }, path, isWindows: true);
            Require(plan.FileName == path, "The launch plan refuses the directory of a deck launch.");
            Expect<InvalidDataException>(() => LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), path, isWindows: true));
        });

        await run("a deck that does not fit the game, an unreadable deck file or an unusable path stops the launch before anything is prepared", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "dv"));
            var main = DeckFixtures.MainCards("kaiba");
            var options = new GameModOptions(CustomOpponentDecks: true);
            var store = new OpponentDeckStore(fixture.Data);
            foreach (var bad in new[]
            {
                new OpponentDeck("a", "kaiba", "Too small", main.Take(39).ToArray(), [], [], true),
                new OpponentDeck("a", "kaiba", "Foreign card", main.Take(39).Append((ushort)700).ToArray(), [], [], true),
                new OpponentDeck("a", "kaiba", "Four copies", main.Take(36).Concat(Enumerable.Repeat(main[0], 4)).ToArray(), [], [], true),
                new OpponentDeck("a", "kaiba", "Fusion in main", main.Take(39).Append(DeckFixtures.FusionCards("kaiba")[0]).ToArray(), [], [], true)
            })
            {
                await store.SaveAsync([new OpponentDeck("ok", "kaiba", "Fine", main.Take(40).ToArray(), [], [], true), bad]);
                try { await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options); throw new InvalidOperationException("Launched with: " + bad.Name); }
                catch (InvalidDataException error) { Require(error.Message.Contains("\"" + bad.Name + "\"", StringComparison.Ordinal), "The refusal does not name the deck."); }
            }
            Require(!Directory.Exists(Path.Combine(fixture.Data, "mod-cache")), "A refused deck launch published a cache.");
            // The same decks are no obstacle while the mod is off, or when they are not ticked.
            await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(SkipSplashScreens: true));
            await store.SaveAsync([new OpponentDeck("a", "kaiba", "Too small", main.Take(39).ToArray(), [], [], false)]);
            await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            await File.WriteAllTextAsync(Path.Combine(fixture.Data, OpponentDeckStore.FileName), "{ not json");
            await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
            // Without the game's own decks there is no pattern for a deck file.
            await store.SaveAsync([new OpponentDeck("ok", "kaiba", "Fine", main.Take(40).ToArray(), [], [], true)]);
            DeckFixtures.WriteArchive(fixture.Game, stockDecks: false);
            await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
            // The component opens deck files by an ANSI path: a data folder outside ASCII is refused, but only when decks are in use.
            var other = await Fixture.Create(Path.Combine(fixtures, "dv-ü"));
            await other.Preparer.PrepareExecutableAsync(other.Game, new GameModOptions(NewCardRewards: true, CustomOpponentDecks: true));
            await new OpponentDeckStore(other.Data).SaveAsync([new OpponentDeck("ok", "kaiba", "Fine", main.Take(40).ToArray(), [], [], true)]);
            await ExpectAsync<InvalidDataException>(() => other.Preparer.PrepareExecutableAsync(other.Game, options));
        });

        await run("Custom opponent decks is saved with the settings and classified as a gameplay mod", async () =>
        {
            var directory = Path.Combine(fixtures, "dp");
            var settings = await new SettingsStore(directory).LoadAsync();
            Require(!settings.CustomOpponentDecks, "Custom opponent decks must be off by default.");
            settings.CustomOpponentDecks = true;
            await new SettingsStore(directory).SaveAsync(settings);
            var loaded = await new SettingsStore(directory).LoadAsync();
            Require(loaded.CustomOpponentDecks && !loaded.NewCardRewards, "The selection was not saved on its own.");
            var options = new GameModOptions(CustomOpponentDecks: true);
            Require(options.UsesGameplayComponent && options.AnyEnabled && !options.BinaryModsEnabled && !options.UsesDisplayComponent,
                "The selection is classified wrongly.");
            Require(GameplayComponents.CreateSettings(new GameModOptions(NewCardRewards: true), ["a.ydc", "b.ydc"]) == "new_card_rewards=1\nopponent_deck=a.ydc\nopponent_deck=b.ydc\n",
                "The component settings are written wrongly.");
        });
    }

    private static async Task<OpponentDeckCatalog> Catalog(string root, string gameId, string language = "en")
    {
        var game = await SplashChecks.Installation(root, gameId, SplashChecks.SyntheticImage());
        DeckFixtures.WriteArchive(game);
        return await new OpponentDeckCatalogReader().LoadAsync(game, language);
    }

    private sealed record Fixture(string Root, string Data, GameInstallation Game, byte[] Original, DisplayComponent Library, GameLaunchPreparer Preparer)
    {
        internal static async Task<Fixture> Create(string root, string id = "kaiba")
        {
            var image = SplashChecks.SyntheticImage(); image[0x230] = 7;
            var splash = image.ToArray(); Write32(splash, 0x210, 476); Write32(splash, 0x220, 1);
            SplashProfile[] profiles = [new(id, image.Length, Hash(image), Hash(splash), [new(0x1010, 0x210, 4294967292, 476), new(0x1020, 0x220, 0, 1)])];
            var game = await SplashChecks.Installation(root, id, image);
            DeckFixtures.WriteArchive(game);
            // Authored inert DLL-shaped headers only; no entry point, executable section or machine instructions.
            var ddraw = SplashChecks.SyntheticImage(); BinaryPrimitives.WriteUInt16LittleEndian(ddraw.AsSpan(0x96, 2), 0x2002);
            var winmm = ddraw.ToArray(); winmm[^1] = 1;
            var gameplay = ddraw.ToArray(); gameplay[^1] = 3;
            var notices = OriginalAspectRatioComponents.Required.Where(component => !component.IsDll).ToDictionary(component => component.FileName,
                component => Encoding.UTF8.GetBytes("Authored synthetic notice fixture: " + component.FileName + "\n" + new string('L', 256)));
            var files = new Dictionary<string, byte[]>(notices) { ["ddraw.dll"] = ddraw, ["winmm.dll"] = winmm };
            Directory.CreateDirectory(Path.Combine(root, "c"));
            foreach (var file in files) await File.WriteAllBytesAsync(Path.Combine(root, "c", file.Key), file.Value);
            var components = files.Select(pair => new DisplayComponent(pair.Key, pair.Value.Length, Hash(pair.Value), pair.Key.EndsWith(".dll", StringComparison.Ordinal))).ToArray();
            Directory.CreateDirectory(Path.Combine(root, "g"));
            await File.WriteAllBytesAsync(Path.Combine(root, "g", "msacm32.dll"), gameplay);
            var library = new DisplayComponent("msacm32.dll", gameplay.Length, Hash(gameplay), true);
            var data = Path.Combine(root, "d");
            return new(root, data, game, image, library,
                new GameLaunchPreparer(data, profiles, Path.Combine(root, "c"), components, null, null, null, Path.Combine(root, "g"), library));
        }
    }

    private static void Write32(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
