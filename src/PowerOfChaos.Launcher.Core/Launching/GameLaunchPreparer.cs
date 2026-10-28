using System.Security.Cryptography;
using System.Text;

namespace PowerOfChaos.Launcher.Core.Launching;

public interface IGameLaunchPreparer
{
    Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Builds verified private launch copies. Installed executables and assets are read-only.</summary>
public sealed class GameLaunchPreparer : IGameLaunchPreparer
{
    private readonly string _dataDirectory;
    private readonly IReadOnlyList<SplashProfile> _profiles;
    private readonly OriginalAspectRatioPreparer _displayPreparer;
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public GameLaunchPreparer(string? dataDirectory = null, string? componentDirectory = null)
        : this(dataDirectory, GameModProfiles.Profiles, componentDirectory) { }
    internal GameLaunchPreparer(string? dataDirectory, IReadOnlyList<SplashProfile> profiles,
        string? componentDirectory = null, IReadOnlyList<DisplayComponent>? components = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory ?? LauncherPaths.DataDirectory);
        _profiles = profiles;
        _displayPreparer = new OriginalAspectRatioPreparer(_dataDirectory, componentDirectory, components);
    }

    public Task<string> PrepareExecutableAsync(GameInstallation installation, bool skipSplashScreens,
        CancellationToken cancellationToken = default) =>
        PrepareExecutableAsync(installation, new GameModOptions(SkipSplashScreens: skipSplashScreens), cancellationToken);

    public async Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var originalPath = Path.GetFullPath(installation.ExecutablePath);
        if (!options.AnyEnabled) return originalPath;
        var definition = GameCatalog.All.FirstOrDefault(game => game.Id == installation.GameId)
            ?? throw new ArgumentException("Unknown game.");
        if (!string.Equals(Path.GetFileName(originalPath), definition.ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected executable does not match this game.");
        var binaryOptions = options.BinaryOptions;
        var profile = options.BinaryModsEnabled
            ? _profiles.SingleOrDefault(item => item.GameId == installation.GameId && item.Options == binaryOptions)
            : _profiles.FirstOrDefault(item => item.GameId == installation.GameId);
        if (profile is null)
            throw new InvalidDataException("The selected mods are not supported for this game version. Untick the mods to launch normally.");
        if (!options.BinaryModsEnabled) profile = profile with { Changes = [], ModifiedHash = profile.OriginalHash };
        await CacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefuseLinks(originalPath);
            // The source is bounded and hash checked on EVERY launch, even when a cache already exists.
            var original = await ReadBoundedAsync(originalPath, profile.Length, cancellationToken).ConfigureAwait(false);
            var modified = SkipSplashPatch.Apply(original, profile);
            cancellationToken.ThrowIfCancellationRequested();
            if (options.RestoreOriginalAspectRatio)
                return await _displayPreparer.PrepareAsync(installation, options, profile, modified, cancellationToken)
                    .ConfigureAwait(false);
            // Keep a full SHA256 identity without nesting the verbose selection and both full hashes.
            // .NET can write paths which Windows CreateProcess cannot open (including the old v2 cache).
            var identity = string.Join('\n', profile.GameId, options.CacheKey, profile.OriginalHash, profile.ModifiedHash);
            var bucket = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            var directory = Path.Combine(_dataDirectory, "mod-cache", "v3", profile.GameId, bucket);
            var outputPath = Path.Combine(directory, definition.ExecutableName);
            if (OperatingSystem.IsWindows()) LaunchPlan.ValidateWindowsPaths(outputPath, Path.GetDirectoryName(originalPath)!);
            var installDirectory = Path.GetDirectoryName(originalPath)!;
            if (directory.Equals(installDirectory, PathComparison)
                || directory.StartsWith(Path.TrimEndingDirectorySeparator(installDirectory) + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("The mod cache must be outside the game installation. Choose a separate launcher data directory.");
            RefuseLinks(directory);
            Directory.CreateDirectory(directory);
            RefuseLinks(directory);
            RefuseLinks(outputPath);
            var lockPath = Path.Combine(directory, ".prepare.lock");
            RefuseLinks(lockPath);
            // Cross-process ownership prevents concurrent launchers replacing a cache under preparation.
            await using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length == profile.Length)
            {
                var cached = await ReadBoundedAsync(outputPath, profile.Length, cancellationToken).ConfigureAwait(false);
                if (SkipSplashPatch.MatchesHash(cached, profile.ModifiedHash)) return outputPath;
            }
            var temporary = Path.Combine(directory, ".preparing-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(modified, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                RefuseLinks(directory);
                RefuseLinks(outputPath);
                File.Move(temporary, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            // A final read checks the file selected for launch, rather than trusting only the write buffer.
            var result = await ReadBoundedAsync(outputPath, profile.Length, cancellationToken).ConfigureAwait(false);
            if (!SkipSplashPatch.MatchesHash(result, profile.ModifiedHash))
                throw new InvalidDataException("The private mod cache failed verification. No game was started.");
            return outputPath;
        }
        finally { CacheGate.Release(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int length, CancellationToken cancellationToken)
    {
        if (length is < 256 or > 16 * 1024 * 1024) throw new InvalidDataException("Unsupported executable size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length != length) throw new InvalidDataException("The selected mods do not support this executable version. Untick the mods to launch normally.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != length) throw new InvalidDataException("The executable changed during verification.");
        return bytes;
    }

    private static void RefuseLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked paths are not supported for mod launch copies. Select the real game and data folders.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
