using System.Text;

namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>What differs between the two uses of the display component.</summary>
internal sealed record DisplayVariant(string RecipeVersion, string ModName, bool HdCards)
{
    internal static DisplayVariant OriginalAspectRatio { get; } =
        new(OriginalAspectRatioPreparer.RecipeVersion, "Restore Original Aspect Ratio", false);
    internal static DisplayVariant HdCardArtwork { get; } =
        new("hd-card-artwork-v2-borderless-4x3-engine-copy-tracking", "HD card artwork", true);
}

/// <summary>Factual bundle pins of the HD card provider. Binaries stay outside the source tree.</summary>
internal static class HdCardArtworkComponents
{
    internal const string DirectoryName = "hd-card-artwork-v2";
    internal const string SettingsFileName = "pochd.ini";
    internal const string LogFileName = "pochd.log";

    /// <summary>The provider built from tools/HdCards; the bootstrap and notices are those of the aspect bundle.</summary>
    internal static DisplayComponent Provider { get; } =
        new("ddraw.dll", 518144, "c8ec14cd5059c9ac7b970d06cda3a0b27dd7473a14cc7050c63ae4a7bec553e4", true);

    internal static IReadOnlyList<DisplayComponent> Required { get; } = Array.AsReadOnly(
        new[] { Provider }.Concat(OriginalAspectRatioComponents.Required.Where(component => component.FileName != "ddraw.dll")).ToArray());

    internal static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Components", DirectoryName);
}

/// <summary>Read-only facts the interface needs about the HD card artwork mod.</summary>
public static class HdCardArtworkSupport
{
    public const string ArtworkDownloadName = "Power-of-Chaos-HD-Card-Artwork-v2.zip";

    /// <summary>True when every file of the HD component bundle is present. Contents are verified on Play.</summary>
    public static bool ComponentPresent(string? componentDirectory = null)
    {
        var directory = componentDirectory ?? HdCardArtworkComponents.DefaultDirectory;
        try { return HdCardArtworkComponents.Required.All(component => File.Exists(Path.Combine(directory, component.FileName))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}

/// <summary>Locates the user's HD card pictures. The folder is only listed; pictures are read by the game process.</summary>
public static class HdCardImages
{
    private const int MaximumEntries = 8192;
    private const string SharedBack = "card-back-shared.png";

    /// <summary>The folder that directly holds the pictures: the given folder, or its "images" child.</summary>
    public static string? Locate(string? directory)
    {
        // Picture paths must stay within the classic Windows path limit the provider uses.
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 180 || directory.Any(char.IsControl)
            || !Path.IsPathFullyQualified(directory)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            foreach (var candidate in new[] { full, Path.Combine(full, "images") })
                if (Directory.Exists(candidate) && Pictures(candidate).Any()) return candidate;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException) { }
        return null;
    }

    /// <summary>Number of card pictures in a folder returned by <see cref="Locate"/>.</summary>
    public static int Count(string directory)
    {
        try { return Pictures(directory).Count(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException) { return 0; }
    }

    internal static string Require(string? directory) => Locate(directory) ?? throw new InvalidDataException(
        "HD card artwork needs the folder with the HD card pictures. Open Mods and choose the image folder, or untick this mod.");

    internal static bool IsPicture(string fileName)
    {
        if (fileName.Equals(SharedBack, StringComparison.OrdinalIgnoreCase)) return true;
        return fileName.Length > 9 && fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiDigit(fileName[0]) && char.IsAsciiDigit(fileName[1]) && char.IsAsciiDigit(fileName[2])
            && char.IsAsciiDigit(fileName[3]) && fileName[4] == ' ';
    }

    private static IEnumerable<string> Pictures(string directory) =>
        Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly).Take(MaximumEntries)
            .Where(path => IsPicture(Path.GetFileName(path)));

    /// <summary>Settings read by the provider beside the private executable. UTF-8, one key per line.</summary>
    internal static string CreateSettings(string imageDirectory)
    {
        var path = Path.GetFullPath(imageDirectory);
        if (path.Any(character => character is '\r' or '\n' or '\0'))
            throw new InvalidDataException("The HD card image folder cannot be written to the provider settings. Choose a folder with an ordinary name.");
        // Wine maps an absolute Unix host path through Z:, while Windows already supplies a drive path.
        if (!OperatingSystem.IsWindows()) path = "Z:" + path.Replace('/', '\\');
        return new StringBuilder().Append("images=").Append(path).Append('\n').ToString();
    }
}
