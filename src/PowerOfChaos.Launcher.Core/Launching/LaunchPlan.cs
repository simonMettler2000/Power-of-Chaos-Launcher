using System.Diagnostics;

namespace PowerOfChaos.Launcher.Core.Launching;

public static class LaunchPlan
{
    public static ProcessStartInfo Create(GameInstallation installation, LauncherSettings settings, bool? isWindows = null)
        => CreatePrepared(installation, settings, installation.ExecutablePath, isWindows);

    internal static ProcessStartInfo CreatePrepared(GameInstallation installation, LauncherSettings settings,
        string preparedExecutablePath, bool? isWindows = null)
    {
        var definition = GameCatalog.All.FirstOrDefault(g => g.Id == installation.GameId)
            ?? throw new ArgumentException("Unknown game.", nameof(installation));
        var executable = Path.GetFullPath(installation.ExecutablePath);
        if (!string.Equals(Path.GetFileName(executable), definition.ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected executable does not match this game.");
        if (!File.Exists(executable) || !File.Exists(installation.DataPath) || !File.Exists(installation.VoicePath))
            throw new FileNotFoundException("Game files are missing. Add or rescan the game path.");
        var preparedExecutable = Path.GetFullPath(preparedExecutablePath);
        if (!File.Exists(preparedExecutable)
            || !string.Equals(Path.GetFileName(preparedExecutable), definition.ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The prepared game executable is missing or invalid.");
        var result = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        if (isWindows ?? OperatingSystem.IsWindows())
            result.FileName = preparedExecutable;
        else
        {
            if (string.IsNullOrWhiteSpace(settings.WineExecutable))
                throw new ArgumentException("Choose a Wine runner in preferences first.");
            result.FileName = settings.WineExecutable.Trim();
            result.ArgumentList.Add("start");
            result.ArgumentList.Add("/wait");
            result.ArgumentList.Add("/unix");
            result.ArgumentList.Add(preparedExecutable);
        }
        return result;
    }
}
