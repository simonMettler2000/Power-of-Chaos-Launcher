using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.Launcher.Tests;

internal static class AspectRatioChecks
{
    internal static IReadOnlyList<GameModOptions> Selections { get; } = Enumerable.Range(0, 8)
        .Select(mask => new GameModOptions((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0)).ToArray();

    internal static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        await run("runtime component and notice pins exactly match the canonical setup manifest", () =>
        {
            var canonical = ReadCanonicalPins();
            Require(canonical.Count(component => component.IsDll) == 2 && canonical.Count(component => !component.IsDll) == 8,
                "Canonical bundle must contain two DLLs and all eight attribution notices.");
            Require(canonical.Select(component => component.FileName).Distinct(StringComparer.Ordinal).Count() == canonical.Length,
                "Canonical pins contain duplicate component or notice names.");
            Require(OriginalAspectRatioComponents.Required.Count == canonical.Length,
                "Runtime provider omits a canonical DLL or attribution notice.");
            foreach (var expected in canonical)
                Require(OriginalAspectRatioComponents.Required.SingleOrDefault(component => component.FileName == expected.FileName) == expected,
                    "Runtime pin differs from the canonical bundle: " + expected.FileName);
            return Task.CompletedTask;
        });

        await run("all canonical attribution notices accompany the private executable and reject missing or corrupt notice sources", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "an"));
            var options = new GameModOptions(RestoreOriginalAspectRatio: true);
            var canonicalNames = ReadCanonicalPins().Where(component => !component.IsDll).Select(component => component.FileName)
                .Order(StringComparer.Ordinal).ToArray();
            var missingNotice = Path.Combine(fixture.ComponentDirectory, "Khronos-LICENSE.txt");
            var savedNotice = await File.ReadAllBytesAsync(missingNotice);
            File.Delete(missingNotice);
            await ExpectAsync<FileNotFoundException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
            Require(!Directory.Exists(fixture.Data), "An incomplete notice bundle published a display launch directory.");
            await File.WriteAllBytesAsync(missingNotice, savedNotice);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var directory = Path.GetDirectoryName(path)!;
            Require(Directory.EnumerateFiles(directory, "*.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(canonicalNames),
                "The private executable directory does not contain exactly the complete canonical notice set.");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "display-manifest.json")));
            using var identity = JsonDocument.Parse(manifest.RootElement.GetProperty("Identity").GetString()!);
            var declaredNotices = identity.RootElement.GetProperty("Components").EnumerateArray()
                .Where(component => !component.GetProperty("IsDll").GetBoolean()).Select(component => component.GetProperty("FileName").GetString())
                .Order(StringComparer.Ordinal).ToArray();
            Require(declaredNotices.SequenceEqual(canonicalNames), "The cache identity omits a required attribution notice.");
            foreach (var name in canonicalNames)
            {
                var source = Path.Combine(fixture.ComponentDirectory, name);
                var cached = Path.Combine(directory, name);
                var original = await File.ReadAllBytesAsync(source);
                var corrupt = original.ToArray(); corrupt[^1] ^= 1;
                await File.WriteAllBytesAsync(source, corrupt);
                await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                Require(original.SequenceEqual(await File.ReadAllBytesAsync(cached)), "Corrupt notice source changed a previously verified cache: " + name);
                await File.WriteAllBytesAsync(source, original);
                await File.WriteAllBytesAsync(cached, corrupt);
                Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path
                    && original.SequenceEqual(await File.ReadAllBytesAsync(cached)), "Corrupt cached notice was not repaired: " + name);
            }
            Require(Hash(await File.ReadAllBytesAsync(path)) == Hash(fixture.Original), "Notice propagation changed the selected executable.");
        });

        await run("all three independent preferences default off and persist every combination", async () =>
        {
            var directory = Path.Combine(fixtures, "as");
            var initial = await new SettingsStore(directory).LoadAsync();
            Require(!initial.RestoreOriginalAspectRatio && !initial.SkipSplashScreens && !initial.SkipCharacterInteractions,
                "Fresh settings enable a mod.");
            foreach (var options in Selections.Concat(Selections.Reverse()))
            {
                var settings = Settings(options);
                var save = new SettingsStore(directory).SaveAsync(settings);
                settings.RestoreOriginalAspectRatio = !settings.RestoreOriginalAspectRatio;
                settings.SkipSplashScreens = !settings.SkipSplashScreens;
                settings.SkipCharacterInteractions = !settings.SkipCharacterInteractions;
                await save;
                var loaded = await new SettingsStore(directory).LoadAsync();
                Require(new GameModOptions(loaded.SkipSplashScreens, loaded.SkipCharacterInteractions,
                    loaded.RestoreOriginalAspectRatio) == options, "Atomic preference snapshot did not persist all three values.");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "launcher-settings.json"), "{\"SchemaVersion\":1,\"SkipSplashScreens\":true}");
            var legacy = await new SettingsStore(directory).LoadAsync();
            Require(legacy.SkipSplashScreens && !legacy.RestoreOriginalAspectRatio, "Legacy settings enabled display restoration.");
        });

        foreach (var id in new[] { "yugi", "kaiba", "joey" })
        {
            await run($"{id} all eight mod combinations select independent verified caches and original asset CWD", async () =>
            {
                var fixture = await Fixture.Create(Path.Combine(fixtures, "a" + id[0]), id);
                var paths = new Dictionary<GameModOptions, string>();
                foreach (var options in Selections.Concat(Selections.Reverse()))
                {
                    var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
                    if (paths.TryGetValue(options, out var previous)) Require(previous == path, "Same selection changed cache paths.");
                    paths[options] = path;
                    Require(Hash(await File.ReadAllBytesAsync(path)) == fixture.ExpectedHash(options), "Display changed the selected binary bytes.");
                    var settings = Settings(options);
                    var windows = LaunchPlan.CreatePrepared(fixture.Game, settings, path, isWindows: true);
                    var wine = LaunchPlan.CreatePrepared(fixture.Game, settings, path, isWindows: false);
                    Require(windows.FileName == path && windows.ArgumentList.Count == 0 && !windows.UseShellExecute,
                        "Native display launch added arguments or a shell.");
                    Require(wine.ArgumentList.SequenceEqual(new[] { "start", "/wait", "/unix", path }), "Wine launch changed native game flags.");
                    Require(windows.WorkingDirectory == Path.GetDirectoryName(fixture.Game.ExecutablePath)
                        && wine.WorkingDirectory == windows.WorkingDirectory, "Display launch lost installed Data/Voice working directory.");
                    if (options.RestoreOriginalAspectRatio)
                    {
                        var directory = Path.GetDirectoryName(path)!;
                        var relative = Path.GetRelativePath(fixture.Data, directory).Split(Path.DirectorySeparatorChar);
                        Require(relative.Length == 3 && relative[0] == "display" && relative[1] == "v1"
                            && relative[2].Length == 32 && relative[2].All(Uri.IsHexDigit), "Display cache is not a short versioned bucket.");
                        Require(Path.Combine(directory, "ddraw.ini").Length < 260, "Wrapper module-local INI exceeds MAX_PATH.");
                        Require(!File.Exists(Path.Combine(directory, "data.dat")) && !File.Exists(Path.Combine(directory, "Voice.dat")), "Installed assets were copied.");
                        foreach (var component in fixture.Components)
                            Require(Hash(await File.ReadAllBytesAsync(Path.Combine(directory, component.FileName))) == component.Sha256,
                                "Component or license was not copied and verified.");
                        Require(await File.ReadAllTextAsync(Path.Combine(directory, "ddraw.ini"))
                            == OriginalAspectRatioPreparer.CreateConfiguration(Path.Combine(directory, "screenshots")), "Wrong display recipe.");
                        var manifest = await File.ReadAllTextAsync(Path.Combine(directory, "display-manifest.json"));
                        Require(manifest.Contains(fixture.ExpectedHash(options), StringComparison.Ordinal)
                            && manifest.Contains(Hash(fixture.Original), StringComparison.Ordinal), "Full hashes were lost behind the short cache key.");
                        Require(wine.Environment["WINEDLLOVERRIDES"]!.EndsWith("winmm,ddraw,*winmm,*ddraw=n", StringComparison.Ordinal), "Wine did not request strict native wrappers.");
                    }
                    else
                    {
                        Require(!File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "ddraw.dll"))
                            && !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "winmm.dll")), "Disabled display mode selected a proxy-bearing directory.");
                        if (!options.AnyEnabled) Require(path == fixture.Game.ExecutablePath, "Stock path changed.");
                    }
                }
                Require(paths.Values.Distinct().Count() == 8, "Selections share executable directories.");
                Require(fixture.Original.SequenceEqual(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)), "Installation was modified.");
                var altered = fixture.Original.ToArray(); altered[^1] = 1;
                await File.WriteAllBytesAsync(fixture.Game.ExecutablePath, altered);
                foreach (var options in Selections.Where(options => options.AnyEnabled))
                    await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions()) == fixture.Game.ExecutablePath,
                    "Stock launch unexpectedly depends on cache version validation.");
            });
        }

        await run("display recipe starts windowed with 4:3 geometry and disables only wrapper logic limiting", () =>
        {
            var config = OriginalAspectRatioPreparer.CreateConfiguration(Path.Combine(fixtures, "screenshots"));
            var pairs = config.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Require(pairs.Count(line => line.StartsWith('[')) == 1 && pairs[0] == "[ddraw]", "Game-specific or duplicate config sections appeared.");
            foreach (var expected in new[] { "width=800", "height=600", "windowed=true", "fullscreen=false", "maintas=true", "aspect_ratio=4:3",
                "boxing=false", "maxgameticks=-1", "limiter_type=0", "minfps=0", "savesettings=0", "resizable=true", "border=true",
                "toggle_borderless=true", "toggle_upscaled=false", "renderer=auto", "d3d9_filter=1", "shader=Nearest neighbor", "maxfps=-1",
                "vsync=false", "adjmouse=true", "keyscreenshot=0x00", "singlecpu=false", "fixchilds=0", "vhack=false",
                "hook_peekmessage=false", "posX=-32000", "posY=-32000" })
                Require(pairs.Count(line => line == expected) == 1, "Missing or duplicate INI setting: " + expected);
            Require(pairs.Length == 30, "Unexpected wrapper settings changed the reviewed recipe.");
            var screenshot = pairs.Single(line => line.StartsWith("screenshotdir=", StringComparison.Ordinal))[14..];
            Require(screenshot.Length > 3 && screenshot[1] == ':' && screenshot.EndsWith('\\'), "Screenshots can fall back to the installed CWD.");
            return Task.CompletedTask;
        });

        await run("every required cache file is checked and repaired while source and components remain untouched", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "ac"));
            var options = new GameModOptions(true, true, true);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            var directory = Path.GetDirectoryName(path)!;
            var sources = fixture.Components.ToDictionary(component => component.FileName,
                component => File.ReadAllBytes(Path.Combine(fixture.ComponentDirectory, component.FileName)));
            var stamp = File.GetLastWriteTimeUtc(path);
            _ = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            Require(File.GetLastWriteTimeUtc(path) == stamp, "A valid cache was unnecessarily replaced.");
            foreach (var file in fixture.Components.Select(component => component.FileName)
                .Concat([Path.GetFileName(path), "ddraw.ini", "display-manifest.json"]))
            {
                var target = Path.Combine(directory, file);
                var expected = await File.ReadAllBytesAsync(target);
                var altered = expected.ToArray(); altered[^1] ^= 1;
                await File.WriteAllBytesAsync(target, altered);
                Require(await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == path, "Repair selected a different cache identity.");
                Require(expected.SequenceEqual(await File.ReadAllBytesAsync(target)), "Same-length cache corruption survived: " + file);
                File.Delete(target);
                _ = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
                Require(expected.SequenceEqual(await File.ReadAllBytesAsync(target)), "Missing cache file was not repaired: " + file);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "ddraw.ini"), "[ddraw]\nwindowed=false\nmaxgameticks=60\n");
            _ = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            Require((await File.ReadAllTextAsync(Path.Combine(directory, "ddraw.ini"))).Contains("windowed=true\n", StringComparison.Ordinal), "A later launch inherited changed window settings.");
            await File.WriteAllTextAsync(Path.Combine(directory, "unreviewed.dll"), "Inert authored corruption marker.");
            _ = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            Require(!File.Exists(Path.Combine(directory, "unreviewed.dll")), "Unexpected cache DLL survived verification.");
            foreach (var source in sources)
                Require(source.Value.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(fixture.ComponentDirectory, source.Key))), "Source component was modified.");
            Require(fixture.Original.SequenceEqual(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)), "Source executable was modified.");
            Require(!Directory.EnumerateDirectories(Path.Combine(fixture.Data, "display", "v1"), ".staging-*").Any(), "Staging directory leaked.");
        });

        await run("missing and corrupt local components fail closed even with a previously valid cache", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "ab"));
            var options = new GameModOptions(RestoreOriginalAspectRatio: true);
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
            foreach (var component in fixture.Components)
            {
                var input = Path.Combine(fixture.ComponentDirectory, component.FileName);
                var bytes = await File.ReadAllBytesAsync(input);
                var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
                await File.WriteAllBytesAsync(input, corrupt);
                await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                await File.WriteAllBytesAsync(input, bytes[..^1]);
                await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                File.Delete(input);
                await ExpectAsync<FileNotFoundException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                await File.WriteAllBytesAsync(input, bytes);
            }
            Require(Hash(await File.ReadAllBytesAsync(path)) == Hash(fixture.Original), "Bad source component changed an existing verified cache.");
            var missing = new GameLaunchPreparer(Path.Combine(fixture.Root, "missing-data"), fixture.Profiles, Path.Combine(fixture.Root, "absent"), fixture.Components);
            var runner = new FakeRunner();
            await using var service = new GameSessionService(Path.Combine(fixture.Root, "session"), runner, preparer: missing);
            var result = await service.LaunchAsync(fixture.Game, Settings(options));
            Require(!result.Success && runner.Starts == 0 && result.Error!.Contains("component setup", StringComparison.Ordinal), "Missing components did not give actionable recovery advice.");
        });

        await run("component hashes cannot admit x64 EXEs truncated sections or unsupported optional headers", async () =>
        {
            foreach (var kind in new[] { "x64", "exe", "pe32plus", "sections", "offset", "signature" })
            {
                var fixture = await Fixture.Create(Path.Combine(fixtures, "ap" + kind));
                var dllPath = Path.Combine(fixture.ComponentDirectory, "ddraw.dll");
                var dll = await File.ReadAllBytesAsync(dllPath);
                switch (kind)
                {
                    case "x64": Write16(dll, 0x84, 0x8664); break;
                    case "exe": Write16(dll, 0x96, 2); break;
                    case "pe32plus": Write16(dll, 0x98, 0x20B); break;
                    case "sections": Write16(dll, 0x86, 96); break;
                    case "offset": Write32(dll, 0x3C, uint.MaxValue); break;
                    case "signature": Write32(dll, 0x80, 0); break;
                }
                await File.WriteAllBytesAsync(dllPath, dll);
                var components = fixture.Components.Select(component => component.FileName == "ddraw.dll" ? component with { Sha256 = Hash(dll) } : component).ToArray();
                var preparer = new GameLaunchPreparer(fixture.Data, fixture.Profiles, fixture.ComponentDirectory, components);
                await ExpectAsync<InvalidDataException>(() => preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(false, false, true)));
                Require(!Directory.Exists(fixture.Data), "Wrong PE components published display files.");
            }
        });

        await run("display preparation serializes concurrent requests and honors a real cross-process cache lock", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "al"));
            var options = new GameModOptions(true, false, true);
            var second = new GameLaunchPreparer(fixture.Data, fixture.Profiles, fixture.ComponentDirectory, fixture.Components);
            var paths = await Task.WhenAll(fixture.Preparer.PrepareExecutableAsync(fixture.Game, options), second.PrepareExecutableAsync(fixture.Game, options));
            Require(paths[0] == paths[1], "Concurrent display preparation published different paths.");
            var directory = Path.GetDirectoryName(paths[0])!;
            var lockPath = Path.Combine(Path.GetDirectoryName(directory)!, Path.GetFileName(directory) + ".lock");
            var childPlan = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                childPlan.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            childPlan.ArgumentList.Add("--hold-display-lock"); childPlan.ArgumentList.Add(lockPath);
            using var child = Process.Start(childPlan) ?? throw new InvalidOperationException("Could not start authored lock fixture.");
            try
            {
                Require(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "LOCKED", "Authored child did not own the lock.");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                await ExpectAsync<OperationCanceledException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options, cancellation.Token));
            }
            finally
            {
                await child.StandardInput.WriteLineAsync("release");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Require(child.ExitCode == 0 && await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options) == paths[0], "Cache ownership was not released cleanly.");
        });

        await run("cancelled or unwritable display preparation never publishes or changes installed files", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "ax"));
            var options = new GameModOptions(false, true, true);
            await ExpectAsync<OperationCanceledException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options, new CancellationToken(true)));
            Require(!Directory.Exists(fixture.Data), "Pre-cancelled display preparation wrote output.");
            Directory.CreateDirectory(fixture.Data);
            await File.WriteAllTextAsync(Path.Combine(fixture.Data, "display"), "Owned file blocking display configuration staging.");
            await ExpectAsync<IOException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
            Require(fixture.Original.SequenceEqual(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)), "Failed display preparation changed the installation.");
            var unsafePreparer = new GameLaunchPreparer(Path.GetDirectoryName(fixture.Game.ExecutablePath), fixture.Profiles, fixture.ComponentDirectory, fixture.Components);
            await ExpectAsync<InvalidDataException>(() => unsafePreparer.PrepareExecutableAsync(fixture.Game, options));
        });

        await run("long legacy INI paths fail before publication with shorter-path recovery advice", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "az"));
            var data = Path.Combine(fixture.Root, new string('d', 160));
            var preparer = new GameLaunchPreparer(data, fixture.Profiles, fixture.ComponentDirectory, fixture.Components);
            try { _ = await preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(false, false, true)); throw new InvalidOperationException("Long display path was admitted."); }
            catch (InvalidDataException exception) { Require(exception.Message.Contains("shorter", StringComparison.OrdinalIgnoreCase), "Long path failure has no recovery advice."); }
            Require(!Directory.Exists(data), "Long path created a directory that could cause wrapper INI fallback.");
        });

        foreach (var (label, suffix) in new[] { ("accent", "\u00E9"), ("non-BMP emoji", "\U0001F642") })
        {
            await run($"{label} display paths fail before native cache or installed-directory writes", async () =>
            {
                var fixture = await Fixture.Create(Path.Combine(fixtures, label == "accent" ? "aua" : "aue"));
                var data = Path.Combine(fixture.Root, "d-" + suffix);
                var preparer = new GameLaunchPreparer(data, fixture.Profiles, fixture.ComponentDirectory, fixture.Components);
                var installedDirectory = Path.GetDirectoryName(fixture.Game.ExecutablePath)!;
                var installed = Directory.EnumerateFiles(installedDirectory).ToDictionary(path => path, path => Hash(File.ReadAllBytes(path)));
                Require(Path.Combine(data, "display", "v1", new string('0', 32), "ddraw.ini").Length < 260,
                    "Unicode regression accidentally depends on MAX_PATH rejection.");
                foreach (var options in Selections.Where(options => options.RestoreOriginalAspectRatio))
                {
                    try
                    {
                        _ = await preparer.PrepareExecutableAsync(fixture.Game, options);
                        throw new InvalidOperationException("A non-ASCII native module path was admitted.");
                    }
                    catch (InvalidDataException exception)
                    {
                        Require(exception.Message.Contains("ASCII", StringComparison.Ordinal)
                            && exception.Message.Contains("POC_LAUNCHER_DATA_DIR", StringComparison.Ordinal)
                            && exception.Message.Contains("short", StringComparison.OrdinalIgnoreCase), "Unicode path failure has no actionable ASCII data-path advice.");
                    }
                    Require(!Directory.Exists(data), "Rejected Unicode path created runtime cache output.");
                }
                Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.CreateConfiguration(Path.Combine(data, "screenshots")));
                var runner = new FakeRunner();
                var sessionDirectory = Path.Combine(fixture.Root, "session");
                await using var service = new GameSessionService(sessionDirectory, runner, preparer: preparer);
                var result = await service.LaunchAsync(fixture.Game, Settings(new(false, false, true)));
                Require(!result.Success && runner.Starts == 0 && !Directory.Exists(data) && !Directory.Exists(sessionDirectory),
                    "Rejected Unicode path started a process or wrote runtime state.");
                Require(await preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions()) == fixture.Game.ExecutablePath,
                    "Display-disabled stock selection was affected by the native wrapper path restriction.");
                Require(Directory.EnumerateFiles(installedDirectory).Count() == installed.Count
                    && installed.All(file => Hash(File.ReadAllBytes(file.Key)) == file.Value), "Unicode rejection wrote an installed file or fallback ddraw.ini.");
                Require(Hash(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)) == Hash(fixture.Original),
                    "Unicode path rejection changed the original executable hash.");
            });
        }

        await run("managed component source folders may contain Unicode when the native cache paths are ASCII", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "aup"));
            var source = Path.Combine(fixture.Root, "components-\u00E9-\U0001F642");
            Directory.CreateDirectory(source);
            foreach (var component in fixture.Components)
                await File.WriteAllBytesAsync(Path.Combine(source, component.FileName),
                    await File.ReadAllBytesAsync(Path.Combine(fixture.ComponentDirectory, component.FileName)));
            var preparer = new GameLaunchPreparer(fixture.Data, fixture.Profiles, source, fixture.Components);
            var path = await preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(false, false, true));
            Require(path.All(character => character <= 0x7F), "Native cache output contains a non-ASCII character.");
            Require(Hash(await File.ReadAllBytesAsync(path)) == Hash(fixture.Original)
                && Hash(await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)) == Hash(fixture.Original),
                "Managed Unicode input path changed cached or installed executable bytes.");
            foreach (var component in fixture.Components)
                Require(Hash(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, component.FileName))) == component.Sha256,
                    "Managed Unicode input path lost a component or notice.");
        });

        await run("display cache paths and component inputs reject directory links", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "aj"));
            var target = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(target);
            Directory.CreateDirectory(fixture.Data);
            var link = Path.Combine(fixture.Data, "display");
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { throw new Program.SkipCheckException("Display-link fixture unavailable: " + exception.GetType().Name); }
            try
            {
                await ExpectAsync<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(false, false, true)));
                Require(!Directory.EnumerateFileSystemEntries(target).Any(), "Display cache followed an output directory link.");
            }
            finally { Directory.Delete(link); }
        });

        await run("aspect child environment removes wrapper config overrides and preserves other Wine choices", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "ae"));
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(false, false, true));
            var parent = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => entry.Value);
            var inherited = new Dictionary<string, string?>
            {
                ["CNC_DDRAW_CONFIG_FILE"] = "installed\\ddraw.ini", ["cnc_ddraw_config_file"] = "installed\\ddraw.ini",
                ["CNC_DDRAW_CONFIG_INIT"] = "1", ["Cnc_Ddraw_Config_Init"] = "1",
                ["WINEDLLOVERRIDES"] = "d3d9,WINMM.dll=b;ddraw,dxgi=n,b;*=b;dsound=n;*ddraw=b;*winmm=b;C:\\game\\ddraw.dll=b",
                ["__COMPAT_LAYER"] = "user-choice", ["STEAMDECK"] = "1"
            };
            var before = inherited.ToArray();
            foreach (var windows in new[] { true, false })
            {
                var off = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), fixture.Game.ExecutablePath, windows, inherited);
                Require(inherited.All(entry => off.Environment.TryGetValue(entry.Key, out var value) && value == entry.Value),
                    "Disabled display changed inherited config or DLL overrides.");
                var plan = LaunchPlan.CreatePrepared(fixture.Game, Settings(new(false, false, true)), path, windows, inherited);
                Require(!plan.Environment.Keys.Any(key => key.Equals("CNC_DDRAW_CONFIG_FILE", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("CNC_DDRAW_CONFIG_INIT", StringComparison.OrdinalIgnoreCase)), "Inherited config override can disable or redirect the wrapper.");
                Require(plan.Environment["__COMPAT_LAYER"] == "user-choice" && plan.Environment["STEAMDECK"] == "1", "Unrelated child environment changed.");
                Require(plan.Environment["WINEDLLOVERRIDES"] == (windows ? inherited["WINEDLLOVERRIDES"]
                    : "d3d9=b;dxgi=n,b;*=b;dsound=n;winmm,ddraw,*winmm,*ddraw=n"), "Wine overrides lost unrelated modules or allowed builtin display fallback.");
            }
            Require(LaunchPlan.WithNativeDisplayOverrides(null) == "winmm,ddraw,*winmm,*ddraw=n"
                && LaunchPlan.WithNativeDisplayOverrides("winmm=n;ddraw=b;foo=n,b") == "foo=n,b;winmm,ddraw,*winmm,*ddraw=n", "Duplicate display overrides survived.");
            Require(parent.Count == Environment.GetEnvironmentVariables().Count && parent.All(entry => Equals(Environment.GetEnvironmentVariable(entry.Key), entry.Value)), "Launch plans mutated the parent environment.");
            Require(before.SequenceEqual(inherited), "Launch plans mutated the supplied environment snapshot.");
        });

        await run("aspect-disabled plans refuse proxy-bearing directories including a prior display cache", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "ad"));
            var path = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(true, true, true));
            Expect<InvalidDataException>(() => LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings(), path, isWindows: true));
            var off = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, new GameModOptions(true, true));
            Require(off != path && !File.Exists(Path.Combine(Path.GetDirectoryName(off)!, "winmm.dll")), "Toggling aspect off retained wrapper injection.");
            var settings = new LauncherSettings();
            var plan = LaunchPlan.CreatePrepared(fixture.Game, settings, off, isWindows: false);
            plan.Environment.TryGetValue("WINEDLLOVERRIDES", out var inherited);
            Require(inherited == Environment.GetEnvironmentVariable("WINEDLLOVERRIDES"), "Disabled display changed inherited Wine environment.");
        });

        await run("all three options remain immutable during queued preparation and cancellation", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(fixtures, "aq"));
            var preparer = new PausingPreparer(fixture.Game.ExecutablePath);
            var runner = new FakeRunner();
            var service = new GameSessionService(Path.Combine(fixture.Root, "session"), runner, preparer: preparer);
            var settings = Settings(new(true, true, true));
            var launch = service.LaunchAsync(fixture.Game, settings);
            await preparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            settings.RestoreOriginalAspectRatio = false; settings.SkipSplashScreens = false; settings.SkipCharacterInteractions = false;
            preparer.Release.TrySetResult();
            Require((await launch).Success && preparer.Captured == new GameModOptions(true, true, true) && runner.Starts == 1,
                "Play used options edited during preparation.");
            await service.DisposeAsync();
            var queuedPreparer = new FirstFailsPreparer(fixture.Game.ExecutablePath);
            var queuedRunner = new FakeRunner();
            await using (var queuedService = new GameSessionService(Path.Combine(fixture.Root, "queued"), queuedRunner, preparer: queuedPreparer))
            {
                var first = queuedService.LaunchAsync(fixture.Game, Settings(new(true, false, false)));
                await queuedPreparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var queuedSettings = Settings(new(false, true, true));
                var second = queuedService.LaunchAsync(fixture.Game, queuedSettings);
                queuedSettings.RestoreOriginalAspectRatio = false; queuedSettings.SkipCharacterInteractions = false; queuedSettings.SkipSplashScreens = true;
                queuedPreparer.Release.TrySetResult();
                Require(!(await first).Success && (await second).Success && queuedPreparer.SecondOptions == new GameModOptions(false, true, true)
                    && queuedRunner.Starts == 1, "Queued request read options edited after Play.");
            }
            var cancelling = new PausingPreparer(fixture.Game.ExecutablePath);
            var cancelledRunner = new FakeRunner();
            var cancelledService = new GameSessionService(Path.Combine(fixture.Root, "cancel-session"), cancelledRunner, preparer: cancelling);
            var pending = cancelledService.LaunchAsync(fixture.Game, Settings(new(false, false, true)));
            await cancelling.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancelledService.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Require(!(await pending).Success && cancelledRunner.Starts == 0, "Cancelled aspect launch started a process.");
        });
    }

    private static DisplayComponent[] ReadCanonicalPins()
    {
        using var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "component-pins.json")));
        Require(pins.RootElement.GetProperty("profile").GetString() == OriginalAspectRatioComponents.DirectoryName,
            "Canonical component profile does not match the runtime bundle directory.");
        return new[] { "components", "notices" }.SelectMany(section => pins.RootElement.GetProperty(section).EnumerateArray()
            .Select(component => new DisplayComponent(component.GetProperty("name").GetString()!, component.GetProperty("length").GetInt32(),
                component.GetProperty("sha256").GetString()!, section == "components"))).ToArray();
    }

    private static LauncherSettings Settings(GameModOptions options) => new()
    {
        SkipSplashScreens = options.SkipSplashScreens, SkipCharacterInteractions = options.SkipCharacterInteractions,
        RestoreOriginalAspectRatio = options.RestoreOriginalAspectRatio
    };

    private sealed record Fixture(string Root, string Data, string ComponentDirectory, GameInstallation Game,
        byte[] Original, SplashProfile[] Profiles, DisplayComponent[] Components, GameLaunchPreparer Preparer)
    {
        internal string ExpectedHash(GameModOptions options) => options.BinaryModsEnabled
            ? Profiles.Single(profile => profile.Options == options.BinaryOptions).ModifiedHash : Hash(Original);

        internal static async Task<Fixture> Create(string root, string id = "yugi")
        {
            var image = SplashChecks.SyntheticImage(); image[0x230] = 7;
            var splash = image.ToArray(); Write32(splash, 0x210, 476); Write32(splash, 0x220, 1);
            var character = image.ToArray(); character[0x230] = 9;
            var both = splash.ToArray(); both[0x230] = 9;
            ScalarChange[] splashFields = [new(0x1010, 0x210, 4294967292, 476), new(0x1020, 0x220, 0, 1)];
            ScalarChange[] characterFields = [new(0x1030, 0x230, 7, 9, 1)];
            SplashProfile[] profiles =
            [
                new(id, image.Length, Hash(image), Hash(splash), splashFields),
                new(id, image.Length, Hash(image), Hash(character), characterFields) { Options = new(false, true) },
                new(id, image.Length, Hash(image), Hash(both), splashFields.Concat(characterFields).ToArray()) { Options = new(true, true) }
            ];
            var game = await SplashChecks.Installation(root, id, image);
            var componentDirectory = Path.Combine(root, "c"); Directory.CreateDirectory(componentDirectory);
            // Authored inert DLL-shaped headers only; no entry point, executable section or machine instructions.
            var ddraw = SplashChecks.SyntheticImage(); Write16(ddraw, 0x96, 0x2002);
            var winmm = ddraw.ToArray(); winmm[^1] = 1;
            var files = new Dictionary<string, byte[]> { ["ddraw.dll"] = ddraw, ["winmm.dll"] = winmm };
            foreach (var notice in OriginalAspectRatioComponents.Required.Where(component => !component.IsDll))
                files.Add(notice.FileName, Encoding.UTF8.GetBytes("Authored synthetic notice fixture: " + notice.FileName + "\n" + new string('L', 256)));
            var components = files.Select(pair => new DisplayComponent(pair.Key, pair.Value.Length, Hash(pair.Value), pair.Key.EndsWith(".dll", StringComparison.Ordinal))).ToArray();
            foreach (var file in files) await File.WriteAllBytesAsync(Path.Combine(componentDirectory, file.Key), file.Value);
            var data = Path.Combine(root, "d");
            return new(root, data, componentDirectory, game, image, profiles, components,
                new GameLaunchPreparer(data, profiles, componentDirectory, components));
        }
    }

    private sealed class PausingPreparer(string path) : IGameLaunchPreparer
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GameModOptions? Captured { get; private set; }
        public async Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options, CancellationToken cancellationToken)
        { Captured = options; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return path; }
    }
    private sealed class FirstFailsPreparer(string path) : IGameLaunchPreparer
    {
        private int _calls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GameModOptions? SecondOptions { get; private set; }
        public async Task<string> PrepareExecutableAsync(GameInstallation installation, GameModOptions options, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
                throw new InvalidDataException("Authored first-request failure fixture.");
            }
            SecondOptions = options; return path;
        }
    }
    private sealed class FakeRunner : IGameProcessRunner
    {
        internal int Starts { get; private set; }
        public IGameProcess Start(ProcessStartInfo startInfo) { Starts++; return new FakeProcess(); }
    }
    private sealed class FakeProcess : IGameProcess
    {
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public void Dispose() { }
    }
    private static void Write16(byte[] image, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), value);
    private static void Write32(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
