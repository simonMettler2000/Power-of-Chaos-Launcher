namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Immutable selection captured when Play is requested; changes apply to the next launch.</summary>
public sealed record GameModOptions(bool SkipSplashScreens = false, bool SkipCharacterInteractions = false,
    bool RestoreOriginalAspectRatio = false)
{
    internal bool AnyEnabled => BinaryModsEnabled || RestoreOriginalAspectRatio;
    internal bool BinaryModsEnabled => SkipSplashScreens || SkipCharacterInteractions;
    internal GameModOptions BinaryOptions => new(SkipSplashScreens, SkipCharacterInteractions);
    internal string CacheKey => (SkipSplashScreens, SkipCharacterInteractions) switch
    {
        (true, true) => "skip-splash-and-character-interactions",
        (true, false) => "skip-splash-screens",
        (false, true) => "skip-character-interactions",
        _ => "stock"
    };
}
