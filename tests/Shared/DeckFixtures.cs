using System.Buffers.Binary;
using System.Text;
using PowerOfChaos.Launcher.Core;

namespace PowerOfChaos.Launcher.TestFixtures;

// Authored synthetic data only: made-up cards, texts and decks in the shape the games' archives use.
internal static class DeckFixtures
{
    public static readonly byte[] Header = [1, 0, 0, 0, 0x11, 0x22, 0x33, 0x44];

    /// <summary>The made-up card number of a kind: every sixth card is a normal, effect, Fusion or Ritual monster, a Spell or a Trap.</summary>
    public static int KindOf(int card) => card % 6;

    public static string Text(string language, int slot) => (language == "ja" ? "説明文" : "Card text") + " " + slot + (slot % 2 == 0 ? "\nSecond line" : "");

    public static List<(string Name, byte[] Data)> Tables()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var entries = CollectionFixtures.CardTables();
        entries.AddRange(CollectionFixtures.CardNameTables());
        var properties = new byte[1115 * 4];
        for (var card = 0; card < 771; card++)
        {
            var kind = KindOf(card);
            // Monster type 1, Trap 21, Spell 22; monster kind 0..3; level, attack and defence in tens.
            var type = kind == 4 ? 22u : kind == 5 ? 21u : 1u + (uint)(card % 20);
            var word = kind >= 4 ? type << 20
                : (uint)(card % 40) | (uint)(card % 300) << 9 | (uint)kind << 18 | type << 20 | (uint)(card % 8 + 1) << 25 | (uint)(card % 6 + 1) << 29;
            BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(CollectionFixtures.Slot(card) * 4), word);
        }
        entries.Add((@"bin#\card_prop.bin", properties));
        foreach (var (language, suffix, codepage) in new[]
        {
            ("en", "eng", 1252), ("de", "ger", 1252), ("fr", "fra", 1252), ("it", "ita", 1252), ("es", "spa", 1252), ("ja", "jpn", 932)
        })
        {
            var encoding = Encoding.GetEncoding(codepage);
            var index = new byte[2048 * 4];
            using var texts = new MemoryStream();
            texts.WriteByte(0);
            for (var slot = 1; slot < 1115; slot++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(slot * 4), (uint)texts.Position);
                texts.Write(encoding.GetBytes(Text(language, slot)));
                texts.WriteByte(0);
            }
            entries.Add((@"bin#\card_indx" + suffix + ".bin", index));
            entries.Add((@"bin#\card_desc" + suffix + ".bin", texts.ToArray()));
        }
        return entries;
    }

    /// <summary>Made-up card numbers of a game's pool that are not Fusion monsters, in order.</summary>
    public static ushort[] MainCards(string gameId) => Enumerable.Range(0, PoolSize(gameId)).Where(card => KindOf(card) != 2).Select(card => (ushort)card).ToArray();

    public static ushort[] FusionCards(string gameId) => Enumerable.Range(0, PoolSize(gameId)).Where(card => KindOf(card) == 2).Select(card => (ushort)card).ToArray();

    public static int PoolSize(string gameId) => gameId switch { "yugi" => 155, "kaiba" => 466, _ => 771 };

    public static char Letter(string gameId) => gameId[0];

    public static byte[] DeckFile(IReadOnlyList<ushort> main, IReadOnlyList<ushort> fusion, IReadOnlyList<ushort> side, byte[]? header = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(header ?? Header);
        foreach (var list in new[] { main, fusion, side })
        {
            writer.Write((ushort)list.Count);
            foreach (var card in list) writer.Write(card);
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Two stock decks for a game: 40 main cards with two Fusion monsters, and 43 main cards with a side deck.</summary>
    public static List<(string Name, byte[] Data)> StockDecks(string gameId)
    {
        var main = MainCards(gameId);
        var fusion = FusionCards(gameId);
        var folder = Letter(gameId) + @"\file\";
        return
        [
            (folder + "alpha.ydc", DeckFile(main.Take(40).ToArray(), fusion.Take(2).ToArray(), [])),
            (folder + "BETA.YDC", DeckFile(main.Skip(10).Take(43).ToArray(), [], main.Skip(60).Take(5).ToArray()))
        ];
    }

    /// <summary>Replaces an installation's archive with one that holds the card tables and stock decks.</summary>
    public static void WriteArchive(GameInstallation installation, IEnumerable<(string Name, byte[] Data)>? extra = null, bool stockDecks = true)
    {
        var entries = Tables();
        if (stockDecks) entries.AddRange(StockDecks(installation.GameId));
        if (extra is not null) entries.AddRange(extra);
        File.Delete(installation.DataPath);
        CollectionFixtures.WriteArchive(installation.DataPath, entries);
    }
}
