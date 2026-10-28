using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.ViewModels;

public sealed class CollectionCardViewModel : INotifyPropertyChanged, IDisposable
{
    private WriteableBitmap? _artwork;
    private bool _disposed;

    public CollectionCardViewModel(CollectionCard entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        var origin = GameCatalog.All.FirstOrDefault(game => game.Id == entry.OriginGameId)?.Title ?? entry.OriginGameId;
        var ownership = IsOwned ? $"{Quantity} {(Quantity == 1 ? "copy" : "copies")} owned" : "Missing";
        Tooltip = $"Card ID {CardId} · {ownership}\nFirst available in {origin}";
    }

    public CollectionCard Entry { get; }
    public int Slot => Entry.Slot;
    public ushort CardId => Entry.CardId;
    public byte Quantity => Entry.Quantity;
    public bool IsOwned => Quantity > 0;
    public string Tooltip { get; }
    public Bitmap? Artwork => _artwork;
    public bool HasArtwork => _artwork is not null;
    public int ArtworkWidth => _artwork?.PixelSize.Width ?? 50;
    public int ArtworkHeight => _artwork?.PixelSize.Height ?? 72;
    public double ArtworkOpacity => IsOwned ? 1.0 : 0.45;

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
        ClearArtwork();
    }

    private void NotifyArtworkChanged()
    {
        Changed(nameof(Artwork));
        Changed(nameof(HasArtwork));
        Changed(nameof(ArtworkWidth));
        Changed(nameof(ArtworkHeight));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
