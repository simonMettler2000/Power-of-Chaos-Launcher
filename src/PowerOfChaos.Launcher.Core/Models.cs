namespace PowerOfChaos.Launcher.Core;

public sealed record GameDefinition(string Id, string Title, string Character, string ExecutableName, string Accent, int Order);

public static class GameCatalog
{
    public static IReadOnlyList<GameDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new GameDefinition("yugi", "Yugi the Destiny", "Yugi", "yugi_pc.exe", "#C5AD70", 0),
        new GameDefinition("kaiba", "Kaiba the Revenge", "Kaiba", "kaiba_pc.exe", "#75B5CC", 1),
        new GameDefinition("joey", "Joey the Passion", "Joey", "joey_pc.exe", "#D19672", 2)
    });
    public static GameDefinition Get(string id) => All.First(g => g.Id == id);
}

public sealed record GameInstallation(string GameId, string ExecutablePath, string DataPath, string VoicePath, string DiscoveryRoot);
public sealed record DiscoveryOptions(int MaxDepth = 6, int MaxDirectories = 10000);
public sealed record DiscoveryResult(IReadOnlyList<GameInstallation> Games, int DirectoriesVisited, int SkippedDirectories, bool LimitReached, IReadOnlyList<string> Warnings);

public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> SearchRoots { get; set; } = [];
    public List<GameInstallation> Installations { get; set; } = [];
    public bool MinimizeOnLaunch { get; set; } = true;
    public bool SkipSplashScreens { get; set; }
    public bool SkipCharacterInteractions { get; set; }
    public string WineExecutable { get; set; } = "wine";
}

public static class LauncherPaths
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("POC_LAUNCHER_DATA_DIR") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerOfChaosLauncher");
}
