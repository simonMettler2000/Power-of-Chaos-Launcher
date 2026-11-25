using System.Buffers.Binary;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Core.Collections;

internal sealed record NativeStarterCards(IReadOnlyList<int> Introduced,
    IReadOnlyDictionary<int, (byte Quantity, byte DeckFlags)> Deck)
{
    // These tables are admitted only after checking the complete executable against the launcher profile.
    internal static NativeStarterCards Read(GameInstallation game, CancellationToken token)
    {
        var (offset, ownBit) = game.GameId switch
        {
            "yugi" => (0x19DF50, 1),
            "kaiba" => (0x1A1030, 2),
            "joey" => (0x1DD9B4, 4),
            _ => throw new ArgumentException("Unknown game.")
        };
        var profile = GameModProfiles.Profiles.FirstOrDefault(item => item.GameId == game.GameId);
        TemporaryCollectionService.RequireOrdinary(game.ExecutablePath);
        using var input = new FileStream(game.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (profile is null || input.Length != profile.Length)
            throw new InvalidDataException("This game version's starter cards are unavailable.");
        var executable = new byte[profile.Length];
        input.ReadExactly(executable);
        if (!SkipSplashPatch.MatchesHash(executable, profile.OriginalHash))
            throw new InvalidDataException("This game version's starter cards are unavailable.");
        token.ThrowIfCancellationRequested();
        TemporaryCollectionService.RequireOrdinary(game.DataPath);
        var resources = GameArchive.Read(game.DataPath, [@"bin#\card_pack.bin", @"bin#\card_id.bin"], 4096, token);
        if (resources is null || resources.Any(table => table.Length != CollectionSave.CardSlots * 2))
            throw new InvalidDataException("Unsupported card tables.");
        var available = new Dictionary<ushort, int>();
        var introduced = new List<int>();
        for (var i = 0; i < CollectionSave.CardSlots; i++)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(resources[0].AsSpan(i * 2));
            if ((flags & (ownBit * 2 - 1)) == 0) continue;
            var cardId = BinaryPrimitives.ReadUInt16LittleEndian(resources[1].AsSpan(i * 2));
            if (cardId == ushort.MaxValue || !available.TryAdd(cardId, i))
                throw new InvalidDataException("Invalid card identities.");
            if ((flags & (ownBit - 1)) == 0) introduced.Add(i);
        }
        if (available.Count != CollectionSnapshot.TotalFor(game.GameId) || introduced.Count == 0)
            throw new InvalidDataException("Unsupported card pool.");

        uint Word(int index) => BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(offset + index * 4));
        var mainCount = Word(0);
        if (mainCount is < 40 or > 80) throw new InvalidDataException("Invalid starter deck.");
        var fusionCount = Word((int)mainCount + 1);
        if (fusionCount > 30) throw new InvalidDataException("Invalid starter deck.");
        var deck = new Dictionary<int, (byte Quantity, byte DeckFlags)>();
        void AddSection(int firstWord, int count, int shift)
        {
            for (var n = 0; n < count; n++)
            {
                if (!available.TryGetValue((ushort)Word(firstWord + n), out var slot))
                    throw new InvalidDataException("A starter card is missing from the installed card pool.");
                var (quantity, flags) = deck.GetValueOrDefault(slot);
                if (((flags >> shift) & 3) == 3) throw new InvalidDataException("Invalid starter deck quantity.");
                deck[slot] = ((byte)(quantity + 1), (byte)(flags + (1 << shift)));
            }
        }
        AddSection(1, (int)mainCount, 0);
        AddSection((int)mainCount + 2, (int)fusionCount, 4);
        return new NativeStarterCards(introduced, deck);
    }
}
