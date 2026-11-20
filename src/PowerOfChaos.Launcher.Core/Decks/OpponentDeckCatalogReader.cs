using System.Buffers.Binary;
using System.Text;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Core.Decks;

/// <summary>The cards a game supports and the decks it brings for its opponent, read from that installation.</summary>
public sealed record OpponentDeckCatalog(string GameId, IReadOnlyList<DeckCard> Cards, IReadOnlyDictionary<ushort, DeckCard> ById,
    IReadOnlyList<StockOpponentDeck> StockDecks)
{
    /// <summary>The first eight bytes of the game's first stock deck. The games' own decks differ in them; custom deck files take these.</summary>
    internal byte[] Header { get; init; } = [];
}

/// <summary>Reads card tables and stock decks from an installation's archive, in memory, without extracting or caching game data.</summary>
public sealed class OpponentDeckCatalogReader
{
    private const int SlotCount = 1115;
    private const int NameBytes = 64;
    private const int TextIndexEntries = 2048;
    private const int MaximumTableBytes = 512 * 1024;
    private const int MaximumStockDecks = 256;
    private const int MaximumTextBytes = 4096;

    static OpponentDeckCatalogReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public Task<OpponentDeckCatalog> LoadAsync(GameInstallation installation, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = CardLanguages.Get(language);
        return Task.Run(() => Load(installation, selected, cancellationToken), cancellationToken);
    }

    /// <summary>The same without names and texts, which a launch does not need: only what decides whether a deck fits the game.</summary>
    internal Task<OpponentDeckCatalog> LoadForLaunchAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Load(installation, null, cancellationToken), cancellationToken);
    }

    /// <summary>The archive folder letter and the pool bits of a game. A later game also supports the earlier games' cards.</summary>
    internal static (char Letter, int PoolMask) Describe(string gameId) => gameId switch
    {
        "yugi" => ('y', 1),
        "kaiba" => ('k', 3),
        "joey" => ('j', 7),
        _ => throw new ArgumentException("Unknown game.", nameof(gameId))
    };

    private static OpponentDeckCatalog Load(GameInstallation installation, CardLanguage? language, CancellationToken token)
    {
        var (letter, poolMask) = Describe(installation.GameId);
        RequireOrdinaryFile(installation.DataPath, token);
        string[] wanted = [@"bin#\card_pack.bin", @"bin#\card_id.bin", @"bin#\card_prop.bin"];
        if (language is not null)
            wanted = [.. wanted, @"bin#\card_name" + language.ResourceCode + ".bin", @"bin#\card_indx" + language.ResourceCode + ".bin",
                @"bin#\card_desc" + language.ResourceCode + ".bin"];
        var tables = GameArchive.Read(installation.DataPath, wanted, MaximumTableBytes, token)
            ?? throw new InvalidDataException("The card tables of this installation could not be read.");
        var (pack, ids, properties) = (tables[0], tables[1], tables[2]);
        var (names, textIndex, texts) = language is null ? (null, null, null) : (tables[3], tables[4], tables[5]);
        if (pack.Length != SlotCount * 2 || ids.Length != SlotCount * 2 || properties.Length != SlotCount * 4
            || (names is not null && (names.Length != SlotCount * NameBytes || textIndex!.Length != TextIndexEntries * 4 || texts!.Length == 0)))
            throw new InvalidDataException("The card tables of this installation have an unexpected size.");

        // Stock decks: every deck file directly in the game's own archive folder.
        var prefix = letter + @"\file\";
        var stock = new List<StockOpponentDeck>();
        byte[] header = [];
        using (var archive = GameArchive.OpenBatch(installation.DataPath, token))
        {
            var deckNames = archive.Names(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".ydc", StringComparison.OrdinalIgnoreCase)
                && name.IndexOf('\\', prefix.Length) < 0).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(MaximumStockDecks).ToArray();
            foreach (var name in deckNames)
            {
                token.ThrowIfCancellationRequested();
                byte[]? payload;
                // An entry that is listed twice, empty or far too large for a deck is not one; the other decks still are.
                try { payload = archive.ReadSelected([name], token)[0]; }
                catch (InvalidDataException) { continue; }
                if (payload is null || OpponentDeckFile.Decode(payload) is not { } deck) continue;
                if (header.Length == 0) header = deck.Header;
                stock.Add(new StockOpponentDeck(name[prefix.Length..^4], deck.Main, deck.Fusion, deck.Side));
            }
        }
        var used = stock.SelectMany(deck => deck.Main.Concat(deck.Fusion).Concat(deck.Side)).ToHashSet();

        var encoding = language is null ? null
            : Encoding.GetEncoding(language.CodePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var cards = new List<DeckCard>();
        var byId = new Dictionary<ushort, DeckCard>();
        for (var slot = 1; slot < SlotCount; slot++)
        {
            token.ThrowIfCancellationRequested();
            if ((BinaryPrimitives.ReadUInt16LittleEndian(pack.AsSpan(slot * 2)) & poolMask) == 0) continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(ids.AsSpan(slot * 2));
            var word = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(slot * 4));
            var kind = Kind(word);
            var monster = kind is not (DeckCardKind.Spell or DeckCardKind.Trap);
            var name = names is null ? "" : Clean(Terminated(names.AsSpan(slot * NameBytes, NameBytes), encoding!), keepLines: false);
            var card = new DeckCard(slot, id, name.Length > 0 ? name : "#" + id, names is null ? "" : Text(textIndex!, texts!, slot, encoding!), kind,
                monster ? (int)(word >> 25 & 15) : 0, monster ? (int)(word >> 9 & 511) * 10 : 0, monster ? (int)(word & 511) * 10 : 0,
                used.Contains(id));
            // Two slots never share a card number within one game's pool; if a table says otherwise the first one stands.
            if (byId.TryAdd(id, card)) cards.Add(card);
        }
        return new OpponentDeckCatalog(installation.GameId, cards, byId, stock) { Header = header };
    }

    /// <summary>Card kind from the games' property word: a type number (21 Trap, 22 Spell, else a monster type) and a monster kind.</summary>
    internal static DeckCardKind Kind(uint word) => (word >> 20 & 31) switch
    {
        21 => DeckCardKind.Trap,
        22 => DeckCardKind.Spell,
        _ => (word >> 18 & 3) switch
        {
            1 => DeckCardKind.EffectMonster,
            2 => DeckCardKind.FusionMonster,
            3 => DeckCardKind.RitualMonster,
            _ => DeckCardKind.NormalMonster
        }
    };

    private static string Text(byte[] index, byte[] texts, int slot, Encoding encoding)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(slot * 4));
        if (offset >= texts.Length) return "";
        // A card text is a few lines; a table without terminators must not turn every card into the whole table.
        var text = texts.AsSpan((int)offset);
        return Clean(Terminated(text.Length > MaximumTextBytes ? text[..MaximumTextBytes] : text, encoding), keepLines: true);
    }

    private static string Terminated(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var end = bytes.IndexOf((byte)0);
        return encoding.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static string Clean(string value, bool keepLines)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(character == '\n' && keepLines ? '\n' : char.IsControl(character) ? ' ' : character);
        return builder.ToString().Trim();
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

/// <summary>Turns the decks the user ticked for a game into the files handed to the gameplay component.</summary>
internal static class OpponentDeckLaunch
{
    internal const string FilePrefix = "opponent-deck-";
    internal const string InvalidDeckMessage = "The opponent deck \"{0}\" cannot be used in this game. Open Opponent decks in Mods to correct it, or untick Custom opponent decks.";

    /// <summary>File name to deck file content, one for each deck in use. Every deck is checked against the game's own cards first.</summary>
    internal static async Task<IReadOnlyList<KeyValuePair<string, byte[]>>> BuildAsync(GameInstallation installation,
        IReadOnlyList<OpponentDeck> decks, CancellationToken cancellationToken)
    {
        var inUse = decks.Where(deck => deck.Enabled && deck.GameId == installation.GameId).Take(OpponentDeckRules.MaximumDecksInUse).ToArray();
        if (inUse.Length == 0) return [];
        var catalog = await new OpponentDeckCatalogReader().LoadForLaunchAsync(installation, cancellationToken).ConfigureAwait(false);
        if (catalog.Header.Length != OpponentDeckFile.HeaderLength)
            throw new InvalidDataException("Custom opponent decks need the game's own decks as a pattern, and this installation has none. Untick Custom opponent decks to play.");
        var files = new List<KeyValuePair<string, byte[]>>();
        foreach (var deck in inUse)
        {
            if (OpponentDeckRules.Check(deck.Main, deck.Fusion, deck.Side, catalog.ById).Count > 0)
                throw new InvalidDataException(string.Format(InvalidDeckMessage, deck.Name));
            files.Add(new(FilePrefix + (files.Count + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".ydc",
                OpponentDeckFile.Encode(catalog.Header, deck.Main, deck.Fusion, deck.Side)));
        }
        return files;
    }
}
