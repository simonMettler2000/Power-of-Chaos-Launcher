using System.Buffers.Binary;

namespace PowerOfChaos.Launcher.Core.Decks;

public enum DeckCardKind { NormalMonster, EffectMonster, FusionMonster, RitualMonster, Spell, Trap }

public enum DeckSection { Main, Fusion, Side }

/// <summary>One card a game supports, as that installation's own tables describe it. Nothing here is stored by the launcher.</summary>
public sealed record DeckCard(int Slot, ushort CardId, string Name, string Text, DeckCardKind Kind, int Level, int Attack, int Defense,
    bool UsedByGame)
{
    public bool IsMonster => Kind is not (DeckCardKind.Spell or DeckCardKind.Trap);
    public bool IsFusion => Kind == DeckCardKind.FusionMonster;
}

/// <summary>A deck the game itself brings for its opponent. Read-only.</summary>
public sealed record StockOpponentDeck(string Name, IReadOnlyList<ushort> Main, IReadOnlyList<ushort> Fusion, IReadOnlyList<ushort> Side);

/// <summary>A deck the user made for a game's opponent. Cards are the external card numbers deck files use.</summary>
public sealed record OpponentDeck(string Id, string GameId, string Name, IReadOnlyList<ushort> Main, IReadOnlyList<ushort> Fusion,
    IReadOnlyList<ushort> Side, bool Enabled);

/// <summary>Why a deck cannot be used: an English sentence with numbered placeholders, and their values.</summary>
public sealed class DeckProblem(string message, params object[] arguments)
{
    public string Message { get; } = message;
    public IReadOnlyList<object> Arguments { get; } = arguments;
    public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, Message, Arguments.ToArray());
}

/// <summary>What the games accept as a deck. The sizes are those of the games' own deck storage.</summary>
public static class OpponentDeckRules
{
    public const int MinimumMain = 40;
    public const int MaximumMain = 80;
    public const int MaximumFusion = 15;
    public const int MaximumSide = 15;
    public const int MaximumCopies = 3;
    public const int MaximumNameLength = 40;
    /// <summary>The gameplay component takes at most this many deck files for one launch.</summary>
    public const int MaximumDecksInUse = 64;

    public static IReadOnlyList<DeckProblem> Check(IReadOnlyList<ushort> main, IReadOnlyList<ushort> fusion, IReadOnlyList<ushort> side,
        IReadOnlyDictionary<ushort, DeckCard> cards)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(fusion);
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(cards);
        var problems = new List<DeckProblem>();
        if (main.Count < MinimumMain) problems.Add(new("The main deck needs at least {0} cards; it has {1}.", MinimumMain, main.Count));
        if (main.Count > MaximumMain) problems.Add(new("The main deck can hold at most {0} cards; it has {1}.", MaximumMain, main.Count));
        if (fusion.Count > MaximumFusion) problems.Add(new("The Fusion deck can hold at most {0} cards; it has {1}.", MaximumFusion, fusion.Count));
        if (side.Count > MaximumSide) problems.Add(new("The side deck can hold at most {0} cards; it has {1}.", MaximumSide, side.Count));
        var unknown = main.Concat(fusion).Concat(side).Where(card => !cards.ContainsKey(card)).Distinct().Count();
        if (unknown > 0) problems.Add(new("{0} cards of this deck do not exist in this game.", unknown));
        if (main.Concat(side).Any(card => cards.TryGetValue(card, out var known) && known.IsFusion))
            problems.Add(new("Fusion monsters belong in the Fusion deck."));
        if (fusion.Any(card => cards.TryGetValue(card, out var known) && !known.IsFusion))
            problems.Add(new("Only Fusion monsters belong in the Fusion deck."));
        foreach (var group in main.Concat(fusion).Concat(side).GroupBy(card => card).Where(group => group.Count() > MaximumCopies).Take(3))
            problems.Add(new("A deck can hold at most {0} copies of {1}.", MaximumCopies,
                cards.TryGetValue(group.Key, out var known) ? known.Name : "#" + group.Key));
        return problems;
    }

    /// <summary>The name as it will be stored, or null when it cannot be used.</summary>
    public static string? CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length is 0 or > MaximumNameLength ? null : cleaned;
    }
}

/// <summary>The games' deck file: eight header bytes, then main, Fusion and side lists, each a count and that many card numbers.</summary>
internal static class OpponentDeckFile
{
    internal const int HeaderLength = 8;

    internal static byte[] Encode(ReadOnlySpan<byte> header, IReadOnlyList<ushort> main, IReadOnlyList<ushort> fusion, IReadOnlyList<ushort> side)
    {
        if (header.Length != HeaderLength) throw new ArgumentException("A deck file header has eight bytes.", nameof(header));
        if (main.Count > OpponentDeckRules.MaximumMain || fusion.Count > OpponentDeckRules.MaximumFusion || side.Count > OpponentDeckRules.MaximumSide)
            throw new ArgumentException("The deck is larger than the games' deck storage.");
        var file = new byte[HeaderLength + 6 + 2 * (main.Count + fusion.Count + side.Count)];
        header.CopyTo(file);
        var position = HeaderLength;
        foreach (var list in new[] { main, fusion, side })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(position), (ushort)list.Count);
            position += 2;
            foreach (var card in list)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(position), card);
                position += 2;
            }
        }
        return file;
    }

    /// <summary>Null for anything that is not exactly one deck within the games' sizes.</summary>
    internal static (byte[] Header, ushort[] Main, ushort[] Fusion, ushort[] Side)? Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderLength + 6) return null;
        var lists = new ushort[3][];
        var limits = new[] { OpponentDeckRules.MaximumMain, OpponentDeckRules.MaximumFusion, OpponentDeckRules.MaximumSide };
        var position = HeaderLength;
        for (var index = 0; index < 3; index++)
        {
            if (file.Length - position < 2) return null;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(file[position..]);
            position += 2;
            if (count > limits[index] || file.Length - position < count * 2) return null;
            lists[index] = new ushort[count];
            for (var card = 0; card < count; card++, position += 2)
                lists[index][card] = BinaryPrimitives.ReadUInt16LittleEndian(file[position..]);
        }
        return position == file.Length ? (file[..HeaderLength].ToArray(), lists[0], lists[1], lists[2]) : null;
    }
}

/// <summary>A deck being put together. It only ever holds cards of the game and keeps Fusion monsters in the Fusion deck.</summary>
public sealed class OpponentDeckDraft
{
    private readonly IReadOnlyDictionary<ushort, DeckCard> _cards;
    private readonly List<ushort> _main = [];
    private readonly List<ushort> _fusion = [];
    private readonly List<ushort> _side = [];

    public OpponentDeckDraft(IReadOnlyDictionary<ushort, DeckCard> cards, IEnumerable<ushort>? main = null,
        IEnumerable<ushort>? fusion = null, IEnumerable<ushort>? side = null)
    {
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        // Whatever a stored or stock deck holds is kept as it is; the rules report what is wrong with it.
        _main.AddRange((main ?? []).Take(OpponentDeckRules.MaximumMain));
        _fusion.AddRange((fusion ?? []).Take(OpponentDeckRules.MaximumFusion));
        _side.AddRange((side ?? []).Take(OpponentDeckRules.MaximumSide));
    }

    public IReadOnlyList<ushort> Main => _main;
    public IReadOnlyList<ushort> Fusion => _fusion;
    public IReadOnlyList<ushort> Side => _side;
    public bool Changed { get; private set; }

    public IReadOnlyList<ushort> Cards(DeckSection section) => List(section);

    public int Copies(ushort card) => _main.Count(item => item == card) + _fusion.Count(item => item == card) + _side.Count(item => item == card);

    public int Copies(ushort card, DeckSection section) => List(section).Count(item => item == card);

    /// <summary>Where a card goes when it is added: Fusion monsters to the Fusion deck, everything else to the asked main or side deck.</summary>
    public DeckSection SectionFor(ushort card, DeckSection asked = DeckSection.Main)
        => _cards.TryGetValue(card, out var known) && known.IsFusion ? DeckSection.Fusion
            : asked == DeckSection.Side ? DeckSection.Side : DeckSection.Main;

    public bool CanAdd(ushort card, DeckSection asked = DeckSection.Main)
    {
        if (!_cards.ContainsKey(card) || Copies(card) >= OpponentDeckRules.MaximumCopies) return false;
        var section = SectionFor(card, asked);
        return List(section).Count < Limit(section);
    }

    public bool Add(ushort card, DeckSection asked = DeckSection.Main)
    {
        if (!CanAdd(card, asked)) return false;
        List(SectionFor(card, asked)).Add(card);
        Changed = true;
        return true;
    }

    public bool Remove(ushort card, DeckSection section)
    {
        var list = List(section);
        var index = list.LastIndexOf(card);
        if (index < 0) return false;
        list.RemoveAt(index);
        Changed = true;
        return true;
    }

    public IReadOnlyList<DeckProblem> Problems => OpponentDeckRules.Check(_main, _fusion, _side, _cards);

    public void MarkSaved() => Changed = false;

    public static int Limit(DeckSection section) => section switch
    {
        DeckSection.Main => OpponentDeckRules.MaximumMain,
        DeckSection.Fusion => OpponentDeckRules.MaximumFusion,
        _ => OpponentDeckRules.MaximumSide
    };

    private List<ushort> List(DeckSection section) => section switch
    {
        DeckSection.Main => _main,
        DeckSection.Fusion => _fusion,
        _ => _side
    };
}
