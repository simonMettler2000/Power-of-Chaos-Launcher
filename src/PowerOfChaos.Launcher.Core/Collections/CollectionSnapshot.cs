namespace PowerOfChaos.Launcher.Core.Collections;

public sealed record CollectionCard(int Slot, ushort CardId, byte Quantity, string OriginGameId);

public sealed record GameCollectionProgress(string GameId, int TotalCards, int? OwnedCards, string Message)
{
    public IReadOnlyList<CollectionCard> Cards { get; init; } = [];
}

public sealed record CollectionSnapshot(IReadOnlyList<GameCollectionProgress> Games, int? UniqueCards,
    string Message, string? SavePath = null)
{
    public const int MaximumUniqueCards = 771;
    public static int TotalFor(string gameId) => gameId switch
    {
        "yugi" => 155, "kaiba" => 466, "joey" => MaximumUniqueCards,
        _ => throw new ArgumentException("Unknown game.", nameof(gameId))
    };
}
