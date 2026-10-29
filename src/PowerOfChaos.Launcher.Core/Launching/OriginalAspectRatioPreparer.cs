using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Factual bundle pins. Components are supplied locally, never downloaded or built during Play.</summary>
internal sealed record DisplayComponent(string FileName, int Length, string Sha256, bool IsDll = false);

internal static class OriginalAspectRatioComponents
{
    internal const string DirectoryName = "original-aspect-ratio-v1";
    // The verified wrapper and bootstrap are runtime components; binaries stay outside the source tree.
    internal static IReadOnlyList<DisplayComponent> Required { get; } = Array.AsReadOnly(new[]
    {
        new DisplayComponent("ddraw.dll", 424448, "0f5f094dd1ab1bc23cfdc1fe4c1ed51d91c1c9a5fafd063c9897fa018606be91", true),
        new DisplayComponent("winmm.dll", 98304, "1de1ed07c548e262767ba06891284ae0ecc39f6cc0217dd715594e5bc30e0e8a", true),
        new DisplayComponent("upstream-cnc-ddraw-LICENSE.txt", 1078, "a705144535bbfccc05e8310c4f8ab5f0b6f0ec2c9371b76ca5a522cf90e031f5"),
        new DisplayComponent("petool-LICENSE.txt", 1067, "d8220b095d6156f2d48aff570955f07d2f159a50a27ef0101d2f042e34cde3b2"),
        new DisplayComponent("Hyllian-Jararaca-LICENSE.txt", 1209, "d036d24fad0973e8b47fdc392067287c5ce4ed4511928c927ec7fab573d383c1"),
        new DisplayComponent("Hyllian-xBR-LICENSE.txt", 1234, "01c779f320826e6ce554ec1fb50009ce7b4b0eb99db11b1145d43ce66a2b1e76"),
        new DisplayComponent("LodePNG-LICENSE.txt", 908, "6d2570c1867730f8a21cdfe1dcd5d7cdbac2343a9357e94ef6b39382c04b18b7"),
        new DisplayComponent("Microsoft-Detours-LICENSE.txt", 1099, "b301808b732cfaa60df2b4b422d78cd97d2a15058b207e7e33f0535ba5170dd6"),
        new DisplayComponent("MJP-LICENSE.txt", 1060, "33f78b7cb3e7887d0dd123b6a1ee03f6b1be44ed70a321e44283efe7e9b9e7b6"),
        new DisplayComponent("Khronos-LICENSE.txt", 2617, "77e1af94552f586bed31027e19b1438148a56571a46ed6205da9bf1ecdb33cad")
    });
}

internal static class HdCardArtworkComponents
{
    internal const string DirectoryName = "hd-card-artwork-v1";
    internal const string RecipeVersion = "hd-card-artwork-v3-r5-exception-no-paint-halftone-1280x960-rgb565";
    // Factual pin of the separately source-built HD provider; no native binary is included in this repository.
    internal const int ProductionDdrawLength = 564224;
    internal const string ProductionDdrawSha256 = "3f29b2499463ce50744c43d418e9fd5a128345af4d333f8ca6be976f5f755fda";
    internal static IReadOnlyList<DisplayComponent> Required { get; } = Array.AsReadOnly(
        OriginalAspectRatioComponents.Required.Select(component => component.FileName == "ddraw.dll"
            ? new DisplayComponent("ddraw.dll", ProductionDdrawLength, ProductionDdrawSha256, true) : component).ToArray());
}

/// <summary>Creates verified private display launch copies without editing installed game files.</summary>
internal sealed class OriginalAspectRatioPreparer
{
    internal const string RecipeVersion = "original-aspect-ratio-v1-windowed-800x600-logic-unlimited";
    private const int MaximumComponentBytes = 16 * 1024 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly bool _hdMode;
    private readonly string _dataDirectory;
    private readonly string _componentDirectory;
    private readonly IReadOnlyList<DisplayComponent> _components;
    private readonly WindowsDisplayCacheLocation? _displayCache;

    internal OriginalAspectRatioPreparer(string dataDirectory, string? componentDirectory,
        IReadOnlyList<DisplayComponent>? components, WindowsDisplayCacheLocation? displayCache = null, bool hdMode = false)
    {
        _hdMode = hdMode;
        _dataDirectory = dataDirectory;
        _componentDirectory = Path.GetFullPath(componentDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Components", hdMode ? HdCardArtworkComponents.DirectoryName : OriginalAspectRatioComponents.DirectoryName));
        _components = Array.AsReadOnly((components ?? (hdMode ? HdCardArtworkComponents.Required : OriginalAspectRatioComponents.Required)).ToArray());
        _displayCache = displayCache;
        if (_components.Count < 2 || _components.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _components.Count
            || !_components.Any(item => item.FileName == "ddraw.dll" && item.IsDll)
            || !_components.Any(item => item.FileName == "winmm.dll" && item.IsDll)
            || _components.Any(item => Path.GetFileName(item.FileName) != item.FileName
                || item.FileName.IndexOfAny(['/', '\\', ':', '\r', '\n']) >= 0
                || item.Length is < 256 or > MaximumComponentBytes || item.Sha256.Length != 64
                || !item.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The original aspect ratio component manifest is invalid.");
    }

    internal async Task<string> PrepareAsync(GameInstallation installation, GameModOptions options,
        SplashProfile profile, byte[] executable, CancellationToken cancellationToken, HdCardPackSnapshot? hdPack = null)
    {
        var originalPath = Path.GetFullPath(installation.ExecutablePath);
        var installDirectory = Path.GetDirectoryName(originalPath)!;
        if (_hdMode != (hdPack is not null)) throw new InvalidDataException("HD launch configuration is missing or unexpected.");
        var location = _displayCache ?? WindowsDisplayCacheLocation.Select(_dataDirectory, _hdMode);
        var root = location.Root;
        // Every native module, INI and screenshot path has this prefix. The pinned wrapper uses ANSI path APIs.
        RefuseNonAsciiNativePath(root);
        RefuseOverlap(root, installDirectory);
        RefuseOverlap(root, Path.GetDirectoryName(Path.GetFullPath(installation.DataPath))!);
        RefuseOverlap(root, Path.GetDirectoryName(Path.GetFullPath(installation.VoicePath))!);
        RefuseOverlap(root, _componentDirectory);
        if (hdPack is not null) foreach (var input in hdPack.InputPaths) RefuseOverlap(root, input);
        RefuseLinks(originalPath);
        RefuseLinks(installation.DataPath);
        RefuseLinks(installation.VoicePath);
        RefuseLinks(root);
        RefuseLinks(_componentDirectory);
        var componentBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var component in _components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(_componentDirectory, component.FileName);
            RefuseLinks(source);
            if (!File.Exists(source))
                throw new FileNotFoundException("The selected display mod needs the verified local component bundle. Run the documented component setup, or untick this mod. Missing: " + component.FileName);
            componentBytes.Add(component.FileName, await ReadComponentAsync(source, component, cancellationToken).ConfigureAwait(false));
        }
        if (executable.Length != profile.Length || !SkipSplashPatch.MatchesHash(executable, profile.ModifiedHash))
            throw new InvalidDataException("The display launch executable failed verification. No game was started.");

        // The full identity stays in the manifest; only the directory bucket is shortened for legacy MAX_PATH APIs.
        var identity = JsonSerializer.Serialize(new
        {
            RecipeVersion = _hdMode ? HdCardArtworkComponents.RecipeVersion : RecipeVersion, installation.GameId, OriginalPath = originalPath, profile.Length,
            profile.OriginalHash, profile.ModifiedHash, options.SkipSplashScreens, options.SkipCharacterInteractions,
            // Hash the complete recipe with a fixed bucket placeholder to avoid a circular directory/config hash.
            ConfigurationRecipeSha256 = Hash(Encoding.UTF8.GetBytes(CreateConfiguration(Path.Combine(root, new string('0', 32), "screenshots"), _hdMode))),
            Components = _components
        });
        if (hdPack is not null) identity = JsonSerializer.Serialize(new { DisplayIdentity = identity, HdPackSha256 = hdPack.Sha256, options.HdPackDirectory, options.HdCardArtwork, options.RestoreOriginalAspectRatio });
        var identityHash = Hash(Encoding.UTF8.GetBytes(identity));
        var directory = Path.Combine(root, identityHash[..32]);
        var executableName = GameCatalog.Get(installation.GameId).ExecutableName;
        var output = Path.Combine(directory, executableName);
        var screenshots = Path.Combine(directory, "screenshots");
        // cnc-ddraw 7.1 uses ANSI MAX_PATH buffers for module-local INI lookup. Fail closed before a fallback can write in game CWD.
        if (Path.Combine(directory, "ddraw.ini").Length >= 260 || screenshots.Length + 1 >= 260)
            throw new InvalidDataException("The launcher data path is too long for display mods. Choose a shorter launcher data directory, or untick the display mods.");
        var configuration = Encoding.UTF8.GetBytes(CreateConfiguration(screenshots, _hdMode));
        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Identity = identity, IdentitySha256 = identityHash, ConfigurationSha256 = Hash(configuration)
        }));
        cancellationToken.ThrowIfCancellationRequested();
        location.EnsureCreated();
        RefuseLinks(root);
        var lockPath = Path.Combine(root, identityHash[..32] + ".lock");
        RefuseLinks(lockPath);
        await using var ownership = await AcquireLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        RefuseLinks(directory);
        if (await VerifyCacheAsync(directory, executableName, profile, configuration, manifest, cancellationToken, hdPack?.Configuration).ConfigureAwait(false))
            return output;

        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        string? retired = null;
        try
        {
            Directory.CreateDirectory(staging);
            await WriteAsync(Path.Combine(staging, executableName), executable, cancellationToken).ConfigureAwait(false);
            foreach (var component in _components)
                await WriteAsync(Path.Combine(staging, component.FileName), componentBytes[component.FileName], cancellationToken).ConfigureAwait(false);
            if (hdPack is not null) await WriteAsync(Path.Combine(staging, HdCardPack.ConfigurationFileName), hdPack.Configuration, cancellationToken).ConfigureAwait(false);
            // Write and verify the INI before publication; a write failure must never fall through to wrapper defaults.
            await WriteAsync(Path.Combine(staging, "ddraw.ini"), configuration, cancellationToken).ConfigureAwait(false);
            await WriteAsync(Path.Combine(staging, "display-manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(staging, "screenshots"));
            if (!await VerifyCacheAsync(staging, executableName, profile, configuration, manifest, cancellationToken, hdPack?.Configuration).ConfigureAwait(false))
                throw new InvalidDataException("The private display cache failed verification. No game was started.");
            cancellationToken.ThrowIfCancellationRequested();
            RefuseLinks(root);
            RefuseLinks(directory);
            if (Directory.Exists(directory))
            {
                retired = Path.Combine(root, ".replaced-" + Guid.NewGuid().ToString("N"));
                Directory.Move(directory, retired);
            }
            try { Directory.Move(staging, directory); }
            catch
            {
                if (retired is not null && !Directory.Exists(directory)) Directory.Move(retired, directory);
                throw;
            }
            if (!await VerifyCacheAsync(directory, executableName, profile, configuration, manifest, cancellationToken, hdPack?.Configuration).ConfigureAwait(false))
                throw new InvalidDataException("The published display cache failed verification. No game was started.");
            return output;
        }
        finally
        {
            TryDeleteOwnedDirectory(staging, root);
            if (retired is not null) TryDeleteOwnedDirectory(retired, root);
        }
    }

    internal static string CreateConfiguration(string screenshotDirectory, bool hdMode = false)
    {
        var screenshotPath = Path.GetFullPath(screenshotDirectory);
        RefuseNonAsciiNativePath(screenshotPath);
        if (screenshotPath.Any(character => character is '\r' or '\n' or '\0'))
            throw new InvalidDataException("The display cache path cannot be represented in the wrapper configuration.");
        // Wine maps an absolute Unix host path through Z:, while Windows already supplies a drive path.
        if (!OperatingSystem.IsWindows()) screenshotPath = "Z:" + screenshotPath.Replace('/', '\\');
        screenshotPath = screenshotPath.TrimEnd('\\') + "\\";
        var configuration = "[ddraw]\nwidth=800\nheight=600\nwindowed=true\nfullscreen=false\nmaintas=true\naspect_ratio=4:3\n"
            + "boxing=false\nmaxgameticks=-1\nlimiter_type=0\nminfps=0\nsavesettings=0\nresizable=true\nborder=true\n"
            + "toggle_borderless=true\ntoggle_upscaled=false\nrenderer=auto\nd3d9_filter=1\nshader=Nearest neighbor\n"
            + "maxfps=-1\nvsync=false\nadjmouse=true\nkeyscreenshot=0x00\nscreenshotdir=" + screenshotPath
            + "\nsinglecpu=false\nfixchilds=0\nvhack=false\nhook_peekmessage=false\nposX=-32000\nposY=-32000\n";
        return hdMode ? configuration.Replace("width=800\nheight=600", "width=1280\nheight=960", StringComparison.Ordinal)
            .Replace("renderer=auto", "renderer=gdi", StringComparison.Ordinal) + "rgb555=false\n" : configuration;
    }

    internal static async Task VerifyComponentsAsync(string directory, IReadOnlyList<DisplayComponent> components, CancellationToken cancellationToken)
    {
        RefuseLinks(directory);
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, component.FileName);
            if (!File.Exists(path)) throw new FileNotFoundException("HD card artwork needs the verified local component bundle. Missing: " + component.FileName);
            await ReadComponentAsync(path, component, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RefuseNonAsciiNativePath(string path)
    {
        if (path.Any(character => character > 0x7F))
            throw new InvalidDataException("Display mods require a short ASCII-only launcher data path. Set POC_LAUNCHER_DATA_DIR to a short path using only ASCII characters, or untick the display mods.");
    }

    private async Task<bool> VerifyCacheAsync(string directory, string executableName, SplashProfile profile,
        byte[] configuration, byte[] manifest, CancellationToken cancellationToken, byte[]? hdConfiguration = null)
    {
        if (!Directory.Exists(directory)) return false;
        RefuseLinks(directory);
        var expected = _components.Select(component => component.FileName)
            .Concat([executableName, "ddraw.ini", "display-manifest.json", "screenshots"]).ToHashSet(StringComparer.Ordinal);
        if (hdConfiguration is not null) expected.Add(HdCardPack.ConfigurationFileName);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RefuseLinks(entry);
            if (!expected.Contains(Path.GetFileName(entry))) return false;
        }
        var screenshots = Path.Combine(directory, "screenshots");
        if (!Directory.Exists(screenshots)) return false;
        RefuseLinks(screenshots);
        if (!await MatchesFileAsync(Path.Combine(directory, executableName), profile.Length, profile.ModifiedHash, cancellationToken).ConfigureAwait(false)
            || !await MatchesFileAsync(Path.Combine(directory, "ddraw.ini"), configuration.Length, Hash(configuration), cancellationToken).ConfigureAwait(false)
            || !await MatchesFileAsync(Path.Combine(directory, "display-manifest.json"), manifest.Length, Hash(manifest), cancellationToken).ConfigureAwait(false)) return false;
        if (hdConfiguration is not null && !await MatchesFileAsync(Path.Combine(directory, HdCardPack.ConfigurationFileName), hdConfiguration.Length, Hash(hdConfiguration), cancellationToken).ConfigureAwait(false)) return false;
        foreach (var component in _components)
        {
            var path = Path.Combine(directory, component.FileName);
            if (!await MatchesFileAsync(path, component.Length, component.Sha256, cancellationToken).ConfigureAwait(false)) return false;
            if (component.IsDll) ValidateDll(await ReadExactAsync(path, component.Length, cancellationToken).ConfigureAwait(false));
        }
        return true;
    }

    private static async Task<byte[]> ReadComponentAsync(string path, DisplayComponent component, CancellationToken cancellationToken)
    {
        var bytes = await ReadExactAsync(path, component.Length, cancellationToken).ConfigureAwait(false);
        if (!SkipSplashPatch.MatchesHash(bytes, component.Sha256))
            throw new InvalidDataException("The display component is damaged or the wrong version: " + component.FileName + ". Run the documented component setup again, or untick this mod.");
        if (component.IsDll) ValidateDll(bytes);
        return bytes;
    }

    private static void ValidateDll(ReadOnlySpan<byte> image)
    {
        if (image.Length < 256 || BinaryPrimitives.ReadUInt16LittleEndian(image) != 0x5A4D) InvalidDll();
        var pe = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x3C, 4));
        if (pe > image.Length - 24) InvalidDll();
        var offset = checked((int)pe);
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset + 6, 2));
        var optionalLength = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset + 20, 2));
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset + 22, 2));
        if (BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, 4)) != 0x4550
            || BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset + 4, 2)) != 0x14C
            || sections is 0 or > 96 || optionalLength < 224 || (characteristics & 0x2002) != 0x2002
            || (long)offset + 24 + optionalLength + sections * 40 > image.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset + 24, 2)) != 0x10B) InvalidDll();
    }

    private static void InvalidDll() => throw new InvalidDataException("Display mods require verified PE32 x86 DLL components. Run the documented component setup again, or untick this mod.");

    private static async Task<bool> MatchesFileAsync(string path, int length, string hash, CancellationToken cancellationToken)
    {
        RefuseLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length != length) return false;
        return SkipSplashPatch.MatchesHash(await ReadExactAsync(path, length, cancellationToken).ConfigureAwait(false), hash);
    }

    private static async Task<byte[]> ReadExactAsync(string path, int length, CancellationToken cancellationToken)
    {
        RefuseLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length != length)
            throw new InvalidDataException("A display component has an unsupported size or changed during verification. Run the documented component setup again, or untick this mod.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != length) throw new InvalidDataException("A display launch file changed during verification.");
        RefuseLinks(path);
        return bytes;
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        RefuseLinks(path);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefuseLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 200)
            { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
        }
    }

    internal static void RefuseOverlap(string output, string input)
    {
        var left = ComparisonPath(output);
        var right = ComparisonPath(input);
        static string Prefix(string value) => Path.EndsInDirectorySeparator(value) ? value : value + Path.DirectorySeparatorChar;
        if (left.Equals(right, PathComparison) || left.StartsWith(Prefix(right), PathComparison)
            || right.StartsWith(Prefix(left), PathComparison))
            throw new InvalidDataException("The display cache must be separate from the game installation and component inputs. Choose a separate launcher data directory.");
    }

    private const string UnresolvedPathMessage = "The display paths could not be resolved safely. Select ordinary existing game and component folders.";

    internal static string NormalizeWindowsPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') return path[4..];
            throw new InvalidDataException(UnresolvedPathMessage);
        }
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UnresolvedPathMessage);
        return path;
    }

    private static string ComparisonPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return Path.TrimEndingDirectorySeparator(full);
        full = NormalizeWindowsPrefix(full);
        var missing = new Stack<string>();
        var ancestor = full;
        while (true)
        {
            try { _ = File.GetAttributes(ancestor); break; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                var parent = Path.GetDirectoryName(ancestor);
                if (string.IsNullOrEmpty(parent)) throw new InvalidDataException(UnresolvedPathMessage, error);
                missing.Push(Path.GetFileName(ancestor));
                ancestor = parent;
            }
        }
        // Expand existing DOS short names before comparing boundaries. Missing cache suffixes are appended
        // only after resolving their existing ancestor; this creates no directory and follows no new path.
        var expanded = new StringBuilder(32768);
        var length = GetLongPathName(ancestor, expanded, (uint)expanded.Capacity);
        if (length == 0 || length >= expanded.Capacity)
            throw new InvalidDataException(UnresolvedPathMessage, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var resolved = NormalizeWindowsPrefix(expanded.ToString());
        foreach (var part in missing) resolved = Path.Combine(resolved, part);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint capacity);

    internal static void RefuseLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked paths are not supported for display launch copies. Select the real game, component and data folders.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void TryDeleteOwnedDirectory(string path, string parent)
    {
        if (!Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Path.GetFullPath(parent), PathComparison)
            || !(Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith(".replaced-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Refusing to remove an unowned display staging directory.");
        try
        {
            if (!Directory.Exists(path)) return;
            RefuseLinks(path);
            DeleteDirectoryWithoutLinks(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static void DeleteDirectoryWithoutLinks(string path)
    {
        RefuseLinks(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            RefuseLinks(entry);
            if (Directory.Exists(entry)) DeleteDirectoryWithoutLinks(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
