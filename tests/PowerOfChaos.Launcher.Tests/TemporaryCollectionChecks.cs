using System.Text.Json.Nodes;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.TestFixtures;

namespace PowerOfChaos.Launcher.Tests;

internal static class TemporaryCollectionChecks
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> run)
    {
        await run("temporary cards grant each supported pool and restore every original save byte", async () =>
        {
            foreach (var count in new[] { 1, 2, 3 })
            {
                var f = new Fixture(root, "roundtrip-" + count);
                var games = f.Games.Take(count).ToArray();
                var enabled = await f.Service.SetEnabledAsync(true, games);
                Require(enabled.Enabled && !enabled.RecoveryRequired && enabled.SavePath == f.SavePath, "Activation state differs.");
                var quantities = CollectionSave.ReadQuantities(await File.ReadAllBytesAsync(f.SavePath));
                var total = new[] { 155, 466, 771 }[count - 1];
                Require(Enumerable.Range(0, total).All(i => quantities[CollectionFixtures.Slot(i)] == 3), "An eligible card was not granted.");
                Require(quantities[0] == 7, "A slot outside every pool was changed.");
                Require((await File.ReadAllBytesAsync(f.OriginalPath)).SequenceEqual(f.Original), "Original backup differs.");
                var temporary = CollectionFixtures.Save([(CollectionFixtures.Slot(22), (byte)1)], deckFlags: [CollectionFixtures.Slot(4)]);
                await File.WriteAllBytesAsync(f.SavePath, temporary);
                var disabled = await f.Service.SetEnabledAsync(false, games);
                Require(!disabled.Enabled && !disabled.RecoveryRequired, "Deactivation state differs.");
                Require((await File.ReadAllBytesAsync(f.SavePath)).SequenceEqual(f.Original), "Deactivation did not restore exact original.");
                Require(f.Archives("temporary").Any(path => File.ReadAllBytes(path).SequenceEqual(temporary)), "Latest temporary changes were discarded.");
                Require(File.ReadAllText(f.UnrelatedPath) == "Synthetic unrelated replay remains unchanged.", "Unrelated CommonDir files changed.");
            }
        });
        await run("temporary card state persists across instances and read-only status does not wait for a game lease", async () =>
        {
            var f = new Fixture(root, "persist");
            await f.Service.SetEnabledAsync(true, f.Games);
            var restarted = f.NewService();
            await using (await restarted.AcquireSessionLeaseAsync(f.Games))
            {
                var statusTask = f.Service.ReadStateAsync();
                Require(statusTask.IsCompleted && (await statusTask).Enabled, "Status blocked behind a session lease.");
                await Throws(() => f.Service.SetEnabledAsync(false, f.Games));
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
            }
            Require((await f.NewService().ReadStateAsync()).Enabled, "Disposing a game lease restored the profile.");
            await restarted.SetEnabledAsync(false, f.Games);
            Require(!(await f.NewService().ReadStateAsync()).Enabled, "Inactive state did not persist.");
        });
        await run("temporary profiles block running games and writers without changing system.dat", async () =>
        {
            var f = new Fixture(root, "busy");
            f.Running = true;
            await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
            await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
            f.Running = false;
            using (new FileStream(f.SavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original) && !File.Exists(f.JournalPath), "A busy profile was modified.");
            await f.Service.SetEnabledAsync(true, f.Games);
            var unlocked = File.ReadAllBytes(f.SavePath);
            f.Running = true;
            await Throws(() => f.Service.SetEnabledAsync(false, f.Games));
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(unlocked), "A running temporary profile was restored.");
        });
        await run("ordinary sessions use the same cross-instance gate without creating a temporary profile", async () =>
        {
            var f = new Fixture(root, "ordinary-session");
            await using (await f.Service.AcquireSessionLeaseAsync(f.Games))
            {
                Require(!(await f.NewService().ReadStateAsync()).Enabled, "Ordinary session enabled a profile.");
                await Throws(() => f.NewService().SetEnabledAsync(true, f.Games));
                Require(!File.Exists(f.JournalPath) && File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Ordinary session wrote game data.");
            }
            await f.NewService().SetEnabledAsync(true, f.Games);
            await f.NewService().SetEnabledAsync(false, f.Games);
        });
        await run("a game detected immediately before replacement leaves the original and a recovery journal", async () =>
        {
            var f = new Fixture(root, "late-process");
            var late = new TemporaryCollectionService(f.Vault, f.Reader, () => f.Running,
                p => { if (p == "enable-journal") f.Running = true; });
            await Throws(() => late.SetEnabledAsync(true, f.Games));
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original) && (await f.Service.ReadStateAsync()).RecoveryRequired,
                "Late process detection modified the target or dropped recovery state.");
            await Throws(() => f.Service.SetEnabledAsync(false, f.Games));
            f.Running = false;
            await f.Service.SetEnabledAsync(false, f.Games);
        });
        await run("temporary profile interruptions retain a recoverable original at every durable boundary", async () =>
        {
            foreach (var point in new[] { "original-preserved", "enabling-recovery", "enable-journal", "enable-replaced", "active-recovery", "active-journal" })
            {
                var f = new Fixture(root, "interrupt-" + point);
                await Throws(() => f.NewService(point).SetEnabledAsync(true, f.Games));
                var state = await f.NewService().ReadStateAsync();
                if (point is "original-preserved" or "enabling-recovery" or "enable-journal" or "active-recovery")
                    Require(state.RecoveryRequired, "Unfinished activation was silently accepted.");
                if (point is "original-preserved" or "enabling-recovery")
                    Require(state.Enabled && File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "An unjournaled swap occurred.");
                Require(File.ReadAllBytes(f.OriginalPath).SequenceEqual(f.Original), "Interrupted activation lost the original.");
                await f.NewService().SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Interrupted activation could not restore.");
            }
            foreach (var point in new[] { "temporary-preserved", "restoring-recovery", "restore-journal", "restore-replaced", "inactive-recovery", "inactive-journal" })
            {
                var f = new Fixture(root, "interrupt-" + point);
                await f.Service.SetEnabledAsync(true, f.Games);
                var current = File.ReadAllBytes(f.SavePath);
                await Throws(() => f.NewService(point).SetEnabledAsync(false, f.Games));
                Require(f.Archives("temporary").Any(p => File.ReadAllBytes(p).SequenceEqual(current)), "Interrupted restoration lost temporary changes.");
                if (point != "inactive-journal") await f.NewService().SetEnabledAsync(false, f.Games);
                Require(!((await f.NewService().ReadStateAsync()).Enabled) && File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Interrupted restoration did not recover exactly.");
            }
        });
        await run("temporary cards refresh before launch while preserving temporary progress and original baseline", async () =>
        {
            var f = new Fixture(root, "refresh");
            await f.Service.SetEnabledAsync(true, f.Games);
            var changed = CollectionFixtures.Save([(CollectionFixtures.Slot(5), (byte)1)], deckFlags: [CollectionFixtures.Slot(98)]);
            await File.WriteAllBytesAsync(f.SavePath, changed);
            await using (await f.Service.AcquireSessionLeaseAsync(f.Games))
            {
                var quantities = CollectionSave.ReadQuantities(File.ReadAllBytes(f.SavePath));
                Require(Enumerable.Range(0, 771).All(i => quantities[CollectionFixtures.Slot(i)] == 3), "Launch did not refresh temporary availability.");
                Require(f.Archives("before-launch").Any(p => File.ReadAllBytes(p).SequenceEqual(changed)), "Refresh discarded temporary progress.");
            }
            Require(File.ReadAllBytes(f.OriginalPath).SequenceEqual(f.Original), "Refresh overwrote the baseline.");
            await f.Service.SetEnabledAsync(false, f.Games);
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Refresh changed the restored baseline.");
        });
        await run("interrupted prelaunch refresh requires recovery until its recorded replacement is complete", async () =>
        {
            foreach (var point in new[] { "refresh-journal", "refresh-replaced" })
            {
                var f = new Fixture(root, point);
                await f.Service.SetEnabledAsync(true, f.Games);
                var changed = CollectionFixtures.Save([(CollectionFixtures.Slot(3), (byte)1)]);
                File.WriteAllBytes(f.SavePath, changed);
                await Throws(() => f.NewService(point).AcquireSessionLeaseAsync(f.Games));
                var state = await f.NewService().ReadStateAsync();
                Require(state.Enabled && state.RecoveryRequired == (point == "refresh-journal"), "Refresh transition was misclassified.");
                if (state.RecoveryRequired) await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                await f.Service.SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Interrupted refresh lost the baseline.");
            }
        });
        await run("temporary profile is bound to the actual automatic save and the installed card pool", async () =>
        {
            var f = new Fixture(root, "bound");
            var other = Path.Combine(f.Directory, "other", "system.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllBytes(other, CollectionFixtures.Save([]));
            await f.Service.SetEnabledAsync(true, f.Games);
            f.ActivePath = other;
            await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
            await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
            f.ActivePath = f.SavePath;
            await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games.Take(2).ToArray()));
            f.ActivePath = other;
            await f.Service.SetEnabledAsync(false, f.Games);
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Restoration followed a changed CommonDir.");
            Require(CollectionSave.ReadQuantities(File.ReadAllBytes(other)).All(q => q == 0), "Changed CommonDir was modified.");
        });
        await run("missing or tampered original backups fail closed for restore and every launch", async () =>
        {
            foreach (var missing in new[] { false, true })
            {
                var f = new Fixture(root, "backup-" + missing);
                await f.Service.SetEnabledAsync(true, f.Games);
                var current = File.ReadAllBytes(f.SavePath);
                if (missing) File.Delete(f.OriginalPath); else File.WriteAllBytes(f.OriginalPath, CollectionFixtures.Save([]));
                Require((await f.Service.ReadStateAsync()).RecoveryRequired, "Damaged original backup was accepted.");
                await Throws(() => f.Service.SetEnabledAsync(false, f.Games));
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(current), "A damaged baseline caused a target write.");
            }
        });
        await run("damaged, missing and different-identity temporary saves can only restore the immutable original", async () =>
        {
            foreach (var mode in new[] { "truncated", "checksum", "missing", "identity" })
            {
                var f = new Fixture(root, mode);
                await f.Service.SetEnabledAsync(true, f.Games);
                byte[]? damaged = mode switch
                {
                    "truncated" => [7, 8, 9], "checksum" => new byte[5062],
                    "identity" => CollectionFixtures.Save([], Enumerable.Repeat((byte)239, 8).ToArray()), _ => null
                };
                if (damaged is null) File.Delete(f.SavePath); else File.WriteAllBytes(f.SavePath, damaged);
                Require((await f.Service.ReadStateAsync()).RecoveryRequired, "A damaged temporary profile was accepted.");
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                await f.Service.SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Damaged temporary profile prevented exact restore.");
                if (damaged is not null) Require(f.Archives("temporary").Any(p => File.ReadAllBytes(p).SequenceEqual(damaged)), "Damaged temporary bytes were discarded.");
            }
        });
        await run("malformed and escaping primary journals require explicit recovery using the preserved descriptor", async () =>
        {
            foreach (var field in new[] { "Version", "Generation", "Phase", "SavePath", "OriginalHash", "Slots", "oversized", "syntax" })
            {
                var f = new Fixture(root, "journal-" + field);
                await f.Service.SetEnabledAsync(true, f.Games);
                var current = File.ReadAllBytes(f.SavePath);
                var node = JsonNode.Parse(File.ReadAllText(f.JournalPath))!.AsObject();
                switch (field)
                {
                    case "Version": node[field] = 2; break;
                    case "Generation": node[field] = "../escape"; break;
                    case "Phase": node[field] = "unrecognized"; break;
                    case "SavePath": node[field] = "../system.dat"; break;
                    case "OriginalHash": node[field] = "not a hash"; break;
                    case "Slots": node[field] = new JsonArray(1, 1); break;
                }
                File.WriteAllText(f.JournalPath, field == "oversized" ? new string(' ', 16385) : field == "syntax" ? "{" : node.ToJsonString());
                var damagedMetadata = File.ReadAllBytes(f.JournalPath);
                Require((await f.Service.ReadStateAsync()).RecoveryRequired, "Malformed journal was ignored.");
                await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(current), "Malformed journal caused a target write.");
                await f.Service.SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "A valid recovery descriptor could not restore the original.");
                Require(Directory.EnumerateFiles(f.Vault, "metadata-state-*.json").Any(path => File.ReadAllBytes(path).SequenceEqual(damagedMetadata)),
                    "The damaged primary metadata was overwritten without preserving evidence.");
            }
        });
        await run("cancelled operations cannot create an unrecorded swap or lose either profile", async () =>
        {
            var f = new Fixture(root, "cancel");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws(() => f.Service.SetEnabledAsync(true, f.Games, cancelled.Token));
            await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games, cancelled.Token));
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original) && !File.Exists(f.JournalPath), "Precancelled activation wrote a profile.");
            using var during = new CancellationTokenSource();
            var afterJournal = new TemporaryCollectionService(f.Vault, f.Reader, () => false, p => { if (p == "enable-journal") during.Cancel(); });
            await afterJournal.SetEnabledAsync(true, f.Games, during.Token);
            Require((await f.Service.ReadStateAsync()).Enabled && File.ReadAllBytes(f.OriginalPath).SequenceEqual(f.Original), "Cancellation abandoned an unrecorded target swap.");
            await Throws(() => f.Service.SetEnabledAsync(false, f.Games, cancelled.Token));
            Require((await f.Service.ReadStateAsync()).Enabled, "Precancelled restoration altered active state.");
            await f.Service.SetEnabledAsync(false, f.Games);
        });
        await run("losing either active metadata copy never disables the mod or captures unlocked cards as a new baseline", async () =>
        {
            foreach (var name in new[] { "state.json", "recovery.json" })
            {
                var f = new Fixture(root, "missing-copy-" + name);
                await f.Service.SetEnabledAsync(true, f.Games);
                var originalPath = f.OriginalPath;
                var current = File.ReadAllBytes(f.SavePath);
                File.Delete(Path.Combine(f.Vault, name));
                var state = await f.NewService().ReadStateAsync();
                Require(state.Enabled && state.RecoveryRequired, "A missing active metadata copy silently disabled the mod.");
                await Throws(() => f.NewService().AcquireSessionLeaseAsync(f.Games));
                await Throws(() => f.NewService().SetEnabledAsync(true, f.Games));
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(current) && File.ReadAllBytes(originalPath).SequenceEqual(f.Original),
                    "Missing metadata caused an unlocked baseline or overwrote the original.");
                Require(Directory.EnumerateDirectories(f.Vault).Count() == 1, "Missing metadata created a new activation generation.");
                await f.NewService().SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original) && !(await f.Service.ReadStateAsync()).Enabled,
                    "The surviving recovery copy could not restore the original.");
                Require(f.Archives("temporary").Any(path => File.ReadAllBytes(path).SequenceEqual(current)), "Missing-copy recovery discarded temporary progress.");
            }
        });
        await run("missing or corrupt inactive metadata is repaired without rewinding subsequent normal progress", async () =>
        {
            foreach (var mode in new[] { "missing-primary", "corrupt-primary", "missing-recovery", "corrupt-recovery" })
            {
                var f = new Fixture(root, "inactive-" + mode);
                await f.Service.SetEnabledAsync(true, f.Games);
                await f.Service.SetEnabledAsync(false, f.Games);
                var normal = CollectionFixtures.Save([(CollectionFixtures.Slot(33), (byte)2)]);
                File.WriteAllBytes(f.SavePath, normal);
                switch (mode)
                {
                    case "missing-primary": File.Delete(f.JournalPath); break;
                    case "corrupt-primary": File.WriteAllText(f.JournalPath, "{ broken primary"); break;
                    case "missing-recovery": File.Delete(f.RecoveryPath); break;
                    case "corrupt-recovery": File.WriteAllText(f.RecoveryPath, "{ broken recovery"); break;
                }
                Require((await f.Service.ReadStateAsync()).RecoveryRequired, "Incomplete inactive metadata silently authorized a new baseline.");
                await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                await f.Service.SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(normal), "Metadata repair rewound normal progress made after the mod was disabled.");
                Require(!(await f.Service.ReadStateAsync()).Enabled && File.ReadAllBytes(f.JournalPath).SequenceEqual(File.ReadAllBytes(f.RecoveryPath)),
                    "Inactive recovery did not repair both metadata copies.");
                await f.Service.SetEnabledAsync(true, f.Games);
                Require(File.ReadAllBytes(f.OriginalPath).SequenceEqual(normal), "A later activation captured an older baseline.");
                await f.Service.SetEnabledAsync(false, f.Games);
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(normal), "The repaired profile did not preserve later normal progress.");
            }
        });
        await run("losing both metadata copies with retained generations fails closed and preserves all bytes", async () =>
        {
            foreach (var mode in new[] { "missing", "corrupt", "one-missing-one-corrupt", "inactive" })
            {
                var f = new Fixture(root, "both-copies-" + mode);
                await f.Service.SetEnabledAsync(true, f.Games);
                var originalPath = f.OriginalPath;
                if (mode == "inactive") await f.Service.SetEnabledAsync(false, f.Games);
                var current = File.ReadAllBytes(f.SavePath);
                if (mode is "missing" or "inactive") { File.Delete(f.JournalPath); File.Delete(f.RecoveryPath); }
                else
                {
                    File.WriteAllText(f.JournalPath, "{ damaged primary");
                    if (mode == "corrupt") File.WriteAllText(f.RecoveryPath, "{ damaged recovery"); else File.Delete(f.RecoveryPath);
                }
                Require((await f.NewService().ReadStateAsync()).RecoveryRequired, "Lost metadata with a retained original was treated as a fresh profile.");
                await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
                await Throws(() => f.Service.SetEnabledAsync(false, f.Games));
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(current) && File.ReadAllBytes(originalPath).SequenceEqual(f.Original),
                    "Lost metadata modified a save or the retained baseline.");
                Require(Directory.EnumerateDirectories(f.Vault).Count() == 1, "Lost metadata silently created another generation.");
            }
        });
        await run("conflicting valid generations cannot select a baseline using timestamps or copy order", async () =>
        {
            var f = new Fixture(root, "write-ahead-generation");
            await f.Service.SetEnabledAsync(true, f.Games);
            var firstPath = f.OriginalPath;
            await f.Service.SetEnabledAsync(false, f.Games);
            var nextOriginal = CollectionFixtures.Save([(CollectionFixtures.Slot(81), (byte)2)]);
            File.WriteAllBytes(f.SavePath, nextOriginal);
            await Throws(() => f.NewService("enabling-recovery").SetEnabledAsync(true, f.Games));
            var primary = JsonNode.Parse(File.ReadAllText(f.JournalPath))!;
            var recovery = JsonNode.Parse(File.ReadAllText(f.RecoveryPath))!;
            Require(primary["Generation"]!.GetValue<string>() != recovery["Generation"]!.GetValue<string>(), "The inter-copy interruption was not established.");
            Require((await f.Service.ReadStateAsync()).RecoveryRequired, "A generation mismatch was accepted.");
            await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
            await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
            await Throws(() => f.Service.SetEnabledAsync(false, f.Games));
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(nextOriginal) && File.ReadAllBytes(firstPath).SequenceEqual(f.Original),
                "Ambiguous recovery selected a generation or overwrote its original.");
        });
        await run("a conflicting inactive descriptor never labels an unlocked active save as safely disabled", async () =>
        {
            foreach (var mode in new[] { "unlocked", "already-restored" })
            {
                var f = new Fixture(root, "phase-conflict-" + mode);
                await f.Service.SetEnabledAsync(true, f.Games);
                var active = File.ReadAllBytes(f.JournalPath);
                var unlocked = File.ReadAllBytes(f.SavePath);
                await f.Service.SetEnabledAsync(false, f.Games);
                File.WriteAllBytes(f.JournalPath, active); // Valid active primary conflicts with the valid inactive write-ahead copy.
                if (mode == "unlocked") File.WriteAllBytes(f.SavePath, unlocked);
                Require((await f.Service.ReadStateAsync()).RecoveryRequired, "A valid phase conflict was silently accepted.");
                await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
                await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
                await f.Service.SetEnabledAsync(false, f.Games);
                Require(!(await f.Service.ReadStateAsync()).Enabled && File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original),
                    "Inactive metadata repaired itself while leaving unlocked cards as the permanent baseline.");
                if (mode == "unlocked") Require(f.Archives("temporary").Any(path => File.ReadAllBytes(path).SequenceEqual(unlocked)),
                    "Conflicting-phase recovery did not preserve the unlocked profile.");
            }
        });
        await run("missing metadata scans are bounded and unexpected retained entries cannot authorize a fresh baseline", async () =>
        {
            var f = new Fixture(root, "bounded-evidence");
            Directory.CreateDirectory(f.Vault);
            for (var index = 0; index < 1025; index++) File.WriteAllText(Path.Combine(f.Vault, $"authored-evidence-{index:D4}.txt"), "Synthetic retained evidence");
            Require((await f.Service.ReadStateAsync()).RecoveryRequired, "A bounded scan limit was treated as no evidence.");
            await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
            await Throws(() => f.Service.AcquireSessionLeaseAsync(f.Games));
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "A bounded evidence scan changed the save.");
        });
        await run("atomic replacement retries are bounded and ambiguous completion requires exact intended bytes", async () =>
        {
            if (!OperatingSystem.IsWindows()) return; // The retry policy targets observed Windows native error codes.
            foreach (var code in new[] { 32, 33, 1175 })
            {
                var f = new Fixture(root, "replace-retry-" + code);
                var attempts = 0;
                var transient = new TemporaryCollectionService(f.Vault, f.Reader, () => false, null, (source, destination) =>
                {
                    if (destination == f.SavePath && ++attempts <= 2) throw NativeError(code);
                    File.Replace(source, destination, null);
                });
                await transient.SetEnabledAsync(true, f.Games);
                Require(attempts == 3 && (await f.Service.ReadStateAsync()).Enabled, "A proven transient replacement was not retried as bounded atomic operations.");
                await f.Service.SetEnabledAsync(false, f.Games);
            }
            var completed = new Fixture(root, "replace-completed");
            var applied = 0;
            var ambiguous = new TemporaryCollectionService(completed.Vault, completed.Reader, () => false, null, (source, destination) =>
            {
                File.Replace(source, destination, null);
                if (destination == completed.SavePath) { applied++; throw NativeError(1175); }
            });
            await ambiguous.SetEnabledAsync(true, completed.Games);
            Require(applied == 1 && (await completed.Service.ReadStateAsync()).Enabled, "An exact completed replacement was repeated or rejected.");
            await completed.Service.SetEnabledAsync(false, completed.Games);
            foreach (var code in new[] { 1175, 5 })
            {
                var f = new Fixture(root, "replace-failure-" + code);
                var attempts = 0;
                var persistent = new TemporaryCollectionService(f.Vault, f.Reader, () => false, null, (source, destination) =>
                {
                    if (destination == f.SavePath) { attempts++; throw NativeError(code); }
                    File.Replace(source, destination, null);
                });
                await Throws(() => persistent.SetEnabledAsync(true, f.Games));
                Require(attempts == (code == 1175 ? 6 : 1), "Replacement retries were unbounded or included an unproven error code.");
                Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original) && File.ReadAllBytes(f.OriginalPath).SequenceEqual(f.Original)
                    && (await f.Service.ReadStateAsync()).RecoveryRequired, "A persistent replacement error changed or lost the original.");
                await f.Service.SetEnabledAsync(false, f.Games);
            }
        });
        await run("concurrent status snapshots and sequential save swaps preserve both metadata copies and original bytes", async () =>
        {
            var f = new Fixture(root, "status-stress");
            using var stop = new CancellationTokenSource();
            var reads = 0;
            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = await f.NewService().ReadStateAsync();
                    Interlocked.Increment(ref reads);
                    await Task.Yield();
                }
            });
            try
            {
                for (var index = 0; index < 40; index++)
                {
                    await f.Service.SetEnabledAsync(true, f.Games);
                    await f.Service.SetEnabledAsync(false, f.Games);
                    Require(File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Concurrent status reads changed a restored save.");
                }
            }
            finally { stop.Cancel(); await reader; }
            Require(reads > 0 && !(await f.Service.ReadStateAsync()).Enabled
                && File.ReadAllBytes(f.JournalPath).SequenceEqual(File.ReadAllBytes(f.RecoveryPath)), "Concurrent snapshot stress did not finish consistently.");
        });
        await run("unsupported card archives and ambiguous automatic saves cannot enable temporary cards", async () =>
        {
            var f = new Fixture(root, "invalid-pool");
            File.WriteAllText(f.Games[1].DataPath, "Authored invalid archive");
            await Throws(() => f.Service.SetEnabledAsync(true, f.Games));
            Require(!File.Exists(f.JournalPath) && File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Partial pools enabled temporary cards.");
            var second = Path.Combine(f.Directory, "second", "system.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(second)!); File.WriteAllBytes(second, f.Original);
            var ambiguous = new TemporaryCollectionService(f.Vault, new CardCollectionReader(_ => [f.SavePath, second]), () => false);
            await Throws(() => ambiguous.SetEnabledAsync(true, f.Games.Take(1).ToArray()));
            Require(!File.Exists(f.JournalPath) && File.ReadAllBytes(f.SavePath).SequenceEqual(f.Original), "Ambiguous saves enabled temporary cards.");
        });
        await run("repeated temporary profile generations never replace earlier originals or archived progress", async () =>
        {
            var f = new Fixture(root, "generations");
            await f.Service.SetEnabledAsync(true, f.Games);
            var firstPath = f.OriginalPath;
            await f.Service.SetEnabledAsync(false, f.Games);
            var secondOriginal = CollectionFixtures.Save([(CollectionFixtures.Slot(76), (byte)2)]);
            File.WriteAllBytes(f.SavePath, secondOriginal);
            await f.Service.SetEnabledAsync(true, f.Games);
            Require(f.OriginalPath != firstPath && File.ReadAllBytes(firstPath).SequenceEqual(f.Original)
                && File.ReadAllBytes(f.OriginalPath).SequenceEqual(secondOriginal), "A later activation overwrote an earlier baseline.");
            await f.Service.SetEnabledAsync(false, f.Games);
            Require(File.ReadAllBytes(f.SavePath).SequenceEqual(secondOriginal), "Later activation restored the wrong generation.");
        });
    }

    private static async Task Throws(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException or System.Text.Json.JsonException) { return; }
        throw new InvalidOperationException("An unsafe or interrupted operation succeeded.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IOException NativeError(int code) => new("Authored Windows replacement fault", unchecked((int)0x80070000) | code);

    private sealed class Fixture
    {
        public Fixture(string root, string name)
        {
            Directory = Path.Combine(root, "temporary-profiles", name);
            System.IO.Directory.CreateDirectory(Directory);
            Games = GameCatalog.All.Select(g =>
            {
                var folder = Path.Combine(Directory, g.Id);
                System.IO.Directory.CreateDirectory(folder);
                var archive = Path.Combine(folder, "data.dat");
                CollectionFixtures.WriteArchive(archive);
                return new GameInstallation(g.Id, Path.Combine(folder, g.ExecutableName), archive, Path.Combine(folder, "voice.dat"), Directory);
            }).ToArray();
            var common = Path.Combine(Directory, "common");
            System.IO.Directory.CreateDirectory(common);
            SavePath = Path.Combine(common, "system.dat");
            ActivePath = SavePath;
            Original = CollectionFixtures.Save([(0, (byte)7), (CollectionFixtures.Slot(1), (byte)1), (CollectionFixtures.Slot(17), (byte)2)],
                deckFlags: [CollectionFixtures.Slot(91)]);
            File.WriteAllBytes(SavePath, Original);
            UnrelatedPath = Path.Combine(common, "replay.ydr");
            File.WriteAllText(UnrelatedPath, "Synthetic unrelated replay remains unchanged.");
            Vault = Path.Combine(Directory, "vault");
            Reader = new CardCollectionReader(_ => [ActivePath]);
            Service = NewService();
        }
        public string Directory { get; }
        public string SavePath { get; }
        public string ActivePath { get; set; }
        public string UnrelatedPath { get; }
        public string Vault { get; }
        public byte[] Original { get; }
        public GameInstallation[] Games { get; }
        public CardCollectionReader Reader { get; }
        public TemporaryCollectionService Service { get; }
        public bool Running { get; set; }
        public string JournalPath => Path.Combine(Vault, "state.json");
        public string RecoveryPath => Path.Combine(Vault, "recovery.json");
        public string OriginalPath => Path.Combine(Vault, JsonNode.Parse(File.ReadAllText(File.Exists(JournalPath) ? JournalPath : RecoveryPath))!["Generation"]!.GetValue<string>(), "original.system.dat");
        public IEnumerable<string> Archives(string kind) => System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(OriginalPath)!, kind + "-*.system.dat");
        public TemporaryCollectionService NewService(string? failAt = null) => new(Vault, Reader, () => Running,
            p => { if (p == failAt) throw new InvalidOperationException("Authored interruption at " + p); });
    }
}
