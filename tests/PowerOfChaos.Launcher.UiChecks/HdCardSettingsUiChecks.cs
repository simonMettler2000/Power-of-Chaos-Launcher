using System.Buffers.Binary;
using System.Text;
using System.Reflection;
using PowerOfChaos.Launcher.Core.Storage;
using PowerOfChaos.Launcher.Core.Collections;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Localization;
using PowerOfChaos.Launcher.Views;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class HdCardSettingsUiChecks
{
    public static void RunAll(MainWindow owner, string root, string data, Action<string, Action> check,
        Action<Window, string> capture, Action<TimeSpan> pump, Action<Func<bool>, string> wait)
    {
        var text = LauncherText.Current;
        var images = Path.Combine(root, "hd-image-selection"); var pack = Path.Combine(root, "prepared-hd-pack");
        Directory.CreateDirectory(images); Directory.CreateDirectory(pack);
        var png = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13); "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 800); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 1160); png[24] = 8; png[25] = 2;
        File.WriteAllBytes(Path.Combine(images, "master.png"), png); File.WriteAllBytes(Path.Combine(pack, "reference.bin"), new byte[116000]);
        foreach (var game in GameCatalog.All)
            File.WriteAllText(Path.Combine(pack, game.Id + "-565.tsv"), $"POC_HD_VISIBLE_V1\n{(game.Id == "yugi" ? 1 : game.Id == "kaiba" ? 2 : 3)}\t1\n1\t{Path.Combine(pack, "reference.bin")}\t{Path.Combine(images, "master.png")}\n", new UTF8Encoding(false));
        check("HD row directly opens folder configuration and help", () =>
        {
            Find<Button>(owner, "ModsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var button = Find<Button>(owner, "HdConfigureButton");
            Require(button.IsEnabled && AutomationProperties.GetName(button) == text.Translate("Configure HD card image folders and open help"), "Direct accessible action is missing.");
            Click(button); wait(() => owner.OwnedWindows.OfType<HdCardSettingsDialog>().Any(), "HD help did not open.");
            var dialog = owner.OwnedWindows.OfType<HdCardSettingsDialog>().Single();
            Require(dialog.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.Contains("PNG images alone", StringComparison.Ordinal) == true), "Prepared-pack requirement is missing.");
            capture(dialog, "hd-help-direct.png"); Click(Find<Button>(dialog, "HdCancelPaths"));
            wait(() => !owner.OwnedWindows.Contains(dialog), "Help cancel did not close.");
        });
        check("HD folder browse targets images separately, cancellation preserves edits and dialog cancel changes no settings", () =>
        {
            var settings = new LauncherSettings { HdPackDirectory = pack, HdCardImageDirectory = "before" };
            var titles = new List<string>(); var picks = new Queue<string?>([images, null, pack]);
            var dialog = new HdCardSettingsDialog(settings, title => { titles.Add(title); return Task.FromResult(picks.Dequeue()); });
            var result = dialog.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(50));
            Click(Find<Button>(dialog, "HdBrowseImages")); Require(Find<TextBox>(dialog, "HdCardImageDirectory").Text == images, "Image folder went to pack control.");
            Click(Find<Button>(dialog, "HdBrowseImages")); Require(Find<TextBox>(dialog, "HdCardImageDirectory").Text == images, "Cancelled picker erased selection.");
            Click(Find<Button>(dialog, "HdBrowsePack")); Require(Find<TextBox>(dialog, "HdPreparedPackDirectory").Text == pack && titles[0] != titles[2], "Prepared pack picker is not distinct.");
            Click(Find<Button>(dialog, "HdCancelPaths")); wait(() => result.IsCompleted, "Cancel result remained pending.");
            Require(result.Result is null && settings.HdCardImageDirectory == "before" && settings.HdPackDirectory == pack, "Cancel modified settings.");
        });
        check("HD folders save validated captured paths and reset restores pack image paths", () =>
        {
            var dialog = new HdCardSettingsDialog(new LauncherSettings { HdPackDirectory = pack, HdCardImageDirectory = images });
            var result = dialog.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(50));
            Click(Find<Button>(dialog, "HdSavePaths")); wait(() => result.IsCompleted, "Valid HD save did not complete.");
            Require(result.Result == new HdCardSettingsResult(images, pack), "Saved paths differ from validated selection.");
            var reset = new HdCardSettingsDialog(new LauncherSettings { HdPackDirectory = pack, HdCardImageDirectory = images });
            var resetResult = reset.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(50));
            Click(Find<Button>(reset, "HdResetImages")); Click(Find<Button>(reset, "HdSavePaths"));
            wait(() => resetResult.IsCompleted, "Reset did not save."); Require(resetResult.Result?.ImageDirectory == "", "Reset kept override.");
        });
        check("HD help, controls, accessibility and existing filename errors switch live in all six languages", () =>
        {
            var missing = Path.Combine(root, "empty-hd-images"); Directory.CreateDirectory(missing);
            var dialog = new HdCardSettingsDialog(new LauncherSettings { HdPackDirectory = pack, HdCardImageDirectory = missing });
            dialog.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(50));
            Click(Find<Button>(dialog, "HdSavePaths")); wait(() => Find<Button>(dialog, "HdSavePaths").IsEnabled && Find<TextBlock>(dialog, "HdSettingsMessage").Text?.Length > 0, "Missing image was accepted.");
            var english = Find<TextBlock>(dialog, "HdSettingsMessage").Text!;
            Require(english.Contains("master.png", StringComparison.Ordinal), "Missing filename is not actionable.");
            foreach (var language in new[] { "en", "de", "es", "fr", "it", "ja" })
            {
                text.SetLanguage(language); pump(TimeSpan.FromMilliseconds(50));
                Require(dialog.Title == text.Translate("HD card images and help")
                    && Find<Button>(dialog, "HdSavePaths").Content?.ToString() == text.Translate("Save HD folders")
                    && AutomationProperties.GetName(Find<TextBox>(dialog, "HdCardImageDirectory")) == text.Translate("HD card image folder")
                    && Find<TextBlock>(dialog, "HdSettingsMessage").Text == text.Translate(english), "Dialog retained a previous language.");
                Require(language == "en" || Find<TextBlock>(dialog, "HdSettingsMessage").Text != english, "Error stayed English.");
                Require(dialog.GetLogicalDescendants().OfType<TextBlock>().Any(label => label.Text == text.Translate("Export PNGs without colour profiles or extra metadata. The launcher checks only PNG headers before launch. Unsupported or damaged image data can still show the original card in-game.")), "PNG export limitations did not follow the launcher language.");
                capture(dialog, "hd-help-" + language + ".png");
            }
            Click(Find<Button>(dialog, "HdCancelPaths")); text.SetLanguage("en");
        });
        check("HD save captures both paths before awaiting and closed dialogs reject late validation", () =>
        {
            var pending = new TaskCompletionSource(); string? capturedPack = null; string? capturedImages = null;
            var dialog = new HdCardSettingsDialog(new LauncherSettings { HdPackDirectory = pack, HdCardImageDirectory = images },
                validateSelection: (selectedPack, selectedImages, _) => { capturedPack = selectedPack; capturedImages = selectedImages; return pending.Task; });
            var result = dialog.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(30));
            Click(Find<Button>(dialog, "HdSavePaths"));
            Find<TextBox>(dialog, "HdCardImageDirectory").Text = "later-images"; Find<TextBox>(dialog, "HdPreparedPackDirectory").Text = "later-pack";
            Require(capturedPack == pack && capturedImages == images, "Pending validation saw mutable edits.");
            pending.SetResult(); wait(() => result.IsCompleted, "Captured save remained pending.");
            Require(result.Result == new HdCardSettingsResult(images, pack), "Save returned unvalidated later edits.");
            var late = new TaskCompletionSource();
            var closing = new HdCardSettingsDialog(new LauncherSettings { HdPackDirectory = pack, HdCardImageDirectory = images }, validateSelection: (_, _, _) => late.Task);
            var closed = closing.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(30));
            Click(Find<Button>(closing, "HdSavePaths")); Click(Find<Button>(closing, "HdCancelPaths"));
            late.SetResult(); pump(TimeSpan.FromMilliseconds(30));
            Require(closed.IsCompleted && closed.Result is null, "Closed validation published a saved selection.");
        });
        check("failed HD folder persistence restores both previous paths and preserves saved settings", () =>
        {
            var settings = Field<LauncherSettings>(owner, "_settings");
            var previousPack = settings.HdPackDirectory; var previousImages = settings.HdCardImageDirectory;
            new SettingsStore(data).SaveAsync(settings).GetAwaiter().GetResult();
            var path = Path.Combine(data, "launcher-settings.json"); var before = File.ReadAllBytes(path); var backup = path + ".hd-test-backup";
            File.Move(path, backup); Directory.CreateDirectory(path);
            try
            {
                var saving = SaveFolders(owner, new(images, pack)); wait(() => saving.IsCompleted, "Failed HD save remained pending.");
                saving.GetAwaiter().GetResult();
                Require(settings.HdPackDirectory == previousPack && settings.HdCardImageDirectory == previousImages, "Failed save changed active HD paths.");
                Require(Find<SelectableTextBlock>(owner, "StatusText").Text == text.Translate("The HD folders could not be saved. Your previous selection was kept."), "Failure did not explain rollback.");
            }
            finally { Directory.Delete(path); File.Move(backup, path); }
            Require(File.ReadAllBytes(path).SequenceEqual(before), "Failed HD save changed prior settings bytes.");
        });
        check("pending HD settings save excludes language and launch edits", () =>
        {
            var gate = Field<SemaphoreSlim>(Field<SettingsStore>(owner, "_store"), "_gate");
            var settings = Field<LauncherSettings>(owner, "_settings"); var language = settings.CardLanguage;
            var previous = new HdCardSettingsResult(settings.HdCardImageDirectory, settings.HdPackDirectory);
            gate.Wait(); Task saving;
            try
            {
                saving = SaveFolders(owner, new(images, pack));
                Require(!saving.IsCompleted && Field<bool>(owner, "_savingMod") && !Find<Button>(owner, "HdConfigureButton").IsEnabled
                    && !Find<ComboBox>(owner, "CardLanguageSelector").IsEnabled, "Pending HD save failed to block edits.");
                Find<ComboBox>(owner, "CardLanguageSelector").SelectedItem = PowerOfChaos.Launcher.Core.Collections.CardLanguages.Get("de");
                Require(settings.CardLanguage == language && (bool)typeof(MainWindow).GetProperty("LaunchBlocked", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!, "Pending save accepted language or enabled Play.");
            }
            finally { gate.Release(); }
            wait(() => saving.IsCompleted, "HD settings gate did not release."); saving.GetAwaiter().GetResult();
            var restoring = SaveFolders(owner, previous); wait(() => restoring.IsCompleted, "HD settings restore remained pending."); restoring.GetAwaiter().GetResult();
            // Closing during dialog validation is tested above; MainWindow's common save guard postpones close.
            Require(!Field<bool>(owner, "_savingMod") && Find<ComboBox>(owner, "CardLanguageSelector").SelectedItem is PowerOfChaos.Launcher.Core.Collections.CardLanguage selected
                && selected.Id == language, "Pending save did not restore editable language controls.");
        });
        check("closing the launcher waits for a pending atomic HD folder save", () =>
        {
            var secondaryData = Path.Combine(root, "hd-close-data");
            var previousData = Environment.GetEnvironmentVariable("POC_LAUNCHER_DATA_DIR"); MainWindow closing;
            try
            {
                Environment.SetEnvironmentVariable("POC_LAUNCHER_DATA_DIR", secondaryData);
                closing = new MainWindow(new TemporaryCollectionService(Path.Combine(secondaryData, "private-vault"),
                    reader: new CardCollectionReader(_ => []), hasRunningGames: () => false));
                typeof(MainWindow).GetField("_collections", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(closing, new CardCollectionReader(_ => []));
            }
            finally { Environment.SetEnvironmentVariable("POC_LAUNCHER_DATA_DIR", previousData); }
            closing.Show(); wait(() => Field<bool>(closing, "_settingsReady"), "Private closing fixture did not initialize.");
            var gate = Field<SemaphoreSlim>(Field<SettingsStore>(closing, "_store"), "_gate"); gate.Wait(); Task pending;
            try
            {
                pending = SaveFolders(closing, new(images, pack)); closing.Close();
                Require(!pending.IsCompleted && !Field<bool>(closing, "_closed") && Field<bool>(closing, "_closeWhenIdle"), "Pending HD persistence did not defer close.");
            }
            finally { gate.Release(); }
            wait(() => pending.IsCompleted && Field<bool>(closing, "_closed"), "Pending save did not finish and close its owner."); pending.GetAwaiter().GetResult();
            var loaded = new SettingsStore(secondaryData).LoadAsync().GetAwaiter().GetResult();
            Require(loaded.HdCardImageDirectory == images && loaded.HdPackDirectory == pack, "Deferred close lost a successful save.");
        });
        check("closed HD dialog ignores a late image picker result", () =>
        {
            var pending = new TaskCompletionSource<string?>();
            var dialog = new HdCardSettingsDialog(new LauncherSettings { HdCardImageDirectory = "before" }, _ => pending.Task);
            dialog.ShowDialog<HdCardSettingsResult?>(owner); pump(TimeSpan.FromMilliseconds(50));
            Click(Find<Button>(dialog, "HdBrowseImages")); dialog.Close(); pending.SetResult(images); pump(TimeSpan.FromMilliseconds(50));
            Require(Find<TextBox>(dialog, "HdCardImageDirectory").Text == "before", "Closed dialog published late folder selection.");
        });
        Find<Button>(owner, "LibraryNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static T Field<T>(object scope, string name) => (T)(scope.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!);
    private static Task SaveFolders(MainWindow owner, HdCardSettingsResult result) => (Task)typeof(MainWindow).GetMethod("SaveHdFoldersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [result])!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(Control scope, string name) where T : Control => scope.GetLogicalDescendants().OfType<T>().SingleOrDefault(control => control.Name == name) ?? throw new InvalidOperationException("Missing HD settings control: " + name);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
