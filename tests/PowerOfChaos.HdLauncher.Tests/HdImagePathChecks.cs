using System.Text;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;
using PowerOfChaos.Launcher.Core.Localization;
using PowerOfChaos.Launcher.Core.Storage;

namespace PowerOfChaos.HdLauncher.Tests;

internal static partial class Program
{
    private static async Task RunImagePathChecks(string root, Func<string, Func<Task>, Task> check)
    {
        await check("old preferences default to pack images and invalid image choices stay explicit", async () =>
        {
            var store = new SettingsStore(Path.Combine(root, "image-preferences"));
            Require((await store.LoadAsync()).HdCardImageDirectory == "", "Default image choice is not blank.");
            await store.SaveAsync(new LauncherSettings { HdCardImageDirectory = "relative-invalid" });
            var loaded = await store.LoadAsync();
            Require(loaded.HdCardImageDirectory.Length > 0, "Invalid image selection became automatic.");
            await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(root, "yugi", default, loaded.HdCardImageDirectory));
        });
        foreach (var game in GameCatalog.All)
            await check(game.Id + " relocation with old master folder missing creates immutable path snapshots", async () =>
            {
                var fixture = await Fixture.Create(Path.Combine(root, "relocate", game.Id), game.Id);
                var old = Path.Combine(fixture.Pack, "old-images"); Directory.CreateDirectory(old);
                var source = Path.Combine(fixture.Pack, "master.png"); var oldMaster = Path.Combine(old, "master.png");
                var config = Path.Combine(fixture.Pack, game.Id + "-565.tsv");
                await File.WriteAllTextAsync(config, (await File.ReadAllTextAsync(config)).Replace(source, oldMaster, StringComparison.Ordinal));
                var before = await File.ReadAllBytesAsync(config); var originalMaster = await File.ReadAllBytesAsync(source);
                var first = Path.Combine(root, "relocated-images", game.Id, "one");
                var second = Path.Combine(root, "relocated-images", game.Id, "two");
                Directory.CreateDirectory(first); Directory.CreateDirectory(second);
                File.Move(source, Path.Combine(first, "master.png")); File.Copy(Path.Combine(first, "master.png"), Path.Combine(second, "master.png"));
                Directory.Delete(old);
                var options = new GameModOptions(true, true, true, true, fixture.Pack, first);
                var output = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options);
                var snapshotPath = Path.Combine(Path.GetDirectoryName(output)!, HdCardPack.ConfigurationFileName);
                var snapshot = await File.ReadAllBytesAsync(snapshotPath);
                Require(Encoding.UTF8.GetString(snapshot).Contains(Path.Combine(first, "master.png"), StringComparison.Ordinal)
                    && !Encoding.UTF8.GetString(snapshot).Contains(oldMaster, StringComparison.Ordinal), "Override did not reach actual config.");
                var next = await fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { HdCardImageDirectory = second });
                Require(next != output && File.ReadAllBytes(snapshotPath).SequenceEqual(snapshot), "Later selection changed or reused the earlier snapshot.");
                var plan = LaunchPlan.CreatePrepared(fixture.Game, Settings(options), output, true);
                Require(plan.Environment["POC_HD_CONFIG"] == snapshotPath, "Child did not use the replaced snapshot.");
                Require(File.ReadAllBytes(config).SequenceEqual(before) && File.ReadAllBytes(Path.Combine(first, "master.png")).SequenceEqual(originalMaster), "Pack or image was modified.");
                await Expect<InvalidDataException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options with { HdCardImageDirectory = "" }));
                File.Delete(Path.Combine(first, "master.png"));
                await Expect<IOException>(() => fixture.Preparer.PrepareExecutableAsync(fixture.Game, options));
                Require(File.ReadAllBytes(snapshotPath).SequenceEqual(snapshot), "Missing image changed old config.");
            });
        await check("invalid selected PNGs never fall back to available old images", async () =>
        {
            var fixture = await Fixture.Create(Path.Combine(root, "invalid-images"), "yugi");
            var images = Path.Combine(root, "bad-images"); Directory.CreateDirectory(images);
            var file = Path.Combine(images, "master.png"); var header = File.ReadAllBytes(Path.Combine(fixture.Pack, "master.png"));
            foreach (var offset in new[] { 16, 20, 24, 25, 26, 27, 28 })
            {
                var wrong = header.ToArray(); wrong[offset] ^= 1; await File.WriteAllBytesAsync(file, wrong);
                await Expect<IOException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default, images));
            }
            File.Delete(file);
            await Expect<IOException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default, images));
            foreach (var path in new[] { "relative", @"\\server\images", Path.Combine(images, "..", "bad-images"), Path.Combine(images, "CON") })
                await Expect<InvalidDataException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default, path));
            await Expect<DirectoryNotFoundException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", default, Path.Combine(images, "missing")));
            await Expect<OperationCanceledException>(() => HdCardPack.ReadAsync(fixture.Pack, "yugi", new CancellationToken(true), images));
            Require(File.Exists(Path.Combine(fixture.Pack, "master.png")), "Old image was changed.");
        });
        await check("invalid explicit images never switch the automatically detected prepared pack", async () =>
        {
            var app = Path.Combine(root, "authoritative-app"); var data = Path.Combine(root, "authoritative-data");
            var appPack = Path.Combine(app, "HdCards"); var dataPack = Path.Combine(data, "HdCards");
            var images = Path.Combine(root, "only-alternate-images");
            Directory.CreateDirectory(appPack); Directory.CreateDirectory(dataPack); Directory.CreateDirectory(images);
            foreach (var game in GameCatalog.All)
            {
                var first = await Fixture.Create(Path.Combine(root, "authoritative-inputs", game.Id), game.Id);
                var config = await File.ReadAllTextAsync(Path.Combine(first.Pack, game.Id + "-565.tsv"));
                File.Copy(Path.Combine(first.Pack, "master.png"), Path.Combine(images, game.Id + "-alternate.png"));
                await File.WriteAllTextAsync(Path.Combine(appPack, game.Id + "-565.tsv"), config);
                await File.WriteAllTextAsync(Path.Combine(dataPack, game.Id + "-565.tsv"), config.Replace("master.png", game.Id + "-alternate.png", StringComparison.Ordinal));
            }
            await Expect<IOException>(() => HdCardPackLocation.ResolveDirectoryAsync("", app, data, imageDirectory: images));
        });
        await check("image filename diagnostics translate while preserving filename data in all six languages", () =>
        {
            var text = LauncherText.Current; const string name = "Missing 日本語 {0}.png";
            try
            {
                foreach (var language in new[] { "en", "de", "es", "fr", "it", "ja" })
                {
                    text.SetLanguage(language);
                    foreach (var template in new[] { "An HD card PNG is missing or unreadable: {0}", "An HD pack file is missing or unreadable: {0}" })
                    {
                        var english = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, name);
                        var rendered = text.Translate(english);
                        Require(rendered == text.Format(template, name) && rendered.Contains(name, StringComparison.Ordinal), "Diagnostic data or translation changed.");
                        Require(language == "en" || rendered != english, "Diagnostic stayed English.");
                    }
                }
            }
            finally { text.SetLanguage("en"); }
            return Task.CompletedTask;
        });
        await check("pending image inspection rejects late older folder results", async () =>
        {
            var old = new TaskCompletionSource<HdCardArtworkStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var tracker = new HdCardArtworkAvailabilityTracker((pack, image, _) => image == "old" ? old.Task : Task.FromResult(new HdCardArtworkStatus(pack, image, null)));
            var pending = tracker.RefreshAsync("same-pack", "old"); await tracker.RefreshAsync("same-pack", "new");
            old.SetResult(new("same-pack", "old", null)); await pending;
            Require(tracker.Status.MasterDirectory == "new", "Old image result replaced newer choice.");
        });
    }
}
