using System.Text.Json;

namespace PowerOfChaos.Launcher.Core.Decks;

/// <summary>Keeps the user's opponent decks in the launcher's own data folder. Never touches game files.</summary>
public sealed class OpponentDeckStore
{
    public const string FileName = "opponent-decks.json";
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    public const int MaximumDecks = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _directory;

    public OpponentDeckStore(string? directory = null) => _directory = Path.GetFullPath(directory ?? LauncherPaths.DataDirectory);

    /// <summary>All stored decks. A missing file is an empty list; an unreadable one throws rather than hiding the user's decks.</summary>
    public async Task<IReadOnlyList<OpponentDeck>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_directory, FileName);
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("The opponent deck file is too large.");
            StoredDecks? stored;
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                try { stored = await JsonSerializer.DeserializeAsync<StoredDecks>(input, JsonOptions, cancellationToken).ConfigureAwait(false); }
                catch (JsonException error) { throw new InvalidDataException("The opponent deck file could not be read.", error); }
            }
            if (stored is null || stored.SchemaVersion != 1) throw new InvalidDataException("The opponent deck file has an unsupported format.");
            return Sanitize(stored.Decks ?? []);
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyList<OpponentDeck> decks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decks);
        var stored = new StoredDecks { SchemaVersion = 1, Decks = Sanitize(decks).ToList() };
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileName);
            var temporary = Path.Combine(_directory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Keeps what can be a deck of a known game; sizes are cut to what the games can store.</summary>
    internal static IReadOnlyList<OpponentDeck> Sanitize(IEnumerable<OpponentDeck?> decks)
    {
        var result = new List<OpponentDeck>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deck in decks.Take(MaximumDecks))
        {
            if (deck is null || GameCatalog.All.All(game => game.Id != deck.GameId)) continue;
            var name = OpponentDeckRules.CleanName(deck.Name);
            if (name is null) continue;
            var identifier = deck.Id is { Length: > 0 and <= 64 } given && given.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
                ? given : Guid.NewGuid().ToString("N");
            if (!identifiers.Add(identifier)) identifier = Guid.NewGuid().ToString("N");
            identifiers.Add(identifier);
            result.Add(new OpponentDeck(identifier, deck.GameId, name,
                (deck.Main ?? []).Take(OpponentDeckRules.MaximumMain).ToArray(),
                (deck.Fusion ?? []).Take(OpponentDeckRules.MaximumFusion).ToArray(),
                (deck.Side ?? []).Take(OpponentDeckRules.MaximumSide).ToArray(), deck.Enabled));
        }
        return result;
    }

    private sealed class StoredDecks
    {
        public int SchemaVersion { get; set; } = 1;
        public List<OpponentDeck>? Decks { get; set; }
    }
}
