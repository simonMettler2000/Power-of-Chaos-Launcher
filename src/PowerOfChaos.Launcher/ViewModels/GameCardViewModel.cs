using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using PowerOfChaos.Launcher.Core;

namespace PowerOfChaos.Launcher.ViewModels;

public sealed class GameCardViewModel : INotifyPropertyChanged
{
    private string _playtime = "0h 00m";
    private bool _isRunning;
    private bool _anotherRunning;
    private bool _launchPending;

    public GameCardViewModel(GameInstallation installation)
    {
        Installation = installation;
        var game = GameCatalog.Get(installation.GameId);
        Title = game.Title;
        Character = game.Character;
        Monogram = game.Character[..1];
        Number = $"0{game.Order + 1}";
        Accent = new SolidColorBrush(Color.Parse(game.Accent));
    }

    public GameInstallation Installation { get; }
    public string Title { get; }
    public string Character { get; }
    public string Monogram { get; }
    public string Number { get; }
    public IBrush Accent { get; }
    public string Folder => Path.GetDirectoryName(Installation.ExecutablePath) ?? Installation.ExecutablePath;
    public string Playtime { get => _playtime; private set => Set(ref _playtime, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { Changed(nameof(PlayLabel)); Changed(nameof(CanPlay)); } } }
    public string PlayLabel => IsRunning ? "Playing" : "Play game  →";
    public bool CanPlay => !IsRunning && !_anotherRunning && !_launchPending;

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
