using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;

namespace PowerOfChaos.Launcher.ViewModels;

public sealed class GameCardViewModel : INotifyPropertyChanged, IDisposable
{
    private string _playtime = "0h 00m";
    private bool _isRunning;
    private bool _anotherRunning;
    private bool _launchPending;
    private WriteableBitmap? _portrait;
    private bool _disposed;

    public GameCardViewModel(GameInstallation installation)
    {
        Installation = installation;
        var game = GameCatalog.Get(installation.GameId);
        Title = game.Title;
    }

    public GameInstallation Installation { get; }
    public string Title { get; }
    public Bitmap? Portrait => _portrait;
    public bool HasPortrait => _portrait is not null;
    public bool ShowPortraitPlaceholder => _portrait is null;
    public string Folder => Path.GetDirectoryName(Installation.ExecutablePath) ?? Installation.ExecutablePath;
    public string Playtime { get => _playtime; private set => Set(ref _playtime, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { Changed(nameof(PlayLabel)); Changed(nameof(CanPlay)); } } }
    public string PlayLabel => IsRunning ? "Playing" : "Play";
    public bool CanPlay => !IsRunning && !_anotherRunning && !_launchPending;

    public void SetPortrait(PortraitPixels pixels)
    {
        if (_disposed) return;
        if (pixels.Width is < 1 or > 2048 || pixels.Height is < 1 or > 2048
            || pixels.BgraPixels.Length != checked(pixels.Width * pixels.Height * 4))
            throw new ArgumentException("Invalid portrait pixels.", nameof(pixels));
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var frame = bitmap.Lock();
            var rowBytes = pixels.Width * 4;
            for (var y = 0; y < pixels.Height; y++)
                Marshal.Copy(pixels.BgraPixels, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
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
        _disposed = true;
        _portrait?.Dispose();
        _portrait = null;
    }

    public void SetLaunchPending(bool pending)
    {
        if (_launchPending == pending) return;
        _launchPending = pending; Changed(nameof(CanPlay));
    }

    public void Refresh(long seconds, bool running, bool anotherRunning = false)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        Playtime = $"{(long)duration.TotalHours}h {duration.Minutes:00}m";
        IsRunning = running;
        if (_anotherRunning != anotherRunning) { _anotherRunning = anotherRunning; Changed(nameof(CanPlay)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}
