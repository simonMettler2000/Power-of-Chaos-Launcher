using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Collections;
using PowerOfChaos.Launcher.Core.Localization;
using PowerOfChaos.Launcher.ViewModels;
using PowerOfChaos.Launcher.Views;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class LaunchStateUiChecks
{
    // Only synthetic UI state is changed. Never invoke Play or start a game process.
    public static void RunAll(MainWindow window, Action<string, Action> check,
        Action<Window, string> capture, Action<TimeSpan> pump, Action<Func<bool>, string> wait)
    {
        var fields = new[] { "_pendingLaunch", "_startingGameId", "_settingsReady", "_savingMod", "_temporaryState" };
        var saved = fields.ToDictionary(name => name, name => Field(name).GetValue(window));
        try
        {
            foreach (var game in GameCatalog.All)
            {
                check($"only {game.Title} shows Starting through session and library refresh", () =>
                {
                    Set("_startingGameId", game.Id); Set("_pendingLaunch", true);
                    Refresh(); RequireLabels(game.Id);
                    var refreshing = (Task)Method("RefreshSessionsAsync").Invoke(window, null)!;
                    wait(() => refreshing.IsCompleted, "Session refresh did not finish.");
                    refreshing.GetAwaiter().GetResult(); RequireLabels(game.Id);
                    Method("RenderLibrary").Invoke(window, null);
                    pump(TimeSpan.FromMilliseconds(30)); RequireLabels(game.Id);
                    if (game.Id == "yugi") capture(window, "starting-yugi-only.png");
                    Set("_pendingLaunch", false); Set("_startingGameId", null); Refresh();
                    RequireIdle();
                });
            }
            check("settings saves and collection recovery disable Play without claiming games are starting", () =>
            {
                Set("_savingMod", true); Refresh(); RequireLabels(null);
                Set("_savingMod", false); Set("_settingsReady", false); Refresh(); RequireLabels(null);
                Set("_settingsReady", true);
                Set("_temporaryState", new TemporaryCollectionState(false, true, null, "Synthetic recovery"));
                Refresh(); RequireLabels(null);
                Set("_temporaryState", saved["_temporaryState"]); Refresh(); RequireIdle();
            });
            check("starting the selected game retains translated labels in all six launcher languages", () =>
            {
                try
                {
                    Set("_pendingLaunch", true); Set("_startingGameId", "kaiba"); Refresh();
                    foreach (var language in new[] { "en", "de", "fr", "it", "es", "ja" })
                    {
                        LauncherText.Current.SetLanguage(language);
                        pump(TimeSpan.FromMilliseconds(30)); RequireLabels("kaiba");
                    }
                }
                finally
                {
                    LauncherText.Current.SetLanguage("en");
                    Set("_pendingLaunch", false); Set("_startingGameId", null); Refresh();
                }
                RequireIdle();
            });
        }
        finally
        {
            foreach (var entry in saved) Set(entry.Key, entry.Value);
            Refresh();
        }

        void Set(string name, object? value) => Field(name).SetValue(window, value);
        void Refresh() { Method("RefreshModControls").Invoke(window, null); pump(TimeSpan.FromMilliseconds(30)); }
        Button[] Buttons() => window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is GameInstallation).ToArray();
        void RequireLabels(string? startingId)
        {
            var buttons = Buttons();
            Require(buttons.Length == 3, "Expected the three actual bound game buttons.");
            foreach (var button in buttons)
            {
                var installation = (GameInstallation)button.Tag!;
                var expected = LauncherText.Current.Translate(installation.GameId == startingId ? "Starting…" : "Play");
                Require(button.Content?.ToString() == expected, $"Wrong label for {installation.GameId}.");
                Require(!button.IsEnabled, "Another launch became available during the guarded operation.");
            }
        }
        void RequireIdle()
        {
            Require(Buttons().Length == 3 && Buttons().All(button => button.IsEnabled
                && button.Content?.ToString() == LauncherText.Current.Translate("Play")),
                "Finishing a startup left a stale label or blocked an idle game.");
        }
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing state " + name);
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing method " + name);
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
