using System.Diagnostics;

namespace PowerOfChaos.Launcher.Core.Launching;

public static class LaunchPlan
{
    public static ProcessStartInfo Create(GameInstallation installation, LauncherSettings settings, bool? isWindows = null)
        => CreatePrepared(installation, settings, installation.ExecutablePath, isWindows);

    internal static ProcessStartInfo CreatePrepared(GameInstallation installation, LauncherSettings settings,
        string preparedExecutablePath, bool? isWindows = null, IReadOnlyDictionary<string, string?>? inheritedEnvironment = null)
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
        if (!settings.RestoreOriginalAspectRatio && Directory.EnumerateFiles(Path.GetDirectoryName(preparedExecutable)!)
            .Any(path => Path.GetFileName(path).Equals("ddraw.dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("winmm.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected launch directory contains display proxy DLLs. Choose the original installation, or enable Restore Original Aspect Ratio.");
        var result = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        if (inheritedEnvironment is not null)
        {
            result.Environment.Clear();
            foreach (var entry in inheritedEnvironment) result.Environment[entry.Key] = entry.Value;
        }
        var windows = isWindows ?? OperatingSystem.IsWindows();
        if (settings.RestoreOriginalAspectRatio) ConfigureDisplayEnvironment(result, windows);
        if (windows)
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

    internal static void ConfigureDisplayEnvironment(ProcessStartInfo result, bool isWindows)
    {
        // Inherited config overrides can redirect writes; the configuration-tool flag skips wrapper hooks entirely.
        foreach (var key in result.Environment.Keys.Where(key => key.Equals("CNC_DDRAW_CONFIG_FILE", StringComparison.OrdinalIgnoreCase)
            || key.Equals("CNC_DDRAW_CONFIG_INIT", StringComparison.OrdinalIgnoreCase)).ToArray())
            result.Environment.Remove(key);
        if (!isWindows)
        {
            result.Environment.TryGetValue("WINEDLLOVERRIDES", out var inherited);
            result.Environment["WINEDLLOVERRIDES"] = WithNativeDisplayOverrides(inherited);
        }
    }

    internal static string WithNativeDisplayOverrides(string? inherited)
    {
        // Wine syntax: module[,module...]=native|builtin entries separated by semicolons.
        // Keep other modules in combined entries and replace our two modules with strict native loading.
        var retained = new List<string>();
        foreach (var entry in (inherited ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equal = entry.IndexOf('=');
            if (equal < 0) { retained.Add(entry); continue; }
            var modules = entry[..equal].Split(',').Where(module => !IsDisplayModule(module)).ToArray();
            if (modules.Length > 0) retained.Add(string.Join(',', modules) + entry[equal..]);
        }
        // Wine tests *basename before basename for an absolute LoadLibrary path (as used by the bootstrap).
        retained.Add("winmm,ddraw,*winmm,*ddraw=n");
        return string.Join(';', retained);
    }

    private static bool IsDisplayModule(string module)
    {
        var name = module.Trim();
        var separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (separator >= 0) name = name[(separator + 1)..];
        if (name.StartsWith('*')) name = name[1..];
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Equals("winmm", StringComparison.OrdinalIgnoreCase) || name.Equals("ddraw", StringComparison.OrdinalIgnoreCase);
    }
}
