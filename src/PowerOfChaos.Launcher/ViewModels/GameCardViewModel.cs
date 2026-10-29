using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.ViewModels;

public sealed class GameCardViewModel : INotifyPropertyChanged, IDisposable
{
    private long _playtimeMinutes;
    private bool _isRunning;
    private bool _anotherRunning;
    private bool _launchBlocked;
    private bool _launchPending;
    private WriteableBitmap? _portrait;
    private bool _disposed;
    private string _collectionCount;
    private string _collectionMessage = "Collection unavailable";

    public GameCardViewModel(GameInstallation installation)
    {
        Installation = installation;
        var game = GameCatalog.Get(installation.GameId);
        Title = game.Title;
        _collectionCount = $"—/{CollectionSnapshot.TotalFor(game.Id).ToString(CultureInfo.InvariantCulture)}";
        LauncherText.Current.LanguageChanged += LanguageChanged;
    }

    public GameInstallation Installation { get; }
    public string Title { get; }
    public Bitmap? Portrait => _portrait;
    public bool HasPortrait => _portrait is not null;
    public bool ShowPortraitPlaceholder => _portrait is null;
    public string Folder => Path.GetDirectoryName(Installation.ExecutablePath) ?? Installation.ExecutablePath;
    public string Playtime => LauncherText.Current.Format("{0}h {1:00}m", _playtimeMinutes / 60, _playtimeMinutes % 60);
    public string CollectionCount { get => _collectionCount; private set => Set(ref _collectionCount, value); }
    public string CollectionMessage => LauncherText.Current.Translate(_collectionMessage);
    public void SetCollection(GameCollectionProgress progress)
    {
        if (_disposed || progress.GameId != Installation.GameId) return;
        CollectionCount = $"{progress.OwnedCards?.ToString(CultureInfo.InvariantCulture) ?? "—"}/{progress.TotalCards.ToString(CultureInfo.InvariantCulture)}";
        if (_collectionMessage != progress.Message)
        {
            _collectionMessage = progress.Message;
            Changed(nameof(CollectionMessage));
        }
    }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { Changed(nameof(PlayLabel)); Changed(nameof(CanPlay)); } } }
    public string PlayLabel => LauncherText.Current.Translate(IsRunning ? "Running" : _launchPending ? "Starting…" : "Play");
    public string PlayAutomationName => LauncherText.Current.Format("Play {0}", Title);
    public string PortraitAutomationName => LauncherText.Current.Format("Portrait of {0}", Title);
    public string CollectionAutomationName => LauncherText.Current.Format("Card collection for {0}", Title);
    public bool CanPlay => !IsRunning && !_anotherRunning && !_launchBlocked && !_launchPending;

    public void SetPortrait(PortraitThumbnail thumbnail)
    {
        if (_disposed) return;
        if (thumbnail.Width is < 1 or > 4096 || thumbnail.Height is < 1 or > 4096
            || thumbnail.PremultipliedBgraPixels.Length != checked(thumbnail.Width * thumbnail.Height * 4)
            || thumbnail.PremultipliedBgraPixels.Length > 8_000_000)
            throw new ArgumentException("Invalid portrait thumbnail.", nameof(thumbnail));
        var bitmap = new WriteableBitmap(new PixelSize(thumbnail.Width, thumbnail.Height),
            // Image source rectangles use pixels. Keep 96-DPI metadata and let Stretch fit the
            // physical-resolution thumbnail; monitor DPI belongs to the destination transform.
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = bitmap.Lock();
            var rowBytes = thumbnail.Width * 4;
            for (var y = 0; y < thumbnail.Height; y++)
                Marshal.Copy(thumbnail.PremultipliedBgraPixels, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        var previous = _portrait;
        _portrait = bitmap;
        Changed(nameof(Portrait));
        Changed(nameof(HasPortrait));
        Changed(nameof(ShowPortraitPlaceholder));
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LauncherText.Current.LanguageChanged -= LanguageChanged;
        _portrait?.Dispose();
        _portrait = null;
    }

    public void SetLaunchBlocked(bool blocked)
    {
        if (_disposed || _launchBlocked == blocked) return;
        _launchBlocked = blocked;
        Changed(nameof(CanPlay));
    }

    public void SetLaunchPending(bool pending)
    {
        if (_disposed || _launchPending == pending) return;
        _launchPending = pending;
        Changed(nameof(CanPlay));
        Changed(nameof(PlayLabel));
    }

    public void Refresh(long seconds, bool running, bool anotherRunning = false)
    {
        if (_disposed) return;
        var minutes = Math.Max(0, seconds) / 60;
        if (_playtimeMinutes != minutes)
        {
            _playtimeMinutes = minutes;
            Changed(nameof(Playtime));
        }
        IsRunning = running;
        if (_anotherRunning != anotherRunning) { _anotherRunning = anotherRunning; Changed(nameof(CanPlay)); }
    }

    private void LanguageChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        Changed(nameof(Playtime));
        Changed(nameof(CollectionMessage));
        Changed(nameof(PlayLabel));
        Changed(nameof(PlayAutomationName));
        Changed(nameof(PortraitAutomationName));
        Changed(nameof(CollectionAutomationName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}
