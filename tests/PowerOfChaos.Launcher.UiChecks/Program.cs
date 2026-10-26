using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PowerOfChaos.Launcher;
using PowerOfChaos.Launcher.Views;
using PowerOfChaos.Launcher.ViewModels;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class Program
{
    private static int passed;
    private static string artifacts = "";

    [STAThread]
    public static int Main(string[] args)
    {
        var project = FindProject();
        artifacts = Path.GetFullPath(Path.Combine(project, "..", "..", ".local", "ui-checks"));
        Directory.CreateDirectory(artifacts);
        var runRoot = Path.Combine(artifacts, "run-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(runRoot, "launcher-data");
        Directory.CreateDirectory(data);
        var originalData = Environment.GetEnvironmentVariable("POC_LAUNCHER_DATA_DIR");
        Environment.SetEnvironmentVariable("POC_LAUNCHER_DATA_DIR", data);
        MainWindow? window = null;
        try
        {
            AppBuilder.Configure<App>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            window = OpenWindow();
            Check("initial empty state is visible and says no game path added", () =>
            {
                var empty = Find<Border>(window, "LibraryEmpty");
                Require(empty.IsVisible, "Initial empty state is hidden.");
                Require(empty.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "no game path added"), "Empty text differs from requested message.");
                Require(CardCount(window) == 0, "Fresh launcher contains games before a path is added.");
                Require(window.Icon is not null, "The generated launcher window icon was not embedded or loaded.");
            });
            Capture(window, "empty.png");

            var gear = window.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Settings");
            var flyout = gear.Flyout as MenuFlyout ?? throw new InvalidOperationException("Cogwheel has no menu flyout.");
            Check("cogwheel offers Add Game path and opens its settings flyout", () =>
            {
                Require(flyout.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "Add Game path"), "Add Game path entry is missing.");
                ClickInput(window, gear);
                WaitUntil(() => flyout.IsOpen, "Settings flyout did not open from headless pointer input.");
            });
            Capture(window, "settings.png");

            var unrelated = Path.Combine(runRoot, "unrelated folder");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "notes.txt"), "Launcher-authored UI test data only.");
            var dialog = OpenAddPath(window, flyout);
            Check("unrelated folder reports game not found", () =>
            {
                SubmitPath(dialog, unrelated);
                WaitUntil(() => Find<TextBlock>(dialog, "ResultText").Text?.StartsWith("game not found", StringComparison.Ordinal) == true,
                    "Unrelated folder did not show game not found.");
                Require(CardCount(window) == 0, "Invalid path added a game.");
            });
            Capture(dialog, "game-not-found.png");
            Find<Button>(dialog, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => !window.OwnedWindows.Contains(dialog), "Invalid path dialog did not close.");

            var parent = Path.Combine(runRoot, "synthetic installations");
            WriteGame(Nested(parent, "Yugi", 2), "yugi_pc.exe");
            WriteGame(Nested(parent, "Kaiba", 3), "kaiba_pc.exe");
            WriteGame(Nested(parent, "Joey", 6), "joey_pc.exe");
            dialog = OpenAddPath(window, flyout);
            Check("parent folder discovers three games including the sixth nested level", () =>
            {
                SubmitPath(dialog, parent + Path.DirectorySeparatorChar);
                WaitUntil(() => CardCount(window) == 3, "Nested synthetic games were not added to the library.");
                Require(!Find<Border>(window, "LibraryEmpty").IsVisible, "Empty state remained visible after games were added.");
                Require(Find<StackPanel>(window, "InstalledLibrary").IsVisible, "Installed library is hidden.");
                var status = Find<TextBlock>(window, "StatusText").Text ?? "";
                Require(status.Contains("3 games found", StringComparison.Ordinal), "Success message does not identify the three games.");
                WaitUntil(() => File.Exists(Path.Combine(data, "launcher-settings.json")), "Settings were not persisted.");
            });
            Check("detected game rows load their local portrait images", () =>
            {
                WaitUntil(() => PortraitCount(window) == 3, "Portrait images did not appear for all three detected games.");
            });
            Capture(window, "three-games.png");

            dialog = OpenAddPath(window, flyout);
            Check("invalid addition preserves all three existing games", () =>
            {
                SubmitPath(dialog, unrelated);
                WaitUntil(() => Find<TextBlock>(dialog, "ResultText").Text?.StartsWith("game not found", StringComparison.Ordinal) == true,
                    "Invalid addition did not report failure.");
                Require(CardCount(window) == 3, "Invalid addition removed previously discovered games.");
            });
            Find<Button>(dialog, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => !window.OwnedWindows.Contains(dialog), "Invalid dialog did not close.");

            Check("preferences menu saves minimize and Wine settings", () =>
            {
                var preferencesItem = flyout.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Preferences");
                preferencesItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                WaitUntil(() => window.OwnedWindows.OfType<PreferencesDialog>().Any(), "Preferences did not open.");
                var preferences = window.OwnedWindows.OfType<PreferencesDialog>().Single();
                preferences.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = false;
                preferences.GetVisualDescendants().OfType<TextBox>().Single().Text = "/test/runner with spaces/wine";
                preferences.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Save preferences")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => (Find<TextBlock>(window, "StatusText").Text ?? "") == "Preferences saved.", "Preferences did not finish saving.");
                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
                Require(!settings.RootElement.GetProperty("MinimizeOnLaunch").GetBoolean(), "Minimize setting was not saved.");
                Require(settings.RootElement.GetProperty("WineExecutable").GetString() == "/test/runner with spaces/wine", "Wine setting was not saved.");
            });

            OpenMods(window);
            Check("Mods shows a left checkbox, title and right description with no subtitle", () =>
            {
                Require(Find<TextBlock>(window, "PageTitle").Text == "Mods", "Mods page title differs.");
                Require(!Find<TextBlock>(window, "PageSubtitle").IsVisible, "Mods subtitle still occupies the header.");
                var row = Find<Grid>(window, "SkipSplashScreensRow");
                var checkbox = Find<CheckBox>(window, "SkipSplashScreensCheckBox");
                var title = Find<TextBlock>(window, "SkipSplashScreensTitle");
                var description = Find<TextBlock>(window, "SkipSplashScreensDescription");
                Require(checkbox.IsEnabled && checkbox.IsChecked == false, "New mod should initially be available and off.");
                Require(checkbox.Bounds.Right <= title.Bounds.Left && title.Bounds.Right <= description.Bounds.Left,
                    "Mod checkbox, title and description are not ordered left to right.");
                Require(description.Bounds.Width > 0 && description.Bounds.Right <= row.Bounds.Width + 1,
                    "Mod description does not fit its row.");
                var interactionsRow = Find<Grid>(window, "SkipCharacterInteractionsRow");
                var interactions = Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox");
                var interactionsTitle = Find<TextBlock>(window, "SkipCharacterInteractionsTitle");
                var interactionsDescription = Find<TextBlock>(window, "SkipCharacterInteractionsDescription");
                Require(interactions.IsEnabled && interactions.IsChecked == false, "Character mod should initially be available and off.");
                Require(interactions.Bounds.Right <= interactionsTitle.Bounds.Left
                    && interactionsTitle.Bounds.Right <= interactionsDescription.Bounds.Left
                    && interactionsDescription.Bounds.Width > 0
                    && interactionsDescription.Bounds.Right <= interactionsRow.Bounds.Width + 1,
                    "Character mod does not have a readable checkbox/title/description row.");
                Require(Find<StackPanel>(window, "ModsView").GetVisualDescendants().OfType<CheckBox>()
                    .Count(c => !c.IsEnabled) == 3, "Future mods must remain disabled.");
            });
            Capture(window, "mods.png");
            Check("global mod checkbox saves without changing game locations or preferences", () =>
            {
                ClickInput(window, Find<CheckBox>(window, "SkipSplashScreensCheckBox"));
                WaitUntil(() => SavedModEnabled(data) && Find<CheckBox>(window, "SkipSplashScreensCheckBox").IsEnabled,
                    "Enabled mod did not finish saving.");
                Require(Find<CheckBox>(window, "SkipSplashScreensCheckBox").IsChecked == true, "Checkbox reverted after successful save.");
                Require((Find<TextBlock>(window, "StatusText").Text ?? "").Contains("all three games", StringComparison.Ordinal),
                    "Mod status does not explain the global selection.");
                CheckPreservedSettings(data);
            });
            Capture(window, "mods-enabled.png");

            Check("character interactions toggle composes with the enabled splash setting", () =>
            {
                ClickInput(window, Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox"));
                WaitUntil(() => SavedModEnabled(data, "SkipCharacterInteractions")
                    && Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox").IsEnabled,
                    "Character mod did not finish saving.");
                Require(SavedModEnabled(data), "Enabling character mod disabled the splash setting.");
                Require((Find<TextBlock>(window, "StatusText").Text ?? "").Contains("all three games", StringComparison.Ordinal),
                    "Character mod does not explain its global scope.");
                CheckPreservedSettings(data);
            });
            Capture(window, "mods-both-enabled.png");

            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
            window = OpenWindow();
            Check("reopening restores three games from launcher settings", () =>
            {
                WaitUntil(() => CardCount(window) == 3, "Reopened launcher did not restore three games.");
                Require(!Find<Border>(window, "LibraryEmpty").IsVisible, "Reopened library shows empty state.");
                WaitUntil(() => PortraitCount(window) == 3, "Reopened launcher did not reload portraits.");
            });
            Capture(window, "reopened.png");

            OpenMods(window);
            Check("reopening restores enabled mod and unticking persists disabled state", () =>
            {
                var checkbox = Find<CheckBox>(window, "SkipSplashScreensCheckBox");
                WaitUntil(() => checkbox.IsEnabled, "Mod checkbox did not finish loading.");
                Require(checkbox.IsChecked == true, "Enabled global mod was not restored.");
                Require(Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox").IsChecked == true,
                    "Enabled character mod was not restored with the splash setting.");
                ClickInput(window, checkbox);
                WaitUntil(() => !SavedModEnabled(data) && checkbox.IsEnabled, "Unticking did not persist disabled state.");
                Require(checkbox.IsChecked == false, "Unticked checkbox stayed enabled.");
                Require(SavedModEnabled(data, "SkipCharacterInteractions"), "Unticking splash disabled the character mod.");
                CheckPreservedSettings(data);
            });
            Check("unticking character interactions restores its saved state independently", () =>
            {
                var checkbox = Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox");
                ClickInput(window, checkbox);
                WaitUntil(() => !SavedModEnabled(data, "SkipCharacterInteractions") && checkbox.IsEnabled,
                    "Unticking character mod did not finish saving.");
                Require(!SavedModEnabled(data), "Unticking character mod enabled splash.");
                Require(checkbox.IsChecked == false, "Character checkbox stayed checked after disabling.");
                CheckPreservedSettings(data);
            });
            Check("failed mod save restores the previous checkbox selection", () =>
            {
                var path = Path.Combine(data, "launcher-settings.json");
                var backup = path + ".test-backup";
                File.Move(path, backup);
                Directory.CreateDirectory(path); // Synthetic test folder blocks atomic replacement.
                try
                {
                    var checkbox = Find<CheckBox>(window, "SkipSplashScreensCheckBox");
                    ClickInput(window, checkbox);
                    WaitUntil(() => checkbox.IsEnabled && (Find<TextBlock>(window, "StatusText").Text ?? "")
                        .Contains("previous selection was kept", StringComparison.Ordinal), "Failed save did not restore selection.");
                    Require(checkbox.IsChecked == false, "Failed save left an unsaved mod active.");
                    var characterCheckbox = Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox");
                    ClickInput(window, characterCheckbox);
                    WaitUntil(() => characterCheckbox.IsEnabled && (Find<TextBlock>(window, "StatusText").Text ?? "")
                        .Contains("previous selection was kept", StringComparison.Ordinal), "Failed character save did not restore selection.");
                    Require(characterCheckbox.IsChecked == false && checkbox.IsChecked == false,
                        "Failed character save changed either active mod setting.");
                    Require(CardCount(window) == 3, "Failed mod save changed the library.");
                }
                finally
                {
                    Directory.Delete(path);
                    File.Move(backup, path);
                }
            });

            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
            window = OpenWindow();
            OpenMods(window);
            Check("reopening keeps a disabled global mod disabled", () =>
            {
                var checkbox = Find<CheckBox>(window, "SkipSplashScreensCheckBox");
                WaitUntil(() => checkbox.IsEnabled, "Reopened mod checkbox did not finish loading.");
                Require(checkbox.IsChecked == false && !SavedModEnabled(data), "Disabled mod was reenabled on reopen.");
                Require(Find<CheckBox>(window, "SkipCharacterInteractionsCheckBox").IsChecked == false
                    && !SavedModEnabled(data, "SkipCharacterInteractions"), "Disabled character mod was reenabled on reopen.");
                Require(CardCount(window) == 3, "Second reopen lost existing installations.");
            });
            Find<Button>(window, "LibraryNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            if (args.Contains("--installed-artwork", StringComparer.Ordinal))
            {
                Check("installed portraits are loaded without starting a game", () =>
                {
                    Require(OperatingSystem.IsWindows(), "Installed artwork preview requires this Windows installation.");
                    var installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "KONAMI");
                    var currentGear = window.GetVisualDescendants().OfType<Button>()
                        .Single(b => AutomationProperties.GetName(b) == "Settings");
                    var currentFlyout = currentGear.Flyout as MenuFlyout ?? throw new InvalidOperationException("Settings menu is missing.");
                    dialog = OpenAddPath(window, currentFlyout);
                    SubmitPath(dialog, installedRoot);
                    WaitUntil(() => CardCount(window) == 3 && Find<ItemsControl>(window, "GameCards").Items
                        .OfType<GameCardViewModel>().All(c => c.Folder.StartsWith(installedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                        "Installed game locations were not discovered.");
                    WaitUntil(() => PortraitCount(window) == 3, "Installed character portraits did not load.");
                    var expected = new Dictionary<string, PixelSize>
                    {
                        ["yugi"] = new(591, 560), ["kaiba"] = new(739, 559), ["joey"] = new(672, 600)
                    };
                    foreach (var card in Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>())
                        Require(card.Portrait?.PixelSize == expected[card.Installation.GameId], "Installed portrait dimensions differ from the selected resource.");
                });
                Capture(window, "installed-portraits.png");
            }

            Console.WriteLine($"PASS {passed} real-component headless UI checks. No Play action or game process was invoked.");
            Console.WriteLine("Rendered frames: " + artifacts);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL headless UI check: " + exception);
            Console.Error.WriteLine("Local artifacts: " + artifacts);
            return 1;
        }
        finally
        {
            if (window is not null)
            {
                foreach (var child in window.OwnedWindows.ToArray()) child.Close();
                window.Close();
                Pump(TimeSpan.FromMilliseconds(80));
            }
            Environment.SetEnvironmentVariable("POC_LAUNCHER_DATA_DIR", originalData);
        }
    }

    private static MainWindow OpenWindow()
    {
        var window = new MainWindow();
        window.Show();
        Pump(TimeSpan.FromMilliseconds(150));
        return window;
    }

    private static void OpenMods(MainWindow window)
    {
        Find<Button>(window, "ModsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(80));
    }

    private static bool SavedModEnabled(string data, string property = "SkipSplashScreens")
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
        return settings.RootElement.TryGetProperty(property, out var value) && value.GetBoolean();
    }

    private static void CheckPreservedSettings(string data)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
        Require(settings.RootElement.GetProperty("Installations").GetArrayLength() == 3, "Mod save changed installed games.");
        Require(settings.RootElement.GetProperty("SearchRoots").GetArrayLength() == 1, "Mod save changed search roots.");
        Require(!settings.RootElement.GetProperty("MinimizeOnLaunch").GetBoolean(), "Mod save changed minimize preference.");
        Require(settings.RootElement.GetProperty("WineExecutable").GetString() == "/test/runner with spaces/wine", "Mod save changed Wine preference.");
    }

    private static AddGamePathDialog OpenAddPath(MainWindow window, MenuFlyout flyout)
    {
        var item = flyout.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Add Game path");
        flyout.Hide();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WaitUntil(() => window.OwnedWindows.OfType<AddGamePathDialog>().Any(), "Add Game path dialog did not open.");
        var dialog = window.OwnedWindows.OfType<AddGamePathDialog>().Single();
        Pump(TimeSpan.FromMilliseconds(70));
        return dialog;
    }

    private static void SubmitPath(AddGamePathDialog dialog, string path)
    {
        Find<TextBox>(dialog, "PathInput").Text = path;
        var button = Find<Button>(dialog, "ScanButton");
        WaitUntil(() => button.IsEnabled, "Find games button remained disabled after a path was entered.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void ClickInput(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Cannot locate control in headless window.");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump(TimeSpan.FromMilliseconds(80));
    }

    private static int CardCount(MainWindow window) => Find<ItemsControl>(window, "GameCards").Items.Count;
    private static int PortraitCount(MainWindow window) => window.GetVisualDescendants().OfType<Image>()
        .Count(i => i.Name == "GamePortrait" && i.IsVisible && i.Source is not null);
    private static T Find<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing named control: " + name);

    private static void Capture(Window window, string name)
    {
        if (name != "settings.png") window.MouseMove(new Point(225, 120), RawInputModifiers.None);
        Pump(TimeSpan.FromMilliseconds(120));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer did not return a frame.");
        frame.Save(Path.Combine(artifacts, name));
    }

    private static void Check(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WaitUntil(Func<bool> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(message);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(TimeSpan duration)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static string FindProject()
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "PowerOfChaos.Launcher.UiChecks.csproj"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Cannot find headless checks project.");
    }

    private static string Nested(string root, string prefix, int depth)
    {
        for (var i = 0; i < depth; i++) root = Path.Combine(root, prefix + "-" + i);
        return root;
    }

    private static void WriteGame(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        using (var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            // Synthetic inert headers only, no executable code or original game bytes.
            stream.SetLength(512);
            writer.Write((ushort)0x5A4D);
            stream.Position = 0x3C; writer.Write(0x80);
            stream.Position = 0x80; writer.Write(0x00004550); writer.Write((ushort)0x014C); writer.Write((ushort)1);
            stream.Position = 0x94; writer.Write((ushort)0x00E0); writer.Write((ushort)0x0002); writer.Write((ushort)0x010B);
        }
        foreach (var archive in new[] { "data.dat", "Voice.dat" })
        {
            if (archive == "data.dat")
            {
                PortraitFixtureWriter.Write(Path.Combine(directory, archive), name);
                continue;
            }
            using var stream = new FileStream(Path.Combine(directory, archive), FileMode.CreateNew, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("KCEJYUGI")); writer.Write(1u); stream.SetLength(12 + 268);
        }
    }
}
