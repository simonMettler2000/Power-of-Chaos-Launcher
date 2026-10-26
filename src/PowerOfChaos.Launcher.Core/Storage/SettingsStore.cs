using System.Text.Json;

namespace PowerOfChaos.Launcher.Core.Storage;

/// <summary>Persists launcher preferences only, never game files or saves.</summary>
public sealed class SettingsStore
{
    private const int MaximumSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(string? directory = null) => _directory = Path.GetFullPath(directory ?? LauncherPaths.DataDirectory);

    public string? LastWarning { get; private set; }

    public async Task<LauncherSettings> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LastWarning = null;
            var path = Path.Combine(_directory, "launcher-settings.json");
            if (!File.Exists(path)) return new LauncherSettings();
            try
            {
                if (new FileInfo(path).Length > MaximumSettingsBytes)
                    throw new InvalidDataException("Settings file is too large.");
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                var loaded = await JsonSerializer.DeserializeAsync<LauncherSettings>(input, JsonOptions).ConfigureAwait(false);
                if (loaded is null || loaded.SchemaVersion != 1)
                    throw new InvalidDataException("Unsupported launcher settings format.");
                return Sanitize(loaded);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                LastWarning = "Saved launcher settings were invalid. A backup was kept and default settings were loaded.";
                try
                {
                    var backup = Path.Combine(_directory, $"launcher-settings.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
                    File.Copy(path, backup, overwrite: false);
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                {
                    LastWarning = "Saved launcher settings were invalid and could not be backed up. Default settings were loaded.";
                }
                return new LauncherSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastWarning = "Saved launcher settings could not be read. Default settings were loaded.";
                return new LauncherSettings();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sanitized = Sanitize(settings);
        var payload = JsonSerializer.SerializeToUtf8Bytes(sanitized, JsonOptions);
        if (payload.Length > MaximumSettingsBytes) throw new InvalidDataException("Launcher settings exceed the size limit.");
        await _gate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_directory);
            temporary = Path.Combine(_directory, $"launcher-settings.tmp-{Guid.NewGuid():N}");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(payload).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(_directory, "launcher-settings.json"), overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    private static LauncherSettings Sanitize(LauncherSettings settings)
    {
        var roots = new List<string>();
        var seenRoots = new HashSet<string>(PathComparer);
        foreach (var root in settings.SearchRoots ?? [])
        {
            if (roots.Count >= 64) break;
            if (ValidAbsolutePath(root) is { } path && seenRoots.Add(path)) roots.Add(path);
        }
        var installations = new List<GameInstallation>();
        var seenInstallations = new HashSet<string>(PathComparer);
        foreach (var installation in settings.Installations ?? [])
        {
            if (installations.Count >= 96) break;
            if (installation is null || !GameCatalog.All.Any(g => g.Id == installation.GameId)) continue;
            var executable = ValidAbsolutePath(installation.ExecutablePath);
            var data = ValidAbsolutePath(installation.DataPath);
            var voice = ValidAbsolutePath(installation.VoicePath);
            var root = ValidAbsolutePath(installation.DiscoveryRoot);
            if (executable is null || data is null || voice is null || root is null) continue;
            var expectedName = GameCatalog.Get(installation.GameId).ExecutableName;
            var parent = Path.GetDirectoryName(executable);
            if (!string.Equals(Path.GetFileName(executable), expectedName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(data), "data.dat", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(voice), "voice.dat", StringComparison.OrdinalIgnoreCase) ||
                !PathComparer.Equals(parent, Path.GetDirectoryName(data)) || !PathComparer.Equals(parent, Path.GetDirectoryName(voice))) continue;
            if (seenInstallations.Add(executable)) installations.Add(new GameInstallation(installation.GameId, executable, data, voice, root));
        }
        var wine = settings.WineExecutable?.Trim();
        if (string.IsNullOrWhiteSpace(wine) || wine.Length > 4096 || wine.Any(char.IsControl)) wine = "wine";
        return new LauncherSettings
        {
            SchemaVersion = 1,
            SearchRoots = roots,
            Installations = installations,
            MinimizeOnLaunch = settings.MinimizeOnLaunch,
            SkipSplashScreens = settings.SkipSplashScreens,
            SkipCharacterInteractions = settings.SkipCharacterInteractions,
            WineExecutable = wine
        };
    }

    private static string? ValidAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
