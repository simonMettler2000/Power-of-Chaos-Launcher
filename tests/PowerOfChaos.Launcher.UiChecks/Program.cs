using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static JsonElement preservedSettings;
    private static readonly string[] ModNames = ["SkipSplashScreens", "SkipCharacterInteractions", "OriginalAspectRatio"];
    private static readonly string[] ModProperties = ["SkipSplashScreens", "SkipCharacterInteractions", "RestoreOriginalAspectRatio"];

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
                preferences.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WineCommand").Text = "/test/runner with spaces/wine";
                preferences.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Save preferences")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => (Find<TextBlock>(window, "StatusText").Text ?? "") == "Preferences saved.", "Preferences did not finish saving.");
                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
                Require(!settings.RootElement.GetProperty("MinimizeOnLaunch").GetBoolean(), "Minimize setting was not saved.");
                Require(settings.RootElement.GetProperty("WineExecutable").GetString() == "/test/runner with spaces/wine", "Wine setting was not saved.");
            });

            using (var beforeMods = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json"))))
                preservedSettings = beforeMods.RootElement.Clone();
            OpenMods(window);
            Check("Mods gives all three available choices readable rows above future mods", () =>
            {
                Require(Find<TextBlock>(window, "PageTitle").Text == "Mods", "Mods page title differs.");
                Require(!Find<TextBlock>(window, "PageSubtitle").IsVisible, "Mods subtitle still occupies the header.");
                foreach (var name in ModNames) CheckModRow(window, name);
                Require(Find<TextBlock>(window, "OriginalAspectRatioTitle").Text == "Original aspect ratio", "Aspect ratio title differs.");
                var description = Find<TextBlock>(window, "OriginalAspectRatioDescription").Text ?? "";
                Require(description.Contains("4:3", StringComparison.Ordinal)
                    && description.Contains("windowed", StringComparison.Ordinal)
                    && description.Contains("resizing", StringComparison.Ordinal)
                    && description.Contains("maximizing", StringComparison.Ordinal)
                    && description.Contains("fullscreen", StringComparison.Ordinal)
                    && description.Contains("normal game speed", StringComparison.Ordinal)
                    && description.Contains("next launch", StringComparison.Ordinal), "Aspect ratio description omits its behavior or timing.");
                Require(ModNames.All(name => ModCheckBox(window, name).IsChecked == false), "New mod choices should default to off.");
                var futureNames = new[] { "Custom opponent decks, in development", "New card rewards, in development" };
                var hd = Find<CheckBox>(window, "HdCardArtworkCheckBox");
                Require(!hd.IsEnabled && hd.IsChecked != true, "HD without a selected pack must be unavailable and off.");
                Require(!string.IsNullOrWhiteSpace(Find<TextBlock>(window, "HdCardArtworkDescription").Text), "Missing HD prerequisite explanation.");
                var mods = Find<StackPanel>(window, "ModsView");
                var availableBottom = ModNames.Max(name => Find<Grid>(window, name + "Row").TranslatePoint(
                    new Point(0, Find<Grid>(window, name + "Row").Bounds.Height), mods)!.Value.Y);
                foreach (var futureName in futureNames)
                {
                    var future = mods.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == futureName);
                    Require(!future.IsEnabled && future.IsChecked != true, "A future mod became selectable.");
                    Require(future.TranslatePoint(new Point(), mods)!.Value.Y >= availableBottom, "A future mod interrupts the available mod group.");
                }
                window.Width = window.MinWidth;
                Pump(TimeSpan.FromMilliseconds(80));
                foreach (var name in ModNames) CheckModRow(window, name);
                window.Width = 1100;
                Pump(TimeSpan.FromMilliseconds(80));
            });
            Capture(window, "mods.png");
            Check("saving a mod disables every choice and Play until persistence finishes", () =>
            {
                // Hold the real settings store's gate so the asynchronous save stays pending deterministically.
                var gate = SettingsGate(window);
                gate.Wait();
                try
                {
                    ClickInput(window, ModCheckBox(window, ModNames[0]));
                    Require(ModNames.All(name => !ModCheckBox(window, name).IsEnabled), "A mod remains enabled during a pending save.");
                    Require(Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>().All(c => !c.CanPlay),
                        "Play remained available before settings were saved.");
                    ClickInput(window, ModCheckBox(window, ModNames[1]));
                    ClickInput(window, ModCheckBox(window, ModNames[2]));
                    Require(ModCheckBox(window, ModNames[1]).IsChecked == false && ModCheckBox(window, ModNames[2]).IsChecked == false,
                        "A disabled mod accepted pointer input during another save.");
                }
                finally { gate.Release(); }
                WaitForModSelections(window, data, true, false, false);
                Require((Find<TextBlock>(window, "StatusText").Text ?? "").Contains("all three games", StringComparison.Ordinal),
                    "Mod status does not explain the global selection.");
                CheckPreservedSettings(data);
            });
            Capture(window, "mods-enabled.png");
            Check("character interactions compose with the enabled splash setting", () =>
            {
                ClickInput(window, ModCheckBox(window, ModNames[1]));
                WaitForModSelections(window, data, true, true, false);
                CheckPreservedSettings(data);
            });
            var legacy = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")))!.AsObject();
            legacy.Remove("RestoreOriginalAspectRatio");
            var legacySettings = legacy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            Check("original aspect ratio composes with both existing global mods", () =>
            {
                ClickInput(window, ModCheckBox(window, ModNames[2]));
                WaitForModSelections(window, data, true, true, true);
                Require((Find<TextBlock>(window, "StatusText").Text ?? "") == "Original aspect ratio enabled for all three games on their next launch.",
                    "Aspect ratio status does not explain its global next-launch scope.");
                CheckPreservedSettings(data);
            });
            Capture(window, "mods-all-enabled.png");
            Check("busy and pending launch states disable all mod controls without starting a game", () =>
            {
                // Exercise the real controls with synthetic state; no Play handler or game process is invoked.
                foreach (var fieldName in new[] { "_busy", "_pendingLaunch" })
                {
                    SetWindowState(window, fieldName, true);
                    try
                    {
                        Require(ModNames.All(name => !ModCheckBox(window, name).IsEnabled), "A blocked state left a mod enabled.");
                        foreach (var name in ModNames) ClickInput(window, ModCheckBox(window, name));
                        Require(ModNames.All(name => ModCheckBox(window, name).IsChecked == true), "Disabled pointer input changed a choice.");
                        CheckSavedSelections(data, true, true, true);
                    }
                    finally { SetWindowState(window, fieldName, false); }
                }
                Require(ModNames.All(name => ModCheckBox(window, name).IsEnabled), "Mod controls did not recover after blocked states.");
            });

            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
            window = OpenWindow();
            Check("reopening restores three games and all three enabled mods", () =>
            {
                WaitUntil(() => CardCount(window) == 3, "Reopened launcher did not restore three games.");
                Require(!Find<Border>(window, "LibraryEmpty").IsVisible, "Reopened library shows empty state.");
                WaitUntil(() => PortraitCount(window) == 3, "Reopened launcher did not reload portraits.");
                OpenMods(window);
                WaitForModSelections(window, data, true, true, true);
                CheckPreservedSettings(data);
            });
            Capture(window, "reopened-mods.png");
            Check("turning aspect ratio off preserves both other enabled choices", () =>
            {
                ClickInput(window, ModCheckBox(window, ModNames[2]));
                WaitForModSelections(window, data, true, true, false);
                CheckPreservedSettings(data);
            });
            Check("failed aspect ratio enable preserves both existing enabled mods", () =>
            {
                WithBlockedSettingsSave(data, () =>
                {
                    ClickInput(window, ModCheckBox(window, ModNames[2]));
                    WaitForFailedModSave(window);
                    CheckVisibleSelections(window, true, true, false);
                    Require(CardCount(window) == 3, "Failed aspect ratio save changed the library.");
                });
                CheckSavedSelections(data, true, true, false);
                CheckPreservedSettings(data);
            });
            ClickInput(window, ModCheckBox(window, ModNames[2]));
            WaitForModSelections(window, data, true, true, true);
            Check("each failed disable restores only that choice with the other two enabled", () =>
            {
                WithBlockedSettingsSave(data, () =>
                {
                    foreach (var name in ModNames)
                    {
                        ClickInput(window, ModCheckBox(window, name));
                        WaitForFailedModSave(window);
                        CheckVisibleSelections(window, true, true, true);
                    }
                    Require(CardCount(window) == 3, "Failed saves changed the library.");
                });
                CheckSavedSelections(data, true, true, true);
                CheckPreservedSettings(data);
            });
            Check("existing mods can be disabled independently while aspect ratio stays enabled", () =>
            {
                ClickInput(window, ModCheckBox(window, ModNames[0]));
                WaitForModSelections(window, data, false, true, true);
                ClickInput(window, ModCheckBox(window, ModNames[1]));
                WaitForModSelections(window, data, false, false, true);
                CheckPreservedSettings(data);
            });
            Check("aspect ratio can be disabled after the other mods", () =>
            {
                ClickInput(window, ModCheckBox(window, ModNames[2]));
                WaitForModSelections(window, data, false, false, false);
                CheckPreservedSettings(data);
            });

            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
            window = OpenWindow();
            OpenMods(window);
            Check("reopening keeps all three disabled choices disabled", () =>
            {
                WaitForModSelections(window, data, false, false, false);
                Require(CardCount(window) == 3, "Second reopen lost existing installations.");
                CheckPreservedSettings(data);
            });
            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
            File.WriteAllText(Path.Combine(data, "launcher-settings.json"), legacySettings);
            window = new MainWindow();
            var loadingGate = SettingsGate(window);
            loadingGate.Wait();
            Check("loading legacy settings keeps every mod disabled until settings are ready", () =>
            {
                try
                {
                    window.Show();
                    OpenMods(window);
                    Require(ModNames.All(name => !ModCheckBox(window, name).IsEnabled), "A mod was available before settings loaded.");
                    foreach (var name in ModNames) ClickInput(window, ModCheckBox(window, name));
                    Require(ModNames.All(name => ModCheckBox(window, name).IsChecked == false), "Loading controls accepted a choice.");
                }
                finally { loadingGate.Release(); }
            });
            Check("legacy settings default aspect ratio off and preserve existing choices and preferences", () =>
            {
                WaitUntil(() => ModNames.All(name => ModCheckBox(window, name).IsEnabled), "Legacy settings did not finish loading.");
                CheckVisibleSelections(window, true, true, false);
                CheckSavedSelections(data, true, true, false);
                Require(CardCount(window) == 3, "Legacy settings lost installed games.");
                CheckPreservedSettings(data);
                ClickInput(window, ModCheckBox(window, ModNames[2]));
                WaitForModSelections(window, data, true, true, true);
                CheckPreservedSettings(data);
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

    private static CheckBox ModCheckBox(MainWindow window, string name) => Find<CheckBox>(window, name + "CheckBox");

    private static void CheckModRow(MainWindow window, string name)
    {
        var row = Find<Grid>(window, name + "Row");
        var checkbox = ModCheckBox(window, name);
        var title = Find<TextBlock>(window, name + "Title");
        var description = Find<TextBlock>(window, name + "Description");
        Require(checkbox.IsEnabled, name + " should be available.");
        Require(checkbox.Bounds.Right <= title.Bounds.Left && title.Bounds.Right <= description.Bounds.Left,
            name + " checkbox, title and description are not ordered left to right.");
        Require(description.Bounds.Width > 0 && description.Bounds.Right <= row.Bounds.Width + 1,
            name + " description does not fit its row.");
    }

    private static void CheckVisibleSelections(MainWindow window, params bool[] expected)
    {
        for (var i = 0; i < ModNames.Length; i++)
            Require(ModCheckBox(window, ModNames[i]).IsChecked == expected[i], "Unexpected checkbox state for " + ModNames[i]);
    }

    private static void CheckSavedSelections(string data, params bool[] expected)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
        for (var i = 0; i < ModProperties.Length; i++)
        {
            var selected = settings.RootElement.TryGetProperty(ModProperties[i], out var value) && value.GetBoolean();
            Require(selected == expected[i], "Unexpected saved selection for " + ModProperties[i]);
        }
    }

    private static void WaitForModSelections(MainWindow window, string data, params bool[] expected)
    {
        WaitUntil(() => ModNames.All(name => ModCheckBox(window, name).IsEnabled), "Mod controls did not finish saving or loading.");
        CheckVisibleSelections(window, expected);
        CheckSavedSelections(data, expected);
    }

    private static void WaitForFailedModSave(MainWindow window) => WaitUntil(
        () => ModNames.All(name => ModCheckBox(window, name).IsEnabled)
            && (Find<TextBlock>(window, "StatusText").Text ?? "").Contains("previous selection was kept", StringComparison.Ordinal),
        "Failed save did not restore the previous selection.");

    private static void WithBlockedSettingsSave(string data, Action action)
    {
        var path = Path.Combine(data, "launcher-settings.json");
        var backup = path + ".test-backup";
        File.Move(path, backup);
        Directory.CreateDirectory(path); // Synthetic test folder blocks atomic replacement.
        try { action(); }
        finally
        {
            Directory.Delete(path);
            File.Move(backup, path);
        }
    }

    private static SemaphoreSlim SettingsGate(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var store = typeof(MainWindow).GetField("_store", flags)?.GetValue(window)
            ?? throw new InvalidOperationException("Settings store was not found.");
        return store.GetType().GetField("_gate", flags)?.GetValue(store) as SemaphoreSlim
            ?? throw new InvalidOperationException("Settings gate was not found.");
    }

    private static void SetWindowState(MainWindow window, string fieldName, bool value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(MainWindow).GetField(fieldName, flags) ?? throw new InvalidOperationException("Missing window state: " + fieldName);
        field.SetValue(window, value);
        var refresh = typeof(MainWindow).GetMethod("RefreshModControls", flags) ?? throw new InvalidOperationException("Missing mod refresh method.");
        refresh.Invoke(window, null);
        Pump(TimeSpan.FromMilliseconds(30));
    }

    private static void CheckPreservedSettings(string data)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
        foreach (var property in new[] { "Installations", "SearchRoots", "MinimizeOnLaunch", "WineExecutable" })
            Require(settings.RootElement.GetProperty(property).GetRawText() == preservedSettings.GetProperty(property).GetRawText(),
                "Mod save changed " + property + ".");
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
