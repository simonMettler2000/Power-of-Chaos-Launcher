using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Discovery;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

internal static class Program
{
    private static int passed;
    private static int skipped;

    public static async Task<int> Main(string[] args)
    {
        using var fixtures = new FixtureScope();
        try
        {
            await Run("new settings begin without game paths", async () =>
            {
                var settings = await new SettingsStore(fixtures.Folder("empty-settings")).LoadAsync();
                Equal(1, settings.SchemaVersion, "schema");
                Equal(0, settings.SearchRoots.Count, "search roots");
                Equal(0, settings.Installations.Count, "installations");
                Equal("wine", settings.WineExecutable, "default Wine command");
            });

            await Run("settings survive store recreation with paths and preferences", async () =>
            {
                var directory = fixtures.Folder("persist-settings");
                var game = fixtures.Game("persistent-game", "kaiba");
                var expected = new LauncherSettings
                {
                    SearchRoots = [game.DiscoveryRoot],
                    Installations = [game],
                    MinimizeOnLaunch = false,
                    WineExecutable = "/opt/example runner/bin/wine"
                };
                await new SettingsStore(directory).SaveAsync(expected);
                var actual = await new SettingsStore(directory).LoadAsync();
                Equal(1, actual.SearchRoots.Count, "persisted root count");
                Equal(expected.SearchRoots[0], actual.SearchRoots[0], "persisted root");
                Equal(game, actual.Installations.Single(), "persisted installation");
                Equal(false, actual.MinimizeOnLaunch, "minimize preference");
                Equal(expected.WineExecutable, actual.WineExecutable, "Wine command with spaces");
                Require(!Directory.EnumerateFiles(directory, "*.tmp*").Any(), "Temporary settings file leaked.");
            });

            await Run("corrupt JSON is preserved and recovers to empty settings", async () =>
            {
                var directory = fixtures.Folder("corrupt-settings");
                var corrupt = "{ malformed launcher settings ]";
                await File.WriteAllTextAsync(Path.Combine(directory, "launcher-settings.json"), corrupt);
                var settings = await new SettingsStore(directory).LoadAsync();
                Equal(0, settings.Installations.Count, "recovered installations");
                Equal(0, settings.SearchRoots.Count, "recovered roots");
                var backups = Directory.EnumerateFiles(directory, "launcher-settings.corrupt-*.json").ToArray();
                Equal(1, backups.Length, "corrupt backup count");
                Equal(corrupt, await File.ReadAllTextAsync(backups[0]), "preserved malformed content");
            });

            await Run("null settings collections are sanitized", async () =>
            {
                var directory = fixtures.Folder("null-settings");
                await File.WriteAllTextAsync(Path.Combine(directory, "launcher-settings.json"),
                    "{\"SchemaVersion\":1,\"SearchRoots\":null,\"Installations\":null,\"WineExecutable\":null}");
                var settings = await new SettingsStore(directory).LoadAsync();
                Require(settings.SearchRoots is not null && settings.Installations is not null,
                    "Null collections escaped storage validation.");
                Require(!string.IsNullOrWhiteSpace(settings.WineExecutable), "Null Wine command escaped validation.");
            });

            await Run("unrelated and missing folders yield no game", async () =>
            {
                var directory = fixtures.Folder("unrelated");
                await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "Launcher-authored test note.");
                var discovery = new GameDiscovery();
                Equal(0, (await discovery.ScanAsync(directory)).Games.Count, "unrelated folder");
                var missing = await discovery.ScanAsync(Path.Combine(directory, "does-not-exist"));
                Equal(0, missing.Games.Count, "missing folder");
                Require(missing.Warnings.Count > 0, "Missing path needs a diagnostic.");
            });

            foreach (var id in new[] { "yugi", "kaiba", "joey" })
            {
                await Run($"recognizes {id} directly without executing its fixture", async () =>
                {
                    var game = fixtures.Game("direct-" + id, id);
                    var result = await new GameDiscovery().ScanAsync(game.DiscoveryRoot);
                    Equal(1, result.Games.Count, "direct game count");
                    Equal(id, result.Games[0].GameId, "direct game identity");
                    Equal(game.ExecutablePath, result.Games[0].ExecutablePath, "direct executable");
                });
            }

            await Run("one parent discovers all three games at depths two, three and six", async () =>
            {
                var root = fixtures.Folder("all-games");
                fixtures.GameAt(Nested(root, 2, "yugi"), "yugi");
                fixtures.GameAt(Nested(root, 3, "kaiba"), "kaiba");
                fixtures.GameAt(Nested(root, 6, "joey"), "joey");
                var result = await new GameDiscovery().ScanAsync(root);
                Equal(3, result.Games.Count, "nested game count");
                SetEqual(new[] { "yugi", "kaiba", "joey" }, result.Games.Select(g => g.GameId), "nested identities");
                Require(!result.LimitReached, "Small scan unexpectedly reached its budget.");
            });

            await Run("depth boundary includes six and excludes seven", async () =>
            {
                var root = fixtures.Folder("depth-boundary");
                fixtures.GameAt(Nested(root, 6, "within"), "yugi");
                fixtures.GameAt(Nested(root, 7, "beyond"), "kaiba");
                var result = await new GameDiscovery().ScanAsync(root, new DiscoveryOptions(MaxDepth: 6));
                SetEqual(new[] { "yugi" }, result.Games.Select(g => g.GameId), "depth-six boundary");
            });

            await Run("zero depth only examines the selected folder", async () =>
            {
                var root = fixtures.Folder("depth-zero");
                fixtures.GameAt(root, "yugi");
                fixtures.GameAt(Path.Combine(root, "child"), "joey");
                var result = await new GameDiscovery().ScanAsync(root, new DiscoveryOptions(MaxDepth: 0));
                SetEqual(new[] { "yugi" }, result.Games.Select(g => g.GameId), "zero-depth identities");
            });

            await Run("directory visit budget is enforced", async () =>
            {
                var root = fixtures.Folder("directory-budget");
                for (var i = 0; i < 8; i++) Directory.CreateDirectory(Path.Combine(root, "child-" + i));
                var result = await new GameDiscovery().ScanAsync(root, new DiscoveryOptions(MaxDirectories: 2));
                Require(result.LimitReached, "Scan did not report reaching the directory budget.");
                Require(result.DirectoriesVisited <= 2, "Scan exceeded its declared directory budget.");
            });

            await Run("cancellation stops discovery", async () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => new GameDiscovery().ScanAsync(
                    fixtures.Folder("cancelled"), cancellationToken: cancellation.Token));
            });

            await Run("an executable path resolves its game directory", async () =>
            {
                var game = fixtures.Game("selected-exe", "joey");
                var result = await new GameDiscovery().ScanAsync(game.ExecutablePath);
                Equal(1, result.Games.Count, "selected executable result");
                Equal("joey", result.Games[0].GameId, "selected executable identity");
            });

            await Run("file names are matched case-insensitively", async () =>
            {
                var directory = fixtures.Folder("mixed-case");
                fixtures.WriteGameFiles(directory, "YUGI_PC.EXE", "DaTa.DaT", "vOiCe.DaT");
                var result = await new GameDiscovery().ScanAsync(directory);
                Equal("yugi", result.Games.Single().GameId, "mixed-case game");
                Require(File.Exists(result.Games[0].DataPath), "Discovered data path lost its actual file casing.");
            });

            await Run("multiple installations do not collapse by game title", async () =>
            {
                var root = fixtures.Folder("multiple-installations");
                fixtures.GameAt(Path.Combine(root, "first"), "yugi");
                fixtures.GameAt(Path.Combine(root, "second"), "yugi");
                var result = await new GameDiscovery().ScanAsync(root);
                Equal(2, result.Games.Count, "separate same-title installs");
                Equal(2, result.Games.Select(g => g.ExecutablePath).Distinct().Count(), "unique executable paths");
            });

            await Run("wrong filenames and renamed copy variants are not games", async () =>
            {
                var root = fixtures.Folder("wrong-names");
                fixtures.WriteGameFiles(root, "yugi_pc - Copy.exe");
                fixtures.WritePe(Path.Combine(root, "not-a-game.exe"));
                Equal(0, (await new GameDiscovery().ScanAsync(root)).Games.Count, "wrong executable names");
            });

            await Run("invalid PE files are rejected", async () =>
            {
                foreach (var kind in new[] { "plain-text", "truncated", "wrong-machine", "dll", "wrong-optional-magic", "missing-section-table" })
                {
                    var root = fixtures.Folder("invalid-pe-" + kind);
                    fixtures.WriteGameFiles(root, "yugi_pc.exe");
                    var exe = Path.Combine(root, "yugi_pc.exe");
                    switch (kind)
                    {
                        case "plain-text": await File.WriteAllTextAsync(exe, "Not an executable."); break;
                        case "truncated": await File.WriteAllBytesAsync(exe, new byte[32]); break;
                        case "wrong-machine": PatchUInt16(exe, 0x84, 0x8664); break;
                        case "dll": PatchUInt16(exe, 0x96, 0x2002); break;
                        case "wrong-optional-magic": PatchUInt16(exe, 0x98, 0x020B); break;
                        case "missing-section-table": using (var stream = new FileStream(exe, FileMode.Open, FileAccess.Write)) stream.SetLength(0x98 + 0xE0); break;
                    }
                    Equal(0, (await new GameDiscovery().ScanAsync(root)).Games.Count, "invalid PE: " + kind);
                }
            });

            await Run("missing or damaged companion archives are rejected", async () =>
            {
                foreach (var kind in new[] { "missing-data", "missing-voice", "bad-data-magic", "bad-voice-magic", "zero-entries", "truncated-directory", "excessive-count" })
                {
                    var root = fixtures.Folder("invalid-archive-" + kind);
                    fixtures.WriteGameFiles(root, "kaiba_pc.exe");
                    var data = Path.Combine(root, "data.dat");
                    var voice = Path.Combine(root, "Voice.dat");
                    switch (kind)
                    {
                        case "missing-data": File.Delete(data); break;
                        case "missing-voice": File.Delete(voice); break;
                        case "bad-data-magic": await File.WriteAllTextAsync(data, "Not a game archive."); break;
                        case "bad-voice-magic": await File.WriteAllTextAsync(voice, "Not a voice archive."); break;
                        case "zero-entries": PatchUInt32(data, 8, 0); break;
                        case "truncated-directory": using (var stream = new FileStream(data, FileMode.Open, FileAccess.Write)) stream.SetLength(12); break;
                        case "excessive-count": PatchUInt32(data, 8, 100001); break;
                    }
                    Equal(0, (await new GameDiscovery().ScanAsync(root)).Games.Count, "bad archive: " + kind);
                }
            });

            await Run("symbolic-link children cannot escape the chosen tree", async () =>
            {
                var root = fixtures.Folder("symlink-root");
                var outside = fixtures.Game("symlink-outside", "joey");
                var link = Path.Combine(root, "outside-link");
                try { Directory.CreateSymbolicLink(link, outside.DiscoveryRoot); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    throw new SkipCheckException("Symbolic-link setup unavailable: " + exception.GetType().Name);
                }
                var result = await new GameDiscovery().ScanAsync(root);
                Equal(0, result.Games.Count, "outside symlink target");
                Require(result.SkippedDirectories > 0, "Skipped symlink should be reflected in the scan result.");
            });

            await Run("discovery does not modify files it examines", async () =>
            {
                var game = fixtures.Game("read-only-inspection", "yugi");
                var paths = new[] { game.ExecutablePath, game.DataPath, game.VoicePath };
                var before = paths.ToDictionary(p => p, File.ReadAllBytes);
                _ = await new GameDiscovery().ScanAsync(game.DiscoveryRoot);
                foreach (var path in paths) Require(before[path].SequenceEqual(await File.ReadAllBytesAsync(path)), "Inspection modified " + Path.GetFileName(path));
            });

            await SessionChecks.RunAll(fixtures.Root, Run);
            await SplashChecks.RunAll(fixtures.Root, Run);
            await InteractionChecks.RunAll(fixtures.Root, Run);
            await PortraitChecks.RunAll(fixtures.Root, Run);

            if (args.Contains("--installed-mods", StringComparer.Ordinal))
                await InteractionChecks.VerifyInstalledProfiles(Run);

            if (args.Contains("--installed", StringComparer.Ordinal))
            {
                await Run("read-only installed KONAMI discovery", async () =>
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "KONAMI");
                    Require(Directory.Exists(path), "Installed integration root not present: " + path);
                    var result = await new GameDiscovery().ScanAsync(path);
                    SetEqual(new[] { "yugi", "kaiba", "joey" }, result.Games.Select(g => g.GameId).Distinct(), "installed identities");
                    Equal(3, result.Games.Count, "installed game count");
                    foreach (var game in result.Games) Require(File.Exists(game.ExecutablePath), "Installed result must point at existing file.");
                    Console.WriteLine("Installed integration only read headers; no game process was started.");
                });
            }

            Console.WriteLine($"PASS {passed} checks; {skipped} optional platform setup skips.");
            return 0;
        }
        catch (Exception exception)
        {
            fixtures.Preserve = true;
            Console.Error.WriteLine($"FAIL after {passed} checks: {exception}");
            Console.Error.WriteLine("Synthetic fixtures preserved at " + fixtures.Root);
            return 1;
        }
    }

    private static async Task Run(string name, Func<Task> action)
    {
        try { await action(); }
        catch (SkipCheckException exception)
        {
            skipped++;
            Console.WriteLine("SKIP " + name + ": " + exception.Message);
            return;
        }
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private sealed class SkipCheckException(string message) : Exception(message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}.");
    }

    private static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        Require(new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(actual), message);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static string Nested(string root, int depth, string prefix)
    {
        var path = root;
        for (var i = 0; i < depth; i++) path = Path.Combine(path, prefix + "-" + i);
        return path;
    }

    private static void PatchUInt16(string path, long position, ushort value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        stream.Position = position;
        writer.Write(value);
    }

    private static void PatchUInt32(string path, long position, uint value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        stream.Position = position;
        writer.Write(value);
    }

    private sealed class FixtureScope : IDisposable
    {
        private readonly string fixtureBase;
        public string Root { get; }
        public bool Preserve { get; set; }

        public FixtureScope()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerOfChaos.Launcher.Tests.csproj"))) directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException("Cannot find tests project for ignored fixture storage.");
            fixtureBase = Path.GetFullPath(Path.Combine(directory.FullName, ".local", "test-fixtures"));
            Root = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Folder(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public GameInstallation Game(string name, string game) => GameAt(Folder(name), game);

        public GameInstallation GameAt(string folder, string game)
        {
            var executable = GameCatalog.Get(game).ExecutableName;
            WriteGameFiles(folder, executable);
            return new GameInstallation(game, Path.Combine(folder, executable), Path.Combine(folder, "data.dat"), Path.Combine(folder, "Voice.dat"), folder);
        }

        public void WriteGameFiles(string directory, string executable, string dataName = "data.dat", string voiceName = "Voice.dat")
        {
            Directory.CreateDirectory(directory);
            WritePe(Path.Combine(directory, executable));
            WriteArchive(Path.Combine(directory, dataName));
            WriteArchive(Path.Combine(directory, voiceName));
        }

        public void WritePe(string path)
        {
            // Synthetic inert structure only. No game code, executable section or original bytes.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            stream.SetLength(512);
            writer.Write((ushort)0x5A4D);
            stream.Position = 0x3C;
            writer.Write(0x80);
            stream.Position = 0x80;
            writer.Write(0x00004550);
            writer.Write((ushort)0x014C);
            writer.Write((ushort)1);
            stream.Position = 0x94;
            writer.Write((ushort)0x00E0);
            writer.Write((ushort)0x0002);
            writer.Write((ushort)0x010B);
        }

        private static void WriteArchive(string path)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("KCEJYUGI"));
            writer.Write(1u);
            stream.SetLength(12 + 268);
        }

        public void Dispose()
        {
            if (Preserve) return;
            var target = Path.GetFullPath(Root);
            var prefix = Path.GetFullPath(fixtureBase).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
                throw new InvalidOperationException("Refusing to remove an unverified fixture path.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
