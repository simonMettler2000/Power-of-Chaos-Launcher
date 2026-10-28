using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;

namespace PowerOfChaos.Launcher.Tests;

internal static class PortableDisplayCacheChecks
{
    internal static async Task RunAll(string root, Func<string, Func<Task>, Task> run)
    {
        await run("suitable native display paths stay unchanged and fallback selection creates no directory", () =>
        {
            var preferred = Path.Combine(root, "portable-preferred");
            var selected = WindowsDisplayCacheLocation.Select(preferred);
            Require(selected.Root == Path.GetFullPath(Path.Combine(preferred, "display", "v1")), "A suitable preferred path changed.");
            Require(!Directory.Exists(preferred), "Path selection created output.");
            Require(!WindowsDisplayCacheLocation.CanUseNativeRoot(Path.Combine(preferred, "日本語"))
                && !WindowsDisplayCacheLocation.CanUseNativeRoot(new string('a', 250)), "An unrepresentable native root was admitted.");
            if (OperatingSystem.IsWindows())
            {
                var automatic = WindowsDisplayCacheLocation.Select(Path.Combine(preferred, "profile-é-🙂"));
                var common = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
                Require(automatic.Root.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && automatic.Root.Contains(WindowsIdentity.GetCurrent().User!.Value, StringComparison.Ordinal)
                    && WindowsDisplayCacheLocation.CanUseNativeRoot(automatic.Root), "Automatic fallback is not a short current-user known-folder path.");
                // Do not initialize the real known-folder cache: all writes below inject an owned synthetic base.
            }
            return Task.CompletedTask;
        });
        if (OperatingSystem.IsWindows()) await RunWindows(root, run);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task RunWindows(string root, Func<string, Func<Task>, Task> run)
    {
        await run("ASCII fallback creates atomically protected current-user directories and reuses them without widening permissions", () =>
        {
            var parent = NewBase(root, "pc-secure");
            var location = WindowsDisplayCacheLocation.FromFallbackBase(parent);
            Require(!Directory.Exists(location.Root), "Fallback selection wrote output.");
            location.EnsureCreated();
            var before = RequirePrivate(location.Root);
            File.WriteAllText(Path.Combine(location.Root, "authored.txt"), "Synthetic private cache marker.");
            location.EnsureCreated();
            Require(RequirePrivate(location.Root) == before && File.ReadAllText(Path.Combine(location.Root, "authored.txt")) == "Synthetic private cache marker.",
                "Reusing a private cache changed its DACL or contents.");
            RequirePrivate(Anchor(location));
            RequirePrivate(Path.GetDirectoryName(location.Root)!);
            return Task.CompletedTask;
        });
        await run("precreated inherited or widened fallback anchors are rejected without permission repair or file adoption", () =>
        {
            foreach (var widened in new[] { false, true })
            {
                var parent = NewBase(root, "pc-untrusted-" + widened);
                var location = WindowsDisplayCacheLocation.FromFallbackBase(parent);
                if (widened)
                {
                    location.EnsureCreated();
                    var directory = new DirectoryInfo(Anchor(location));
                    var acl = directory.GetAccessControl();
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.Read,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    directory.SetAccessControl(acl);
                }
                else Directory.CreateDirectory(Anchor(location));
                var before = new DirectoryInfo(Anchor(location)).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                var marker = Path.Combine(Anchor(location), "untrusted.txt");
                File.WriteAllText(marker, "Synthetic pre-existing bytes.");
                Expect<InvalidDataException>(location.EnsureCreated);
                Require(new DirectoryInfo(Anchor(location)).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All) == before
                    && File.ReadAllText(marker) == "Synthetic pre-existing bytes.", "Untrusted folder permissions or contents were changed.");
            }
            return Task.CompletedTask;
        });
        await run("fallback initialization rejects an unsafe base and missing parents without creating unrelated directories", () =>
        {
            var parent = NewBase(root, "pc-invalid");
            Expect<InvalidDataException>(() => WindowsDisplayCacheLocation.FromFallbackBase(Path.Combine(parent, "é")));
            Expect<InvalidDataException>(() => WindowsDisplayCacheLocation.FromFallbackBase("relative-cache-base"));
            Expect<InvalidDataException>(() => WindowsDisplayCacheLocation.FromFallbackBase(Path.Combine(parent, new string('x', 190))));
            var missing = Path.Combine(parent, "not-created");
            var location = WindowsDisplayCacheLocation.FromFallbackBase(missing);
            Expect<InvalidDataException>(location.EnsureCreated);
            Require(!Directory.Exists(missing), "A missing fallback parent was manufactured.");
            return Task.CompletedTask;
        });
        await run("all three synthetic games prepare in a private ASCII fallback despite Unicode or excessive preferred paths", async () =>
        {
            foreach (var id in GameCatalog.All.Select(game => game.Id))
            foreach (var suffix in new[] { "profile-é-🙂", new string('x', 170) })
            {
                var fixture = await Fixture.Create(NewBase(root, "pc-game-" + id + (suffix.Length > 30 ? "-long" : "-unicode")), id);
                var requested = Path.Combine(fixture.Root, suffix);
                var location = WindowsDisplayCacheLocation.FromFallbackBase(fixture.FallbackBase);
                var preparer = new OriginalAspectRatioPreparer(requested, fixture.ComponentDirectory, fixture.Components, location);
                var output = await preparer.PrepareAsync(fixture.Game, new(RestoreOriginalAspectRatio: true), fixture.Profile, fixture.Image, default);
                Require(output.StartsWith(location.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && output.All(character => character <= 0x7F) && output.Length < 260, "Display output escaped its private ANSI-compatible cache.");
                Require(!Directory.Exists(requested), "The rejected preferred path received output.");
                Require((await File.ReadAllBytesAsync(output)).SequenceEqual(fixture.Image)
                    && (await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)).SequenceEqual(fixture.Image), "Fallback changed the selected or installed bytes.");
                var ini = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!, "ddraw.ini"));
                Require(ini == OriginalAspectRatioPreparer.CreateConfiguration(Path.Combine(Path.GetDirectoryName(output)!, "screenshots")), "Native configuration uses the wrong screenshot location.");
                var plan = LaunchPlan.CreatePrepared(fixture.Game, new LauncherSettings { RestoreOriginalAspectRatio = true }, output);
                Require(plan.WorkingDirectory == Path.GetDirectoryName(fixture.Game.ExecutablePath), "Fallback changed the original asset working directory.");
                RequirePrivate(location.Root);
            }
        });
        await run("fallback output waits for complete input verification and cancellation before directory creation", async () =>
        {
            foreach (var mode in new[] { "missing-component", "bad-executable", "cancelled" })
            {
                var fixture = await Fixture.Create(NewBase(root, "pc-input-" + mode), "yugi");
                var location = WindowsDisplayCacheLocation.FromFallbackBase(fixture.FallbackBase);
                var preparer = new OriginalAspectRatioPreparer(Path.Combine(fixture.Root, "é"), fixture.ComponentDirectory, fixture.Components, location);
                var image = fixture.Image.ToArray();
                var token = mode == "cancelled" ? new CancellationToken(true) : default;
                if (mode == "missing-component") File.Delete(Path.Combine(fixture.ComponentDirectory, "ddraw.dll"));
                if (mode == "bad-executable") image[^1] ^= 1;
                await ExpectFailure(() => preparer.PrepareAsync(fixture.Game, new(RestoreOriginalAspectRatio: true), fixture.Profile, image, token));
                Require(!Directory.Exists(Anchor(location)) && (await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)).SequenceEqual(fixture.Image),
                    "Unverified/cancelled preparation wrote fallback or original bytes.");
            }
        });
        await run("corrupt synthetic fallback caches are repaired from verified inputs while preserving the original executable", async () =>
        {
            var fixture = await Fixture.Create(NewBase(root, "pc-repair"), "joey");
            var location = WindowsDisplayCacheLocation.FromFallbackBase(fixture.FallbackBase);
            var preparer = new OriginalAspectRatioPreparer(Path.Combine(fixture.Root, "é"), fixture.ComponentDirectory, fixture.Components, location);
            var first = await preparer.PrepareAsync(fixture.Game, new(RestoreOriginalAspectRatio: true), fixture.Profile, fixture.Image, default);
            var native = Path.Combine(Path.GetDirectoryName(first)!, "ddraw.dll");
            var expected = await File.ReadAllBytesAsync(native);
            var corrupt = expected.ToArray(); corrupt[^1] ^= 1; await File.WriteAllBytesAsync(native, corrupt);
            var second = await preparer.PrepareAsync(fixture.Game, new(RestoreOriginalAspectRatio: true), fixture.Profile, fixture.Image, default);
            Require(first == second && (await File.ReadAllBytesAsync(native)).SequenceEqual(expected)
                && (await File.ReadAllBytesAsync(fixture.Game.ExecutablePath)).SequenceEqual(fixture.Image), "Fallback repair changed source bytes or trusted corrupt native bytes.");
        });
        await run("private fallback descendants reject widened ACLs even beneath a valid protected anchor", () =>
        {
            var location = WindowsDisplayCacheLocation.FromFallbackBase(NewBase(root, "pc-descendant"));
            location.EnsureCreated();
            var directory = new DirectoryInfo(location.Root);
            var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            Expect<InvalidDataException>(location.EnsureCreated);
            return Task.CompletedTask;
        });
        await run("concurrent private fallback initialization preserves one current-user anchor and exact permissions", async () =>
        {
            var parent = NewBase(root, "pc-concurrent");
            var first = WindowsDisplayCacheLocation.FromFallbackBase(parent);
            var second = WindowsDisplayCacheLocation.FromFallbackBase(parent);
            await Task.WhenAll(Task.Run(first.EnsureCreated), Task.Run(second.EnsureCreated));
            Require(first.Root == second.Root && Directory.EnumerateDirectories(parent).Count() == 1, "Concurrent fallback selection created competing anchors.");
            RequirePrivate(first.Root);
        });
        await run("display overlap boundaries normalize drive roots extended DOS UNC prefixes and sibling names", () =>
        {
            var directory = NewBase(root, "pc-overlap-boundaries");
            var child = Path.Combine(directory, "cache");
            var drive = Path.GetPathRoot(directory)!;
            Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.RefuseOverlap(child, drive));
            Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.RefuseOverlap(drive, child));
            Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.RefuseOverlap(child, @"\\?\" + directory));
            Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.RefuseOverlap(@"\\?\" + directory, child));
            OriginalAspectRatioPreparer.RefuseOverlap(Path.Combine(directory, "alpha"), Path.Combine(directory, "alphabet"));
            Require(OriginalAspectRatioPreparer.NormalizeWindowsPrefix(@"\\?\UNC\server\share\game") == @"\\server\share\game",
                "Extended UNC namespace comparison changed.");
            Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.NormalizeWindowsPrefix(@"\\.\PhysicalDrive0"));
            Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.NormalizeWindowsPrefix(@"\\?\GLOBALROOT\Device\HarddiskVolume1"));
            Require(!Directory.Exists(child), "Boundary checks created an output directory.");
            return Task.CompletedTask;
        });
        await run("extended installation aliases cannot publish a private fallback beneath any supported game", async () =>
        {
            foreach (var game in GameCatalog.All)
            {
                var fixture = await Fixture.Create(Path.Combine(root, "pc-alias-" + game.Id), game.Id);
                var install = NewBase(fixture.Root, "ordinary-game");
                var ordinary = fixture.Game with { ExecutablePath = Path.Combine(install, Path.GetFileName(fixture.Game.ExecutablePath)),
                    DataPath = Path.Combine(install, Path.GetFileName(fixture.Game.DataPath)), VoicePath = Path.Combine(install, Path.GetFileName(fixture.Game.VoicePath)) };
                File.Copy(fixture.Game.ExecutablePath, ordinary.ExecutablePath);
                File.Copy(fixture.Game.DataPath, ordinary.DataPath);
                File.Copy(fixture.Game.VoicePath, ordinary.VoicePath);
                var before = Directory.EnumerateFiles(install).ToDictionary(path => path, path => Hash(File.ReadAllBytes(path)));
                var location = WindowsDisplayCacheLocation.FromFallbackBase(install);
                var alias = ordinary with { ExecutablePath = @"\\?\" + ordinary.ExecutablePath,
                    DataPath = @"\\?\" + ordinary.DataPath, VoicePath = @"\\?\" + ordinary.VoicePath };
                var preparer = new OriginalAspectRatioPreparer(Path.Combine(fixture.Root, "é"), fixture.ComponentDirectory, fixture.Components, location);
                await ExpectFailure(() => preparer.PrepareAsync(alias, new(RestoreOriginalAspectRatio: true), fixture.Profile, fixture.Image, default));
                Require(!Directory.Exists(Anchor(location)) && before.Count == Directory.EnumerateFiles(install).Count()
                    && before.All(item => Hash(File.ReadAllBytes(item.Key)) == item.Value), "An equivalent extended path bypassed installation isolation.");
                Expect<InvalidDataException>(() => OriginalAspectRatioPreparer.RefuseOverlap(install, @"\\?\" + Path.Combine(install, "nested")));
            }
        });
    }

    private static string NewBase(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Anchor(WindowsDisplayCacheLocation location) => Path.GetDirectoryName(Path.GetDirectoryName(location.Root)!)!;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string RequirePrivate(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User!;
        Require(security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner.Equals(user)
            && security.AreAccessRulesProtected, "Fallback ownership/protection is wrong.");
        var expected = new HashSet<string>([user.Value, "S-1-5-18", "S-1-5-32-544"], StringComparer.Ordinal);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        Require(rules.Count == 3, "A fallback principal is missing or unexpected.");
        foreach (FileSystemAccessRule rule in rules)
            Require(expected.Remove(rule.IdentityReference.Value) && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights == FileSystemRights.FullControl && !rule.IsInherited
                && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                && rule.PropagationFlags == PropagationFlags.None, "A fallback permission differs from the private contract.");
        Require(expected.Count == 0, "A trusted fallback principal is missing.");
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.All);
    }

    private sealed record Fixture(string Root, string FallbackBase, string ComponentDirectory, GameInstallation Game,
        byte[] Image, SplashProfile Profile, DisplayComponent[] Components)
    {
        internal static async Task<Fixture> Create(string root, string id)
        {
            var image = SplashChecks.SyntheticImage();
            var game = await SplashChecks.Installation(Path.Combine(root, "installed-é-🙂"), id, image);
            var componentDirectory = Path.Combine(root, "components-é"); Directory.CreateDirectory(componentDirectory);
            var dll = image.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(dll.AsSpan(0x96), 0x2002);
            var bootstrap = dll.ToArray(); bootstrap[^1] = 17;
            var bytes = new Dictionary<string, byte[]> { ["ddraw.dll"] = dll, ["winmm.dll"] = bootstrap,
                ["synthetic-notice.txt"] = Encoding.UTF8.GetBytes("Authored synthetic attribution fixture.\n" + new string('n', 256)) };
            foreach (var pair in bytes) await File.WriteAllBytesAsync(Path.Combine(componentDirectory, pair.Key), pair.Value);
            var components = bytes.Select(pair => new DisplayComponent(pair.Key, pair.Value.Length, Hash(pair.Value), pair.Key.EndsWith(".dll", StringComparison.Ordinal))).ToArray();
            var fallback = Path.Combine(root, "f"); Directory.CreateDirectory(fallback);
            return new(root, fallback, componentDirectory, game, image, new(id, image.Length, Hash(image), Hash(image), []), components);
        }
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Unsafe or cancelled fallback preparation succeeded.");
    }
}
