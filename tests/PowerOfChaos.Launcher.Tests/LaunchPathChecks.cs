using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Launching;

namespace PowerOfChaos.Launcher.Tests;

internal static class LaunchPathChecks
{
    internal static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        foreach (var id in new[] { "yugi", "kaiba", "joey" })
        {
            await run($"{id} mod cache stays launchable for all binary selections and keeps full content verification", async () =>
            {
                var root = Path.Combine(fixtures, "lp-" + id);
                var image = SplashChecks.SyntheticImage();
                var game = await SplashChecks.Installation(root, id, image);
                var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
                // Identical inert outputs deliberately test selection isolation independently of output hashes.
                var profiles = new[] { new GameModOptions(true), new(false, true), new(true, true) }
                    .Select(options => new SplashProfile(id, image.Length, hash, hash, []) { Options = options }).ToArray();
                var data = Path.Combine(root, "data");
                var preparer = new GameLaunchPreparer(data, profiles);
                var outputs = new List<string>();
                foreach (var profile in profiles)
                {
                    var output = await preparer.PrepareExecutableAsync(game, profile.Options);
                    Require(output.Length < 260, "A mod combination still exceeds the Windows process-launch limit.");
                    Require(image.SequenceEqual(await File.ReadAllBytesAsync(output)), "Fixture content was changed.");
                    Require(await preparer.PrepareExecutableAsync(game, profile.Options) == output, "Cache selection is unstable.");
                    var plan = LaunchPlan.CreatePrepared(game, new LauncherSettings(), output, isWindows: true);
                    Require(plan.FileName == output && plan.WorkingDirectory == Path.GetDirectoryName(game.ExecutablePath), "Launch plan lost cache or asset root.");
                    outputs.Add(output);
                }
                Require(outputs.Distinct().Count() == profiles.Length, "Different selections shared a cache.");
                await File.WriteAllTextAsync(outputs[0], "Damaged fixture.");
                await preparer.PrepareExecutableAsync(game, profiles[0].Options);
                Require(image.SequenceEqual(await File.ReadAllBytesAsync(outputs[0])), "Content checks stopped repairing damaged caches.");
                Require(image.SequenceEqual(await File.ReadAllBytesAsync(game.ExecutablePath)), "Installed synthetic input was changed.");

                if (OperatingSystem.IsWindows())
                {
                    // Replace ONLY the inert private fixture with our own apphost to test CreateProcess.
                    // This is not a game, and does not claim to test gameplay or a real patch output.
                    CopyAuthoredProbe(outputs[0]);
                    var plan = LaunchPlan.CreatePrepared(game, new LauncherSettings(), outputs[0]);
                    await RunAuthoredProbe(plan);
                    Console.WriteLine($"Authored {id}-named child exited 0 from {outputs[0].Length}-character cache path.");
                }
            });
        }

        await run("Windows launch planning reports excessive paths before native process creation", async () =>
        {
            var game = await SplashChecks.Installation(Path.Combine(fixtures, "lp-limit"), "kaiba", SplashChecks.SyntheticImage());
            var longPath = Path.Combine(fixtures, new string('x', 180), new string('y', 80), "kaiba_pc.exe");
            try { _ = LaunchPlan.CreatePrepared(game, new LauncherSettings(), longPath, isWindows: true); }
            catch (InvalidDataException exception)
            {
                Require(exception.Message.Contains("shorter", StringComparison.Ordinal), "Long path has no recovery guidance.");
                return;
            }
            throw new InvalidOperationException("The oversized executable path reached native launch planning.");
        });

        if (OperatingSystem.IsWindows())
        {
            await run("an excessively long custom cache root is refused without creating cache output", async () =>
            {
                var image = SplashChecks.SyntheticImage();
                var game = await SplashChecks.Installation(Path.Combine(fixtures, "lp-custom"), "yugi", image);
                var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
                var data = Path.Combine(fixtures, new string('d', 180));
                var preparer = new GameLaunchPreparer(data, [new("yugi", image.Length, hash, hash, [])]);
                try { _ = await preparer.PrepareExecutableAsync(game, true); }
                catch (InvalidDataException)
                {
                    Require(!Directory.Exists(data), "Refused cache still created directories.");
                    return;
                }
                throw new InvalidOperationException("An excessively long cache root was accepted.");
            });

            await run("authored native probe records the legacy 264-character process-path behavior", async () =>
            {
                var root = Path.Combine(fixtures, "legacy");
                var fileName = "kaiba_pc.exe";
                var path = Path.Combine(root, new string('p', Math.Max(1, 264 - root.Length - fileName.Length - 2)), fileName);
                CopyAuthoredProbe(path);
                try
                {
                    await RunAuthoredProbe(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = fixtures });
                    Console.WriteLine($"This host permits an authored {path.Length}-character native process path.");
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 206)
                {
                    Console.WriteLine($"Reproduced legacy launch failure: existing {path.Length}-character authored executable; Win32 error {exception.NativeErrorCode}.");
                }
            });
        }
    }

    private static void CopyAuthoredProbe(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var assembly = Assembly.GetExecutingAssembly().Location;
        var apphost = Path.ChangeExtension(assembly, ".exe");
        Require(File.Exists(apphost), "Build the authored test apphost before running Windows process checks.");
        File.Copy(apphost, destination, overwrite: true);
        foreach (var name in new[] { Path.GetFileName(assembly), Path.GetFileNameWithoutExtension(assembly) + ".deps.json",
            Path.GetFileNameWithoutExtension(assembly) + ".runtimeconfig.json", "PowerOfChaos.Launcher.Core.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(directory, name), overwrite: true);
    }

    private static async Task RunAuthoredProbe(ProcessStartInfo plan)
    {
        plan.ArgumentList.Add("--launch-path-probe");
        plan.CreateNoWindow = true;
        plan.RedirectStandardOutput = true;
        plan.RedirectStandardError = true;
        using var child = Process.Start(plan) ?? throw new InvalidOperationException("Authored launch probe did not start.");
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Require(child.ExitCode == 0, "Authored child failed: " + await child.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
