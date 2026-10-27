namespace PowerOfChaos.HdCards;

public enum CardGame { Yugi, Kaiba, Joey }
public enum CardSide { Front, Back }
public readonly record struct CardArtKey(CardGame Game, string CardId, string ArtId, CardSide Side);

public sealed record HdCardPackOptions
{
    /// <summary>Trusted caller selection; never read from the pack manifest.</summary>
    public string? AssetRootDirectory { get; init; }
    public long MaxCacheBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumPngBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumManifestBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumEntries { get; init; } = 10000;
}

/// <summary>Resident LRU data only. Images retained by callers and transient decode buffers are excluded.</summary>
public readonly record struct HdCardCacheStats(int ResidentImages, long ResidentBytes, long Hits, long Misses);

/// <summary>An RGB8 image. Logical size belongs to game placement; physical size belongs to the art.</summary>
public sealed class RgbCardImage
{
    private readonly byte[] pixels;
    internal RgbCardImage(byte[] pixels) => this.pixels = pixels;
    public int Width => 800;
    public int Height => 1160;
    public int LogicalWidth => 200;
    public int LogicalHeight => 290;
    public int ByteCount => pixels.Length;
    public ReadOnlySpan<byte> Pixels => pixels;
    public void CopyPixelsTo(Span<byte> destination) => pixels.AsSpan().CopyTo(destination);
}
