using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.ViewModels;

public sealed class CollectionCardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _originTitle;
    private string? _cardName;
    private WriteableBitmap? _artwork;
    private bool _disposed;

    public CollectionCardViewModel(CollectionCard entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        _originTitle = GameCatalog.All.FirstOrDefault(game => game.Id == entry.OriginGameId)?.Title ?? entry.OriginGameId;
        LauncherText.Current.LanguageChanged += LanguageChanged;
    }

    public CollectionCard Entry { get; }
    public int Slot => Entry.Slot;
    public ushort CardId => Entry.CardId;
    public byte Quantity => Entry.Quantity;
    public bool IsOwned => Quantity > 0;
    public string? CardName => _cardName;
    public string DisplayName => CardName ?? LauncherText.Current.Format("Card ID {0}", CardId);
    public string Tooltip => LauncherText.Current.Format("{0} · {1}\nFirst available in {2}", DisplayName,
        IsOwned ? LauncherText.Current.Format(Quantity == 1 ? "{0} copy owned" : "{0} copies owned", Quantity)
            : LauncherText.Current.Translate("Missing"), _originTitle);
    public Bitmap? Artwork => _artwork;
    public bool HasArtwork => _artwork is not null;
    public int ArtworkWidth => _artwork?.PixelSize.Width ?? 50;
    public int ArtworkHeight => _artwork?.PixelSize.Height ?? 72;
    public double ArtworkOpacity => IsOwned ? 1.0 : 0.45;

    public void SetName(string? value)
    {
        if (_disposed) return;
        var name = NormalizeName(value);
        if (_cardName == name) return;
        _cardName = name;
        Changed(nameof(CardName));
        Changed(nameof(DisplayName));
        Changed(nameof(Tooltip));
    }

    public void SetArtwork(NativeCardImage image)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(image);
        if (image.Slot != Slot || image.Width is not (49 or 50) || image.Height != 72
            || image.BgraPixels is null || image.BgraPixels.Length != image.Width * image.Height * 4)
            throw new ArgumentException("Invalid native card artwork.", nameof(image));
        for (var alpha = 3; alpha < image.BgraPixels.Length; alpha += 4)
            if (image.BgraPixels[alpha] != 255)
                throw new ArgumentException("Native card artwork must be opaque.", nameof(image));

        // Native image pixels are already opaque BGRA, so they are also premultiplied.
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = bitmap.Lock();
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(image.BgraPixels, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        var previous = _artwork;
        _artwork = bitmap;
        NotifyArtworkChanged();
        previous?.Dispose();
    }

    public void ClearArtwork()
    {
        var previous = _artwork;
        if (previous is null) return;
        _artwork = null;
        NotifyArtworkChanged();
        previous.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LauncherText.Current.LanguageChanged -= LanguageChanged;
        ClearArtwork();
    }

    private void LanguageChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        Changed(nameof(DisplayName));
        Changed(nameof(Tooltip));
    }

    private void NotifyArtworkChanged()
    {
        Changed(nameof(Artwork));
        Changed(nameof(HasArtwork));
        Changed(nameof(ArtworkWidth));
        Changed(nameof(ArtworkHeight));
    }

    private static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim();
        if (name.Length > 128) return null;
        var remaining = name.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
                return null;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return null;
            remaining = remaining[consumed..];
        }
        return name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
