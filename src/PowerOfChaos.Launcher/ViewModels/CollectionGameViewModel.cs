using System.ComponentModel;
using System.Runtime.CompilerServices;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.ViewModels;

public sealed class CollectionGameViewModel : INotifyPropertyChanged, IDisposable
{
    private GameCollectionProgress _progress;
    private CollectionCard[] _entries = [];
    private IReadOnlyList<CollectionCardViewModel> _cards = [];
    private IReadOnlyList<CollectionCardViewModel> _ownedCards = [];
    private IReadOnlyList<CollectionCardViewModel> _missingCards = [];
    private readonly Dictionary<int, CollectionCardViewModel> _cardsBySlot = [];
    private bool _isExpanded;
    private bool _disposed;
    private string _galleryStatus;

    public CollectionGameViewModel(GameCollectionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _progress = progress;
        ReplaceCards(GetEntries(progress));
        _galleryStatus = InitialGalleryStatus;
    }

    public GameCollectionProgress Progress => _progress;
    public string Title => GameCatalog.Get(Progress.GameId).Title;
    public string Count => $"{Progress.OwnedCards?.ToString() ?? "—"}/{Progress.TotalCards}";
    public string Message => Progress.Message;
    public bool HasCount => Progress.OwnedCards.HasValue;
    public double Percentage => (Progress.OwnedCards ?? 0) * 100.0 / Progress.TotalCards;
    public IReadOnlyList<CollectionCardViewModel> Cards => _cards;
    public IReadOnlyList<CollectionCardViewModel> OwnedCards => _ownedCards;
    public IReadOnlyList<CollectionCardViewModel> MissingCards => _missingCards;
    public bool HasCards => Cards.Count > 0;
    public string OwnedHeading => $"Owned cards ({OwnedCards.Count})";
    public string MissingHeading => $"Missing cards ({MissingCards.Count})";
    public string GalleryStatus => _galleryStatus;
    public int ArtworkVersion { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_disposed || _isExpanded == value) return;
            _isExpanded = value;
            Changed();
        }
    }

    /// <summary>Returns true only when the gallery's metadata or known state changed.</summary>
    public bool UpdateProgress(GameCollectionProgress progress)
    {
        if (_disposed) return false;
        ArgumentNullException.ThrowIfNull(progress);
        var entries = GetEntries(progress);
        var cardsChanged = HasCount != progress.OwnedCards.HasValue || !_entries.SequenceEqual(entries);
        _progress = progress;
        if (cardsChanged)
        {
            ReplaceCards(entries);
            InvalidateArtwork();
            SetGalleryStatus(InitialGalleryStatus);
            Changed(nameof(Cards));
            Changed(nameof(OwnedCards));
            Changed(nameof(MissingCards));
            Changed(nameof(HasCards));
            Changed(nameof(OwnedHeading));
            Changed(nameof(MissingHeading));
        }
        else if (!HasCount)
        {
            SetGalleryStatus(Message);
        }
        Changed(nameof(Progress));
        Changed(nameof(Title));
        Changed(nameof(Count));
        Changed(nameof(Message));
        Changed(nameof(HasCount));
        Changed(nameof(Percentage));
        return cardsChanged;
    }

    public void SetGalleryStatus(string status)
    {
        if (_disposed || _galleryStatus == status) return;
        ArgumentNullException.ThrowIfNull(status);
        _galleryStatus = status;
        Changed(nameof(GalleryStatus));
    }

    public void SetArtwork(NativeCardImage image)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(image);
        if (_cardsBySlot.TryGetValue(image.Slot, out var card)) card.SetArtwork(image);
    }

    public void ClearArtwork()
    {
        if (_disposed) return;
        foreach (var card in Cards) card.ClearArtwork();
        // Even an empty gallery can have an in-flight result that must become stale.
        InvalidateArtwork();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var card in Cards) card.Dispose();
        _cardsBySlot.Clear();
        InvalidateArtwork();
    }

    private string InitialGalleryStatus => !HasCount ? Message : HasCards ? string.Empty : "No cards available.";

    private static CollectionCard[] GetEntries(GameCollectionProgress progress) => progress.OwnedCards.HasValue
        ? progress.Cards.OrderBy(card => card.CardId).ThenBy(card => card.Slot).ToArray()
        : [];

    private void ReplaceCards(CollectionCard[] entries)
    {
        foreach (var card in Cards) card.Dispose();
        _entries = entries;
        var cards = entries.Select(entry => new CollectionCardViewModel(entry)).ToArray();
        _cards = Array.AsReadOnly(cards);
        _ownedCards = Array.AsReadOnly(cards.Where(card => card.IsOwned).ToArray());
        _missingCards = Array.AsReadOnly(cards.Where(card => !card.IsOwned).ToArray());
        _cardsBySlot.Clear();
        foreach (var card in cards) _cardsBySlot.TryAdd(card.Slot, card);
    }

    private void InvalidateArtwork()
    {
        ArtworkVersion++;
        Changed(nameof(ArtworkVersion));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
