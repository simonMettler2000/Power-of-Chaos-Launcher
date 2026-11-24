using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Package-only diagnostics. Never discovers games, reads user settings or loads mod DLLs.</summary>
public static class ReleasePackageProbe
{
    public static string Version => InformationalVersion.Split('+')[0];
    private static string InformationalVersion => typeof(ReleasePackageProbe).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static int Run(string reportPath, Func<bool>? desktopDependencies = null)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new ArgumentException("An absolute report path is required.", nameof(reportPath));
        if (!Path.GetExtension(reportPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || new[] { "launcher-settings.json", "playtime.json", "state.json", "recovery.json", "package-manifest.json", "release-settings.json", "component-pins.json", "hd-component-pins.json", "gameplay-component-pins.json" }
                .Contains(Path.GetFileName(reportPath), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("A new diagnostic JSON report filename is required.", nameof(reportPath));
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var errors = new List<string>();
        bool Inside(string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(root)
            + Path.DirectorySeparatorChar, comparer);
        var localRuntime = Inside(typeof(object).Assembly.Location)
            && new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" }.All(name => File.Exists(Path.Combine(root, name)));
        if (!localRuntime) errors.Add("The runtime is not contained in the package.");
        var componentsValid = true;
        // The game-side bundles are only hashed here; nothing in them is loaded.
        foreach (var (directory, components, kind) in new (string, IReadOnlyList<DisplayComponent>, string)[]
        {
            (OriginalAspectRatioComponents.DirectoryName, OriginalAspectRatioComponents.Required, "display"),
            (HdCardArtworkComponents.DirectoryName, HdCardArtworkComponents.Required, "display"),
            (GameplayComponents.DirectoryName, new[] { GameplayComponents.Library }, "gameplay")
        })
        {
            foreach (var component in components)
            {
                var path = Path.Combine(root, "Components", directory, component.FileName);
                try
                {
                    RequireOrdinary(path);
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length != component.Length || !Convert.ToHexString(SHA256.HashData(input))
                        .Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Component identity mismatch.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    componentsValid = false;
                    errors.Add("Missing or changed " + kind + " component: " + directory + "/" + component.FileName);
                }
            }
            // A bundle holds its pinned files and nothing else.
            try
            {
                var present = Directory.EnumerateFileSystemEntries(Path.Combine(root, "Components", directory))
                    .Select(Path.GetFileName).ToArray();
                if (present.Length != components.Count
                    || present.Any(name => !components.Any(component => component.FileName.Equals(name, StringComparison.Ordinal))))
                {
                    componentsValid = false;
                    errors.Add("Unexpected content in component bundle: " + directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                componentsValid = false;
                errors.Add("Missing component bundle: " + directory);
            }
        }
        var informational = InformationalVersion;
        var sourceCommit = informational.Contains('+') ? informational[(informational.IndexOf('+') + 1)..] : "";
        var commitEnd = sourceCommit.IndexOf('.');
        if (commitEnd >= 0) sourceCommit = sourceCommit[..commitEnd];
        if (sourceCommit.Length != 40 || !sourceCommit.All(char.IsAsciiHexDigit)) errors.Add("A complete source commit is not embedded.");
        var runtimeIdentifier = "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (!OperatingSystem.IsWindows()) errors.Add("This package is intended for Windows.");
        var desktopDependenciesValid = false;
        try { desktopDependenciesValid = desktopDependencies?.Invoke() == true; }
        catch (Exception error) when (error is InvalidOperationException or TypeInitializationException or DllNotFoundException
            or FileNotFoundException or BadImageFormatException or NotSupportedException)
        {
            errors.Add("Desktop dependency initialization failed: " + error.GetType().Name);
        }
        if (!desktopDependenciesValid) errors.Add("Desktop graphics and font dependencies did not pass their authored rendering check.");
        var result = new
        {
            schemaVersion = 1,
            success = errors.Count == 0,
            version = Version,
            sourceCommit,
            runtimeIdentifier,
            selfContained = localRuntime,
            runtimeLocal = localRuntime,
            actualVersion = Environment.Version.ToString(),
            componentsValid,
            desktopDependenciesValid,
            builtInMods = new[] { "skip-splash-screens", "skip-character-interactions", "speedy-mode", "original-aspect-ratio",
                "temporary-all-cards", "hd-card-artwork", "new-card-rewards", "custom-opponent-decks" },
            gameFilesRead = false,
            userSettingsRead = false,
            errors
        };
        RequireOrdinary(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(output, result, new JsonSerializerOptions { WriteIndented = true });
        return result.success ? 0 : 1;
    }

    private static void RequireOrdinary(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked package paths are not supported.");
    }
}
