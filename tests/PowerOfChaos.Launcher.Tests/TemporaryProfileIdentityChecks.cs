using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.TestFixtures;

namespace PowerOfChaos.Launcher.Tests;

internal static class TemporaryProfileIdentityChecks
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> run)
    {
        await run("native profile identity permits ignored padding but rejects every semantic identity field change", () =>
        {
            byte[] header = [7, 13, 29, 43, 61, 79, 101, 127];
            var original = CollectionFixtures.Save([], header);
            for (var field = 0; field < 8; field++)
            {
                var changed = header.ToArray(); changed[field] ^= 255;
                var current = CollectionFixtures.Save([(CollectionFixtures.Slot(17), (byte)2)], changed);
                Require(CollectionSave.SameProfileIdentity(original, current) == (field is >= 1 and <= 3), "Native identity field distinction changed.");
            }
            var damaged = original.ToArray(); damaged[0] ^= 1;
            try { CollectionSave.SameProfileIdentity(original, damaged); throw new Exception("Damaged encoding was accepted."); }
            catch (InvalidDataException) { }
            return Task.CompletedTask;
        });
        await run("ordinary game padding rewrites keep temporary cards active and preserve exact original restoration", async () =>
        {
            var directory = Path.Combine(root, "native-profile-padding"); Directory.CreateDirectory(directory);
            var save = Path.Combine(directory, "system.dat");
            byte[] header = [1, 2, 3, 4, 5, 6, 7, 8];
            var original = CollectionFixtures.Save([(CollectionFixtures.Slot(3), (byte)2)], header);
            await File.WriteAllBytesAsync(save, original);
            var games = GameCatalog.All.Select(game =>
            {
                var folder = Path.Combine(directory, game.Id); Directory.CreateDirectory(folder);
                var data = Path.Combine(folder, "data.dat"); CollectionFixtures.WriteArchive(data);
                return new GameInstallation(game.Id, Path.Combine(folder, game.ExecutableName), data, Path.Combine(folder, "Voice.dat"), directory);
            }).ToArray();
            var profiles = new TemporaryCollectionService(Path.Combine(directory, "vault"), new CardCollectionReader(_ => [save]), () => false);
            await profiles.SetEnabledAsync(true, games);
            var rewrittenHeader = header.ToArray(); rewrittenHeader[1] = 203; rewrittenHeader[2] = 17; rewrittenHeader[3] = 0;
            var progress = CollectionFixtures.Save([(CollectionFixtures.Slot(37), (byte)2)], rewrittenHeader, deckFlags: [CollectionFixtures.Slot(9)]);
            await File.WriteAllBytesAsync(save, progress);
            var state = await profiles.ReadStateAsync();
            Require(state.Enabled && !state.RecoveryRequired, "Ordinary ignored padding required recovery.");
            await using (await profiles.AcquireSessionLeaseAsync(games))
                Require((await profiles.ReadStateAsync()).Enabled, "Padding rewrite prevented a managed session.");
            await profiles.SetEnabledAsync(false, games);
            Require((await File.ReadAllBytesAsync(save)).SequenceEqual(original), "Padding acceptance changed the preserved original.");
            Require(Directory.EnumerateFiles(Path.Combine(directory, "vault"), "before-launch-*.system.dat", SearchOption.AllDirectories)
                .Any(path => File.ReadAllBytes(path).SequenceEqual(progress)), "Ordinary temporary progress was discarded.");
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
