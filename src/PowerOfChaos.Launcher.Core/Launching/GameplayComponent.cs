using System.Security.Cryptography;
using System.Text;

namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Factual pin of the gameplay component built from tools/GameplayComponent. The binary stays outside the source tree.</summary>
internal static class GameplayComponents
{
    internal const string DirectoryName = "gameplay-v1";
    internal const string SettingsFileName = "pocgp.ini";
    internal const string LogFileName = "pocgp.log";

    /// <summary>The games import this system library by name; the component stands in for it beside the private executable.</summary>
    internal static DisplayComponent Library { get; } =
        new("msacm32.dll", 122880, "3745a071f3cef16a0a5ef5d8511f0f52fd2491c981153fd3748255baa99e33ce", true);

    internal static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Components", DirectoryName);

    /// <summary>Settings read by the component beside the private executable. UTF-8, one key per line.</summary>
    internal static string CreateSettings(GameModOptions options, IEnumerable<string> deckFileNames)
    {
        var settings = new StringBuilder().Append("new_card_rewards=").Append(options.NewCardRewards ? '1' : '0').Append('\n');
        // Plain file names: the component looks them up beside itself.
        foreach (var name in deckFileNames) settings.Append("opponent_deck=").Append(name).Append('\n');
        return settings.ToString();
    }
}

/// <summary>Read-only facts the interface needs about the mods that use the gameplay component.</summary>
public static class GameplaySupport
{
    /// <summary>True when the gameplay component is present. Its content is verified on Play.</summary>
    public static bool ComponentPresent(string? componentDirectory = null)
    {
        var directory = componentDirectory ?? GameplayComponents.DefaultDirectory;
        try { return File.Exists(Path.Combine(directory, GameplayComponents.Library.FileName)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}

/// <summary>The files a launch with the gameplay component adds beside the private executable.</summary>
internal sealed class GameplayLaunchFiles
{
    private const string MissingMessage = "The gameplay component is missing beside the launcher. Untick New card rewards and Custom opponent decks to play without it. Missing: ";
    private const string DamagedMessage = "The gameplay component is damaged or the wrong version: {0}. Restore the launcher's Components folder, or untick this mod.";

    private GameplayLaunchFiles(IReadOnlyDictionary<string, byte[]> files, string identity)
    {
        Files = files;
        Identity = identity;
    }

    /// <summary>File name to exact content: the component itself, its settings and the opponent deck files in use.</summary>
    internal IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>Names and hashes of <see cref="Files"/>; part of the private cache identity.</summary>
    internal string Identity { get; }

    /// <summary>How many of the files are opponent decks. The component opens them by an ANSI path beside itself.</summary>
    internal int DeckFileCount { get; private init; }

    /// <summary>Refuses a private directory the component could not open its deck files in, before anything is written there.</summary>
    internal void RequireReachableFrom(string directory)
    {
        if (DeckFileCount == 0) return;
        // Twelve characters short of the classic limit: under Wine the component sees a drive prefix in front.
        if (!directory.All(char.IsAscii) || Files.Keys.Any(name => Path.Combine(directory, name).Length >= 248))
            throw new InvalidDataException("Custom opponent decks need a short launcher data path of plain ASCII characters. Set POC_LAUNCHER_DATA_DIR to a short folder, or untick Custom opponent decks.");
    }

    /// <summary>Reads the component from the launcher's own folder and checks it against its pin. Nothing is written.</summary>
    internal static async Task<GameplayLaunchFiles> ReadAsync(string? componentDirectory, DisplayComponent? library,
        GameModOptions options, IReadOnlyList<KeyValuePair<string, byte[]>> deckFiles, CancellationToken cancellationToken)
    {
        var pin = library ?? GameplayComponents.Library;
        if (Path.GetFileName(pin.FileName) != pin.FileName || !pin.IsDll || pin.Length is < 256 or > 16 * 1024 * 1024
            || pin.Sha256.Length != 64 || !pin.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The gameplay component pin is invalid.");
        var directory = Path.GetFullPath(componentDirectory ?? GameplayComponents.DefaultDirectory);
        var source = Path.Combine(directory, pin.FileName);
        OriginalAspectRatioPreparer.RefuseLinks(source);
        if (!File.Exists(source)) throw new FileNotFoundException(MissingMessage + pin.FileName);
        byte[] bytes;
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
        {
            if (stream.Length != pin.Length) throw new InvalidDataException(string.Format(DamagedMessage, pin.FileName));
            bytes = new byte[pin.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        if (!SkipSplashPatch.MatchesHash(bytes, pin.Sha256)) throw new InvalidDataException(string.Format(DamagedMessage, pin.FileName));
        var settings = Encoding.UTF8.GetBytes(GameplayComponents.CreateSettings(options, deckFiles.Select(file => file.Key)));
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [pin.FileName] = bytes,
            [GameplayComponents.SettingsFileName] = settings
        };
        foreach (var deck in deckFiles)
        {
            if (Path.GetFileName(deck.Key) != deck.Key || !deck.Key.All(char.IsAscii) || !files.TryAdd(deck.Key, deck.Value))
                throw new InvalidDataException("An opponent deck file name is invalid. No game was started.");
        }

        var identity = string.Join('\n', files.Select(file => file.Key + ":" + Hash(file.Value)));
        return new GameplayLaunchFiles(files, identity) { DeckFileCount = deckFiles.Count };
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
