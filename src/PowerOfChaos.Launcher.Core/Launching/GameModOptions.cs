namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Immutable selection captured when Play is requested; changes apply to the next launch.</summary>
public sealed record GameModOptions(bool SkipSplashScreens = false, bool SkipCharacterInteractions = false,
    bool RestoreOriginalAspectRatio = false, bool HdCardArtwork = false, string? HdCardImageDirectory = null)
{
    internal bool AnyEnabled => BinaryModsEnabled || UsesDisplayComponent;
    internal bool BinaryModsEnabled => SkipSplashScreens || SkipCharacterInteractions;
    /// <summary>HD card artwork brings its own build of the display component, which also keeps the 4:3 picture.</summary>
    internal bool UsesDisplayComponent => RestoreOriginalAspectRatio || HdCardArtwork;
    internal GameModOptions BinaryOptions => new(SkipSplashScreens, SkipCharacterInteractions);
    internal string CacheKey => (SkipSplashScreens, SkipCharacterInteractions) switch
    {
        (true, true) => "skip-splash-and-character-interactions",
        (true, false) => "skip-splash-screens",
        (false, true) => "skip-character-interactions",
        _ => "stock"
    };
}
