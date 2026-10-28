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
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PowerOfChaos.Launcher;
using PowerOfChaos.Launcher.Views;
using PowerOfChaos.Launcher.ViewModels;
using PowerOfChaos.Launcher.TestFixtures;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Collections;

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

            CollectionViewModelChecks.RunAll(Check);

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

            Check("launcher diagnostics can be selected and copied in full without changing the message", () =>
            {
                const string message = "Could not start the game: An error occurred trying to start process 'C:\\Synthetic game folder\\kaiba_pc.exe'. The system cannot find the file specified.";
                typeof(MainWindow).GetMethod("Notice", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [message]);
                Pump(TimeSpan.FromMilliseconds(80));
                var status = Find<SelectableTextBlock>(window, "StatusText");
                Require(status.Focus(), "Diagnostic text cannot receive keyboard focus.");
                window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
                Require(status.SelectedText == message, "Ctrl+A did not select the complete diagnostic.");
                window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
                var clipboard = window.Clipboard ?? throw new InvalidOperationException("Headless clipboard is unavailable.");
                var copied = clipboard.TryGetTextAsync();
                WaitUntil(() => copied.IsCompleted, "Keyboard copy did not reach the clipboard.");
                Require(copied.GetAwaiter().GetResult() == message, "Ctrl+C changed or truncated the diagnostic.");
                status.ClearSelection();
                clipboard.SetTextAsync("previous clipboard").GetAwaiter().GetResult();
                ClickInput(window, Find<Button>(window, "CopyStatusButton"));
                WaitUntil(() => Find<Button>(window, "CopyStatusButton").Content?.ToString() == "Copied", "Copy button did not complete.");
                copied = clipboard.TryGetTextAsync();
                WaitUntil(() => copied.IsCompleted, "Copy button did not reach the clipboard.");
                Require(copied.GetAwaiter().GetResult() == message && status.Text == message, "Copy button changed or truncated the diagnostic.");
            });
            window.Width = 880; window.Height = 600;
            Capture(window, "copyable-launch-error.png");
            Check("long diagnostics scroll within the banner and retain their complete copyable text", () =>
            {
                var message = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"Diagnostic line {i}: C:\\synthetic fixture path\\game.exe"));
                typeof(MainWindow).GetMethod("Notice", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [message]);
                Pump(TimeSpan.FromMilliseconds(80));
                Require(Find<Border>(window, "StatusBanner").Bounds.Height <= 204, "A diagnostic hid the entire game library.");
                Require(Find<SelectableTextBlock>(window, "StatusText").Text == message, "A long diagnostic was truncated.");
                Require(Find<Button>(window, "CopyStatusButton").Content?.ToString() == "Copy", "A new message kept stale copy feedback.");
            });
            Capture(window, "copyable-long-error.png");
            Find<Border>(window, "StatusBanner").IsVisible = false;
            window.Width = 1100; window.Height = 720;
            Pump(TimeSpan.FromMilliseconds(150));

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
            Check("portrait display stores premultiplied coverage and prepares physical pixels for fractional DPI", () =>
            {
                var installation = new GameInstallation("kaiba", "unused", "unused", "unused", "unused");
                using var card = new GameCardViewModel(installation);
                card.SetPortrait(new PortraitThumbnail(1, 1, [64, 0, 0, 128]));
                using (var frame = ((WriteableBitmap)card.Portrait!).Lock())
                {
                    var pixel = new byte[4];
                    System.Runtime.InteropServices.Marshal.Copy(frame.Address, pixel, 0, 4);
                    Require(pixel.SequenceEqual(new byte[] { 64, 0, 0, 128 }), "Partial coverage was multiplied twice or replaced with an opaque edge.");
                }
                using (var rendered = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96, 96)))
                {
                    using (var context = rendered.CreateDrawingContext())
                    {
                        context.FillRectangle(Brushes.Black, new Rect(0, 0, 1, 1));
                        context.DrawImage(card.Portrait!, new Rect(0, 0, 1, 1));
                    }
                    using var png = new MemoryStream();
                    rendered.Save(png);
                    using var decoded = SKBitmap.Decode(png.ToArray()) ?? throw new InvalidOperationException("Synthetic render could not be decoded.");
                    var color = decoded.GetPixel(0, 0);
                    Require(color.Blue == 64 && color.Red == 0 && color.Green == 0 && color.Alpha == 255,
                        "Drawing the partial-coverage pixel multiplied alpha twice or introduced a matte fringe.");
                }
                var sourcePixels = new byte[320 * 280 * 4];
                for (var y = 0; y < 280; y++)
                for (var x = 0; x < 320; x++)
                {
                    var offset = (y * 320 + x) * 4;
                    sourcePixels[offset] = y >= 140 ? (byte)255 : (byte)0;
                    sourcePixels[offset + 1] = x >= 160 ? (byte)255 : (byte)0;
                    sourcePixels[offset + 2] = x < 160 && y < 140 || x >= 160 && y >= 140 ? (byte)255 : (byte)0;
                    sourcePixels[offset + 3] = 255;
                }
                var source = new PortraitPixels(320, 280, sourcePixels);
                foreach (var (scale, expected) in new[]
                {
                    (1d, new PixelSize(112, 98)), (1.25, new PixelSize(140, 123)),
                    (1.5, new PixelSize(168, 147)), (2d, new PixelSize(224, 196))
                })
                {
                    var width = (int)Math.Ceiling(112 * scale);
                    var height = (int)Math.Ceiling(104 * scale);
                    card.SetPortrait(PortraitThumbnail.Create(source, width, height));
                    Require(card.Portrait!.PixelSize == expected && card.Portrait.Dpi == new Vector(96, 96),
                        "Thumbnail did not use the physical display footprint.");
                    var image = new Image { Source = card.Portrait, Width = 112, Height = 104, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                    image.Measure(new Size(112, 104));
                    image.Arrange(new Rect(0, 0, 112, 104));
                    using var render = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
                    render.Render(image);
                    using var bytes = new MemoryStream();
                    render.Save(bytes);
                    using var decoded = SKBitmap.Decode(bytes.ToArray()) ?? throw new InvalidOperationException("Quadrant render could not be decoded.");
                    var samples = new[] { decoded.GetPixel(width / 4, height / 4), decoded.GetPixel(width * 3 / 4, height / 4),
                        decoded.GetPixel(width / 4, height * 3 / 4), decoded.GetPixel(width * 3 / 4, height * 3 / 4) };
                    Require(samples.SequenceEqual(new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White }),
                        "A scaled portrait cropped or zoomed into a source quadrant.");
                }
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

            var collectionSave = Path.Combine(runRoot, "synthetic-system.dat");
            File.WriteAllBytes(collectionSave, CollectionFixtures.Save(
                new[] { 0, 1, 154, 155, 465, 466, 770 }.Select(card => (CollectionFixtures.Slot(card), (byte)3))));
            Check("Collection displays per-game progress and a deduplicated shared total", () =>
            {
                Find<Button>(window, "CollectionNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<TextBox>(window, "CollectionSaveInput").Text = collectionSave;
                Find<StackPanel>(window, "CollectionView").GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Content?.ToString() == "Use path").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<TextBlock>(window, "UniqueCardCount").Text == "7/771", "Shared unique count was not displayed.");
                var rows = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().ToArray();
                Require(rows.Select(row => row.Count).SequenceEqual(new[] { "3/155", "5/466", "7/771" }), "Collection pool counts differ.");
                Require(Find<TextBlock>(window, "CollectionStatus").Text!.Contains("Duplicate cards count once", StringComparison.Ordinal), "Shared counting is unexplained.");
                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
                Require(settings.RootElement.GetProperty("CollectionSavePath").GetString() == collectionSave, "Selected save path did not persist.");
            });
            Capture(window, "collection.png");
            CheckNativeGalleries(window, "synthetic");
            CheckCardLanguages(window, data);
            Check("Games displays the same card counts beside playtime and Play", () =>
            {
                Find<Button>(window, "LibraryNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var cards = Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>().ToArray();
                Require(cards.Select(card => card.CollectionCount).SequenceEqual(new[] { "3/155", "5/466", "7/771" }), "Games and Collection counts disagree.");
                window.Width = 880; window.Height = 600;
                Pump(TimeSpan.FromMilliseconds(150));
                foreach (var button in Find<ItemsControl>(window, "GameCards").GetVisualDescendants().OfType<Button>())
                    Require(button.Bounds.Width >= 90, "Play button was squeezed by the card counter.");
            });
            Capture(window, "games-collection-minimum.png");
            window.Width = 1100; window.Height = 720;
            Check("winning new card types refreshes both tabs across all three game pools", () =>
            {
                var cards = new List<int> { 0, 1, 154, 155, 465, 466, 770 };
                foreach (var (card, expected) in new[]
                {
                    (100, new[] { "4/155", "6/466", "8/771" }),
                    (300, new[] { "4/155", "7/466", "9/771" }),
                    (600, new[] { "4/155", "7/466", "10/771" })
                })
                {
                    Find<Button>(window, "CollectionNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitUntil(() => Find<Button>(window, "RefreshCollectionButton").IsEnabled, "Navigation refresh did not finish.");
                    var gallery = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().Last();
                    gallery.IsExpanded = true;
                    cards.Add(card);
                    File.WriteAllBytes(collectionSave, CollectionFixtures.Save(cards.Select(c => (CollectionFixtures.Slot(c), (byte)2))));
                    Find<Button>(window, "RefreshCollectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitUntil(() => Find<TextBlock>(window, "UniqueCardCount").Text == cards.Count + "/771", "New reward was not refreshed.");
                    Require(Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>()
                        .Select(row => row.Count).SequenceEqual(expected), "Collection used incorrect per-game pools after a reward.");
                    WaitUntil(() => gallery.Cards.All(item => item.HasArtwork), "Expanded gallery did not reload after a reward.");
                    Require(gallery.OwnedCards.Count == cards.Count && gallery.OwnedCards.Any(item => item.CardId == card),
                        "The reward remained in the missing gallery.");
                    gallery.IsExpanded = false;
                    Find<Button>(window, "LibraryNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>()
                        .Select(row => row.CollectionCount).SequenceEqual(expected), "Games retained stale reward counts.");
                }
            });
            Check("Collection refresh reads save changes and damaged saves never become zero", () =>
            {
                Find<Button>(window, "CollectionNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<Button>(window, "RefreshCollectionButton").IsEnabled, "Collection navigation refresh did not finish.");
                File.WriteAllBytes(collectionSave, CollectionFixtures.Save([]));
                Find<Button>(window, "RefreshCollectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<TextBlock>(window, "UniqueCardCount").Text == "0/771", "Valid empty collection was not refreshed.");
                File.WriteAllBytes(collectionSave, new byte[31]);
                Find<Button>(window, "RefreshCollectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<TextBlock>(window, "UniqueCardCount").Text == "—/771", "Invalid collection retained a fabricated count.");
                Require(Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>()
                    .All(card => card.CollectionCount.StartsWith("—/", StringComparison.Ordinal)), "Games retained stale counts after save damage.");
                Require(Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>()
                    .All(row => !row.HasCards && row.Cards.Count == 0), "An unreadable save fabricated an all-missing gallery.");
            });
            Capture(window, "collection-unavailable.png");
            File.WriteAllBytes(collectionSave, CollectionFixtures.Save(
                new[] { 0, 1, 154, 155, 465, 466, 770 }.Select(card => (CollectionFixtures.Slot(card), (byte)3))));
            Find<Button>(window, "RefreshCollectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => Find<TextBlock>(window, "UniqueCardCount").Text == "7/771", "Restored synthetic save did not refresh.");
            Check("an overlong pasted save path is rejected without changing the selected collection", () =>
            {
                Find<TextBox>(window, "CollectionSaveInput").Text = Path.GetPathRoot(runRoot) + new string('x', 40000);
                Find<StackPanel>(window, "CollectionView").GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Content?.ToString() == "Use path").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<TextBlock>(window, "CollectionStatus").Text == "Enter the full path to system.dat.", "Overlong save path did not produce a validation message.");
                Require(Find<TextBlock>(window, "UniqueCardCount").Text == "7/771", "Rejected path changed the active count.");
                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
                Require(settings.RootElement.GetProperty("CollectionSavePath").GetString() == collectionSave, "Rejected path changed the saved collection.");
                Find<TextBox>(window, "CollectionSaveInput").Text = collectionSave;
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
                var futureNames = new[] { "Custom opponent decks, in development", "New card rewards, in development", "HD card artwork, in development" };
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
            Find<ComboBox>(window, "CardLanguageSelector").SelectedItem = CardLanguages.Get("fr");
            WaitUntil(() => Find<ComboBox>(window, "CardLanguageSelector").IsEnabled, "Card language save did not finish.");
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
            Check("card language survives reopening the launcher", () =>
            {
                var selector = Find<ComboBox>(window, "CardLanguageSelector");
                Require(selector.SelectedItem is CardLanguage { Id: "fr" }, "Reopening lost the selected card language.");
                selector.SelectedItem = CardLanguages.Get("en");
                WaitUntil(() => selector.IsEnabled, "Restoring English did not finish.");
            });
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
                        ["yugi"] = new(110, 104), ["kaiba"] = new(112, 85), ["joey"] = new(112, 100)
                    };
                    foreach (var card in Find<ItemsControl>(window, "GameCards").Items.OfType<GameCardViewModel>())
                        Require(card.Portrait?.PixelSize == expected[card.Installation.GameId], "Installed portrait thumbnail does not fit its display-pixel footprint.");
                });
                Capture(window, "installed-portraits.png");
                Find<Button>(window, "CollectionNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<Button>(window, "RefreshCollectionButton").IsEnabled, "Installed collection refresh did not finish.");
                CheckNativeGalleries(window, "installed");
            }

            Check("closing during gallery loading cancels work and releases card images", () =>
            {
                Find<Button>(window, "CollectionNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => Find<Button>(window, "RefreshCollectionButton").IsEnabled, "Final collection refresh did not finish.");
                var rows = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().ToArray();
                foreach (var row in rows) row.IsExpanded = true;
                window.Close();
                Pump(TimeSpan.FromMilliseconds(200));
                Require(rows.All(row => row.Cards.All(card => !card.HasArtwork)), "Late artwork survived window close.");
                var loads = (System.Collections.IDictionary)typeof(MainWindow).GetField("_galleryLoads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Require(loads.Count == 0, "Window close retained active gallery loads.");
            });

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

    private static void CheckCardLanguages(MainWindow window, string data)
    {
        var selector = Find<ComboBox>(window, "CardLanguageSelector");
        var rows = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().ToArray();
        var row = rows[0];
        row.IsExpanded = true;
        WaitUntil(() => row.Cards.All(card => card.HasArtwork && card.CardName is not null), "Initial card names did not load.");
        var originalCards = row.Cards.ToArray();
        var originalCounts = rows.Select(item => item.Count).ToArray();
        Check("six card languages appear below Settings and update tooltips from native slot records", () =>
        {
            Require(selector.Items.OfType<CardLanguage>().Select(item => item.Id).Order().SequenceEqual(new[] { "de", "en", "es", "fr", "it", "ja" }),
                "Language selector does not contain the six supported native languages.");
            var settings = Find<Button>(window, "SettingsButton");
            var selectorPoint = selector.TranslatePoint(new Point(), window)!.Value;
            var settingsPoint = settings.TranslatePoint(new Point(0, settings.Bounds.Height), window)!.Value;
            Require(selectorPoint.Y > settingsPoint.Y && selectorPoint.Y + selector.Bounds.Height <= window.Bounds.Height,
                "Card language selector is not visible beneath Settings.");
            foreach (var language in new[] { "de", "fr", "it", "es", "ja", "en" })
            {
                selector.SelectedItem = CardLanguages.Get(language);
                WaitUntil(() => selector.IsEnabled && row.Cards.All(card => card.CardName == CollectionFixtures.CardName(language, card.Slot)
                    && card.HasArtwork), "Language change left missing or stale names: " + language);
                Require(row.Cards.SequenceEqual(originalCards) && rows.Select(item => item.Count).SequenceEqual(originalCounts),
                    "Changing language changed card identities or ownership.");
                Require(row.Cards.All(card => card.Tooltip.StartsWith(card.CardName!, StringComparison.Ordinal)
                    && card.Tooltip.Contains("First available in", StringComparison.Ordinal)), "A tooltip retained its ID heading or lost origin.");
                var first = row.OwnedCards.First();
                var tile = Find<ItemsControl>(window, "CollectionGames").GetVisualDescendants().OfType<Border>()
                    .First(border => ReferenceEquals(border.DataContext, first) && ToolTip.GetTip(border) is string);
                Require(ToolTip.GetTip(tile)?.ToString() == first.Tooltip, "The actual tooltip binding retained its previous language.");
                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "launcher-settings.json")));
                Require(saved.RootElement.GetProperty("CardLanguage").GetString() == language, "Language choice was not persisted.");
            }
        });
        Check("a failed language save restores the previous selection and displayed names", () =>
        {
            WithBlockedSettingsSave(data, () =>
            {
                selector.SelectedItem = CardLanguages.Get("de");
                WaitUntil(() => selector.IsEnabled && selector.SelectedItem is CardLanguage { Id: "en" }
                    && row.Cards.All(card => card.CardName == CollectionFixtures.CardName("en", card.Slot)),
                    "Failed language save did not restore the selector.");
                Require(row.Cards.All(card => card.CardName == CollectionFixtures.CardName("en", card.Slot)),
                    "Failed language save changed visible names.");
            });
        });
        Check("rapid language changes and collapse cannot publish stale names", () =>
        {
            foreach (var language in new[] { "ja", "de", "fr", "en" })
            {
                selector.SelectedItem = CardLanguages.Get(language);
                WaitUntil(() => selector.IsEnabled, "Language save remained pending.");
            }
            WaitUntil(() => row.Cards.All(card => card.CardName == CollectionFixtures.CardName("en", card.Slot)), "A stale language won the load race.");
            row.IsExpanded = false;
            Pump(TimeSpan.FromMilliseconds(100));
            Require(row.Cards.All(card => card.CardName is null && !card.HasArtwork), "Collapsed gallery retained late name or image results.");
        });
        Check("a failed language save restarts an expanded gallery after its in-flight load retired", () =>
        {
            WithBlockedSettingsSave(data, () =>
            {
                var gate = SettingsGate(window);
                var released = false;
                gate.Wait();
                try
                {
                    var loads = (System.Collections.IDictionary)typeof(MainWindow).GetField("_galleryLoads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    row.IsExpanded = true;
                    Require(loads.Count > 0, "Race fixture did not start an in-flight gallery load.");
                    selector.SelectedItem = CardLanguages.Get("de");
                    Require(!selector.IsEnabled, "Race fixture did not hold the preference save.");
                    WaitUntil(() => loads.Count == 0, "The superseded language load did not retire.");
                    gate.Release(); released = true;
                    WaitUntil(() => selector.IsEnabled && selector.SelectedItem is CardLanguage { Id: "en" }, "Failed save did not restore English.");
                    WaitUntil(() => row.Cards.All(card => card.HasArtwork && card.CardName == CollectionFixtures.CardName("en", card.Slot)),
                        "Expanded gallery remained stuck after a failed language save.");
                    Require(row.IsExpanded, "Recovering a failed language save collapsed the gallery.");
                }
                finally { if (!released) gate.Release(); }
            });
            row.IsExpanded = false;
        });
        Find<Border>(window, "StatusBanner").IsVisible = false;
        Capture(window, "collection-padded-headers.png");
        row.IsExpanded = true;
        WaitUntil(() => row.Cards.All(card => card.HasArtwork && card.CardName is not null), "Named gallery did not reopen.");
        Capture(window, "collection-named-cards.png");
        row.IsExpanded = false;
        Find<Border>(window, "StatusBanner").IsVisible = false;
    }

    private static void CheckNativeGalleries(MainWindow window, string source)
    {
        var rows = Find<ItemsControl>(window, "CollectionGames").Items.OfType<CollectionGameViewModel>().ToArray();
        Require(rows.Length == 3, "Collection lost a game panel.");
        foreach (var row in rows)
        {
            Check(source + " " + row.Progress.GameId + " gallery loads native owned and missing cards on expansion", () =>
            {
                var expander = Find<ItemsControl>(window, "CollectionGames").GetVisualDescendants().OfType<Expander>()
                    .Single(control => ReferenceEquals(control.DataContext, row));
                Require(!expander.IsExpanded && !row.IsExpanded, "Gallery did not start collapsed.");
                Require(row.Cards.All(card => !card.HasArtwork), "Collapsed gallery eagerly loaded artwork.");
                Require(row.HasCount && row.HasCards && row.Cards.Count == row.Progress.TotalCards, "Gallery pool is incomplete.");
                Require(row.OwnedCards.Count == row.Progress.OwnedCards && row.OwnedCards.Count + row.MissingCards.Count == row.Cards.Count,
                    "Owned/missing partition disagrees with the card counter.");
                expander.IsExpanded = true;
                WaitUntil(() => row.IsExpanded && row.Cards.All(card => card.HasArtwork), "Not every native mini loaded: " + row.GalleryStatus);
                Pump(TimeSpan.FromMilliseconds(250));
                Require(row.Cards.All(card => card.ArtworkWidth is 49 or 50 && card.ArtworkHeight == 72), "A native mini was enlarged or distorted.");
                Require(row.Cards.All(card => card.CardName is not null && card.Tooltip.StartsWith(card.CardName, StringComparison.Ordinal)),
                    "Card names were not loaded before their artwork.");
                Require(row.OwnedCards.All(card => card.ArtworkOpacity == 1) && row.MissingCards.All(card => card.ArtworkOpacity < 1),
                    "Missing cards cannot be distinguished from owned cards.");
                var images = expander.GetVisualDescendants().OfType<Image>().Where(image => image.DataContext is CollectionCardViewModel).ToArray();
                Require(images.Length == row.Cards.Count && images.All(image => image.Bounds.Width <= 50 && image.Bounds.Height <= 72),
                    "Rendered cards exceed their native size.");
                var first = images.First(image => ((CollectionCardViewModel)image.DataContext!).IsOwned);
                var border = first.GetVisualAncestors().OfType<Border>().First(control => control.DataContext is CollectionCardViewModel);
                var card = (CollectionCardViewModel)first.DataContext!;
                Require(ToolTip.GetTip(border)?.ToString() == card.Tooltip && card.Tooltip.Contains("First available in", StringComparison.Ordinal),
                    "Card tooltip does not identify its game.");
                Require(!first.GetVisualAncestors().TakeWhile(control => control != expander).Any(control => control is Button),
                    "A card unexpectedly has an activation button.");
                var size = first.Bounds.Size;
                var ownedWindows = window.OwnedWindows.Count();
                var point = border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Pump(TimeSpan.FromMilliseconds(80));
                Require(first.Bounds.Size == size && window.OwnedWindows.Count() == ownedWindows, "Clicking a card changed its size or opened a preview.");
                Capture(window, source + "-" + row.Progress.GameId + "-gallery.png");
                if (row.Progress.GameId == "yugi")
                {
                    var width = window.Width; var height = window.Height;
                    window.Width = 880; window.Height = 600;
                    Pump(TimeSpan.FromMilliseconds(100));
                    Require(images.All(image => image.Bounds.Width <= 50 && image.Bounds.Height <= 72), "Minimum-size window enlarged native cards.");
                    Capture(window, source + "-gallery-minimum.png");
                    window.Width = width; window.Height = height;
                }
                var previous = row.Cards.First().Artwork;
                expander.IsExpanded = false;
                WaitUntil(() => !row.IsExpanded && row.Cards.All(item => !item.HasArtwork), "Collapsing retained card bitmaps.");
                expander.IsExpanded = true;
                expander.IsExpanded = false;
                Pump(TimeSpan.FromMilliseconds(100));
                Require(row.Cards.All(item => !item.HasArtwork), "A cancelled load repopulated a collapsed gallery.");
                expander.IsExpanded = true;
                WaitUntil(() => row.Cards.All(item => item.HasArtwork), "Reopening a gallery did not reload cards.");
                Require(!ReferenceEquals(previous, row.Cards.First().Artwork), "Reopening reused a retired bitmap.");
                expander.IsExpanded = false;
                Pump(TimeSpan.FromMilliseconds(80));
            });
        }
    }

    private static MainWindow OpenWindow()
    {
        var window = new MainWindow();
        // Ordinary UI checks use authored saves only, regardless of the user's real registry CommonDir.
        typeof(MainWindow).GetField("_collections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, new PowerOfChaos.Launcher.Core.Collections.CardCollectionReader(_ => []));
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
