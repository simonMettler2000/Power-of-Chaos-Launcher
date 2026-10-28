using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.ViewModels;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class CollectionViewModelChecks
{
    // Call after Avalonia SetupWithoutStarting. All metadata and pixels below are authored fixtures.
    public static void RunAll(Action<string, Action> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        check("collection gallery preserves counts, sorts card IDs, and labels origin separately from ownership", Metadata);
        check("unknown collection stays empty while known zero ownership shows missing cards", KnownState);
        check("collection expansion and gallery status notify bindings", Notifications);
        check("native card bitmaps keep 49/50 by 72 pixels, 96 DPI, and authored pixel rows", NativeDimensions);
        check("invalid native card buffers cannot replace valid artwork", InvalidArtwork);
        check("unchanged collection refresh retains card and artwork instances", StableRefresh);
        check("clearing or replacing collection metadata invalidates artwork without reviving old cards", Invalidation);
        check("collection disposal and late results are idempotent", Disposal);
    }

    private static void Metadata()
    {
        var progress = Progress();
        using var game = new CollectionGameViewModel(progress);
        Require(game.Progress == progress && game.Title == "Joey the Passion" && game.Count == "1/771"
            && game.Message == "Synthetic collection", "Legacy collection text values changed.");
        Require(game.HasCount && Math.Abs(game.Percentage - 100.0 / 771) < 0.000001,
            "Legacy collection count values changed.");
        Require(game.Cards.Select(card => card.CardId).SequenceEqual(new ushort[] { 7, 120 }),
            "Gallery card IDs are not sorted.");
        Require(game.HasCards && game.OwnedCards.Count == 1 && game.MissingCards.Count == 1,
            "Gallery did not split cards by ownership.");
        var owned = game.OwnedCards[0];
        var missing = game.MissingCards[0];
        Require(owned.Entry == progress.Cards[0] && owned.Slot == 4 && owned.CardId == 120
            && owned.Quantity == 2 && owned.IsOwned, "Owned card metadata or copy quantity changed.");
        Require(owned.Tooltip.Contains("2 copies owned") && owned.Tooltip.Contains("Card ID 120")
            && owned.Tooltip.Contains("First available in Yugi the Destiny"),
            "Owned tooltip does not distinguish copy count, ID, and source game.");
        Require(missing.Quantity == 0 && !missing.IsOwned && missing.Tooltip.Contains("Missing")
            && missing.Tooltip.Contains("First available in Kaiba the Revenge"),
            "Missing tooltip does not identify ownership and source game.");
        Require(owned.ArtworkOpacity == 1.0 && missing.ArtworkOpacity == 0.45,
            "Missing card opacity is not distinct from owned cards.");
        Require(!owned.HasArtwork && owned.Artwork is null && owned.ArtworkWidth == 50 && owned.ArtworkHeight == 72,
            "Artwork placeholder dimensions changed.");
        using var singleCopy = new CollectionCardViewModel(new(8, 121, 1, "yugi"));
        Require(singleCopy.Tooltip.Contains("1 copy owned"), "Single-copy tooltip uses the wrong quantity.");
    }

    private static void KnownState()
    {
        var progress = Progress();
        using var game = new CollectionGameViewModel(progress with { OwnedCards = null, Message = "Unavailable" });
        Require(!game.HasCount && game.Count == "—/771" && !game.HasCards && game.Cards.Count == 0
            && game.OwnedCards.Count == 0 && game.MissingCards.Count == 0 && game.GalleryStatus == "Unavailable",
            "Unknown collection invented ownership or missing cards.");
        Require(!game.UpdateProgress(progress with { OwnedCards = null, Message = "Still unavailable" })
            && game.GalleryStatus == "Still unavailable", "Unknown refresh rebuilt cards or lost its status.");
        var emptyCollection = progress with
        {
            OwnedCards = 0,
            Cards = progress.Cards.Select(card => card with { Quantity = 0 }).ToArray()
        };
        Require(game.UpdateProgress(emptyCollection), "Unknown-to-known transition was not detected.");
        Require(game.HasCount && game.Count == "0/771" && game.HasCards && game.OwnedCards.Count == 0
            && game.MissingCards.Count == 2, "Known zero ownership was treated as an unknown collection.");
        Require(game.UpdateProgress(progress with { OwnedCards = null }) && !game.HasCards,
            "Known-to-unknown transition retained card metadata.");
    }

    private static void Notifications()
    {
        using var game = new CollectionGameViewModel(Progress());
        var changes = new List<string?>();
        game.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        game.IsExpanded = true;
        Require(game.IsExpanded && changes.Contains(nameof(game.IsExpanded)), "Expansion did not notify bindings.");
        changes.Clear();
        game.IsExpanded = true;
        Require(changes.Count == 0, "Identical expansion notified again.");
        game.SetGalleryStatus("Loading artwork…");
        Require(game.GalleryStatus == "Loading artwork…" && changes.Contains(nameof(game.GalleryStatus)),
            "Gallery status did not notify bindings.");
        var card = game.OwnedCards[0];
        changes.Clear();
        card.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        card.SetArtwork(Image(card.Slot));
        Require(changes.Contains(nameof(card.Artwork)) && changes.Contains(nameof(card.HasArtwork))
            && changes.Contains(nameof(card.ArtworkWidth)) && changes.Contains(nameof(card.ArtworkHeight)),
            "Artwork binding notifications are incomplete.");
    }

    private static void NativeDimensions()
    {
        using var card = new CollectionCardViewModel(new(4, 120, 2, "yugi"));
        var image = Image(card.Slot);
        card.SetArtwork(image);
        Require(card.HasArtwork && card.ArtworkWidth == 49 && card.ArtworkHeight == 72,
            "49-pixel native artwork was resized.");
        Require(card.Artwork!.Dpi == new Vector(96, 96), "Native artwork bitmap does not use 96-DPI metadata.");
        using (var frame = ((WriteableBitmap)card.Artwork).Lock())
        {
            var row = new byte[49 * 4];
            Marshal.Copy(frame.Address + 71 * frame.RowBytes, row, 0, row.Length);
            Require(row.AsSpan().SequenceEqual(image.BgraPixels.AsSpan(71 * row.Length, row.Length)),
                "Native pixel rows changed while copying with framebuffer stride.");
        }
        var previous = card.Artwork;
        card.SetArtwork(Image(card.Slot, 50));
        Require(card.ArtworkWidth == 50 && card.ArtworkHeight == 72 && !ReferenceEquals(previous, card.Artwork),
            "50-pixel native artwork did not replace the previous bitmap.");
        card.ClearArtwork();
        card.ClearArtwork();
        Require(!card.HasArtwork && card.ArtworkWidth == 50 && card.ArtworkHeight == 72,
            "Repeated card clear did not retain the placeholder dimensions.");
    }

    private static void InvalidArtwork()
    {
        using var card = new CollectionCardViewModel(new(4, 120, 2, "yugi"));
        card.SetArtwork(Image(card.Slot));
        var original = card.Artwork;
        Reject(() => card.SetArtwork(new(card.Slot, 100, 72, new byte[100 * 72 * 4])), "Wide artwork was accepted.");
        Reject(() => card.SetArtwork(new(card.Slot, 49, 144, new byte[49 * 144 * 4])), "Tall artwork was accepted.");
        Reject(() => card.SetArtwork(new(card.Slot, 49, 72, new byte[1])), "Truncated artwork was accepted.");
        Reject(() => card.SetArtwork(new(card.Slot, 49, 72, new byte[49 * 72 * 4])), "Transparent artwork was accepted.");
        Reject(() => card.SetArtwork(Image(card.Slot + 1)), "Artwork for another slot was accepted.");
        Require(ReferenceEquals(original, card.Artwork), "Rejected artwork replaced a valid bitmap.");
    }

    private static void StableRefresh()
    {
        var progress = Progress();
        using var game = new CollectionGameViewModel(progress);
        var card = game.OwnedCards[0];
        game.SetArtwork(Image(card.Slot));
        var bitmap = card.Artwork;
        var version = game.ArtworkVersion;
        game.IsExpanded = true;
        game.SetGalleryStatus("Artwork loaded");
        Require(!game.UpdateProgress(progress with { Message = "Refreshed", Cards = progress.Cards.Reverse().ToArray() }),
            "Reordered but equivalent metadata rebuilt the gallery.");
        Require(ReferenceEquals(card, game.OwnedCards[0]) && ReferenceEquals(bitmap, card.Artwork)
            && game.ArtworkVersion == version && game.IsExpanded, "Unchanged refresh lost card, artwork, version, or expansion.");
        Require(game.Message == "Refreshed" && game.GalleryStatus == "Artwork loaded",
            "Unchanged refresh lost progress or gallery status.");
        Require(!game.UpdateProgress(progress with { OwnedCards = 2 }) && game.Count == "2/771"
            && ReferenceEquals(card, game.OwnedCards[0]), "Count-only refresh unnecessarily recreated card metadata.");
        game.SetArtwork(Image(1114));
        Require(ReferenceEquals(bitmap, card.Artwork), "Unknown artwork slot changed another card.");
    }

    private static void Invalidation()
    {
        var progress = Progress();
        using var game = new CollectionGameViewModel(progress);
        var card = game.OwnedCards[0];
        game.SetArtwork(Image(card.Slot));
        var version = game.ArtworkVersion;
        game.ClearArtwork();
        Require(!card.HasArtwork && game.ArtworkVersion == version + 1 && ReferenceEquals(card, game.OwnedCards[0]),
            "Clearing artwork failed to invalidate the batch while retaining card metadata.");
        game.ClearArtwork();
        Require(game.ArtworkVersion == version + 2, "Empty clear did not invalidate an in-flight artwork batch.");
        game.SetArtwork(Image(card.Slot));
        var changed = progress with { Cards = [progress.Cards[0] with { Quantity = 3 }, progress.Cards[1]] };
        Require(game.UpdateProgress(changed), "Changed copy quantity did not change the gallery metadata.");
        Require(!card.HasArtwork && !ReferenceEquals(card, game.OwnedCards[0])
            && game.OwnedCards[0].Quantity == 3 && game.ArtworkVersion == version + 3,
            "Metadata replacement did not dispose old artwork, replace cards, or invalidate the old batch.");
        card.SetArtwork(Image(card.Slot));
        Require(!card.HasArtwork, "Late artwork revived a card disposed by metadata replacement.");
    }

    private static void Disposal()
    {
        using var game = new CollectionGameViewModel(Progress());
        var card = game.OwnedCards[0];
        var missing = game.MissingCards[0];
        game.IsExpanded = true;
        game.SetGalleryStatus("Before dispose");
        game.SetArtwork(Image(card.Slot));
        game.SetArtwork(Image(missing.Slot));
        game.Dispose();
        var version = game.ArtworkVersion;
        game.Dispose();
        game.SetArtwork(Image(card.Slot));
        card.SetArtwork(Image(card.Slot));
        missing.SetArtwork(Image(missing.Slot));
        game.ClearArtwork();
        card.Dispose();
        game.SetGalleryStatus("Late status");
        game.IsExpanded = false;
        Require(!card.HasArtwork && !missing.HasArtwork && game.ArtworkVersion == version,
            "Repeated disposal or late artwork changed disposed card state.");
        Require(game.IsExpanded && game.GalleryStatus == "Before dispose"
            && !game.UpdateProgress(Progress() with { Cards = [] }), "Disposed gallery accepted a late mutation.");
    }

    private static GameCollectionProgress Progress() => new("joey", 771, 1, "Synthetic collection")
    {
        Cards = [new(4, 120, 2, "yugi"), new(2, 7, 0, "kaiba")]
    };

    private static NativeCardImage Image(int slot, int width = 49)
    {
        var pixels = new byte[width * 72 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(index / 4 % 251);
            pixels[index + 1] = 37;
            pixels[index + 2] = 91;
            pixels[index + 3] = 255;
        }
        return new(slot, width, 72, pixels);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
}
