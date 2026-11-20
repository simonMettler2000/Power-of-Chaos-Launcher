using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Decks;
using PowerOfChaos.Launcher.TestFixtures;
using PowerOfChaos.Launcher.Views;

namespace PowerOfChaos.Launcher.UiChecks;

/// <summary>The opponent deck window against an authored synthetic game archive. No game is involved.</summary>
internal static class OpponentDeckWindowChecks
{
    internal static void RunAll(string root, Action<string, Action> check, Action<Func<bool>, string> waitUntil, string picture)
    {
        var folder = Path.Combine(root, "game");
        Directory.CreateDirectory(folder);
        var game = new GameInstallation("yugi", Path.Combine(folder, "yugi_pc.exe"), Path.Combine(folder, "data.dat"), Path.Combine(folder, "Voice.dat"), folder);
        File.WriteAllBytes(game.DataPath, []);
        DeckFixtures.WriteArchive(game);
        var store = new OpponentDeckStore(Path.Combine(root, "data"));
        var main = DeckFixtures.MainCards("yugi");
        string Name(ushort card) => CollectionFixtures.CardName("en", CollectionFixtures.Slot(card));
        IReadOnlyList<OpponentDeck> Stored() => store.LoadAsync().GetAwaiter().GetResult();

        var window = new OpponentDecksWindow([game], "en", store)
        {
            AskName = (suggestion, taken) => Task.FromResult<string?>(taken.Contains("My deck") ? "My second deck" : "My deck"),
            ConfirmDelete = () => Task.FromResult(true)
        };
        T Named<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        Button? Action(string name) => window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => AutomationProperties.GetName(button) == name);
        void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            window.Show();
            var decks = Named<ListBox>("OpponentDeckList");
            var cards = Named<ListBox>("OpponentDeckCards");
            var browser = Named<ListBox>("OpponentDeckBrowser");
            var title = Named<TextBlock>("OpponentDeckTitle");
            var status = Named<TextBlock>("OpponentDeckStatus");
            var problems = Named<TextBlock>("OpponentDeckProblems");
            var save = Named<Button>("OpponentDeckSave");
            waitUntil(() => decks.ItemCount == 4 && cards.ItemCount > 0, "The game's decks were not listed.");

            check("Opponent decks lists the game's own decks read-only, with every card, its values and its text", () =>
            {
                Require(decks.Items.OfType<DeckListEntry>().Select(entry => entry.Title).SequenceEqual(new[] { "Your decks", "The game's own decks", "alpha", "BETA" }),
                    "The deck list is wrong.");
                Require((title.Text ?? "").Contains("alpha", StringComparison.Ordinal) && (title.Text ?? "").Contains("read-only", StringComparison.Ordinal)
                    && !save.IsVisible, "A game's own deck is not shown read-only.");
                var rows = cards.Items.OfType<DeckCardRow>().ToArray();
                Require(rows.Count(row => row.IsHeading) == 3 && rows.Where(row => !row.IsHeading).Sum(row => row.Count) == 42
                    && rows.First().Heading == "Main deck · 40 cards" && rows.Any(row => row.Heading == "Fusion deck · 2 cards")
                    && rows.All(row => row.IsHeading || !row.Editable), "The cards of the deck are listed wrongly.");
                Require(browser.ItemCount == 155 && Named<Button>("OpponentDeckCopy").IsEnabled && !Named<Button>("OpponentDeckRename").IsEnabled
                    && !Named<Button>("OpponentDeckDelete").IsEnabled, "The card list or the deck buttons are wrong.");
                cards.SelectedItem = rows.First(row => !row.IsHeading);
                Dispatcher.UIThread.RunJobs();
                var shown = rows.First(row => !row.IsHeading).Card!;
                Require(Named<TextBlock>("OpponentDeckCardName").Text == shown.Name && Named<SelectableTextBlock>("OpponentDeckCardText").Text == shown.Text
                    && shown.Text.StartsWith("Card text ", StringComparison.Ordinal), "Selecting a card does not show its text.");
                // Adding to a game's own deck is refused with a reason.
                Named<TextBox>("OpponentDeckSearch").Text = Name(main[100]);
                Dispatcher.UIThread.RunJobs();
                waitUntil(() => Action("Add one copy of " + Name(main[100])) is not null, "The searched card is not offered.");
                Click(Action("Add one copy of " + Name(main[100]))!);
                Require((status.Text ?? "").Contains("Copy this deck", StringComparison.Ordinal) && cards.Items.OfType<DeckCardRow>().Where(row => !row.IsHeading).Sum(row => row.Count) == 42,
                    "A game's own deck was changed.");
            });

            check("a copied deck can be changed with plus and minus, needs 40 main cards to be saved and is stored under its name", () =>
            {
                Click(Named<Button>("OpponentDeckCopy"));
                Require((title.Text ?? "").Contains("not saved yet", StringComparison.Ordinal) && save.IsVisible && save.IsEnabled && !decks.IsEnabled
                    && !Named<ComboBox>("OpponentDeckGames").IsEnabled, "A copied deck is not an unsaved deck of the user's.");
                var first = cards.Items.OfType<DeckCardRow>().First(row => !row.IsHeading).Card!;
                waitUntil(() => Action("Remove one copy of " + first.Name) is not null, "The copied deck has no minus buttons.");
                Click(Action("Remove one copy of " + first.Name)!);
                Require(!save.IsEnabled && problems.IsVisible && problems.Text == "The main deck needs at least 40 cards; it has 39."
                    && cards.Items.OfType<DeckCardRow>().First().Heading == "Main deck · 39 cards", "A 39-card deck can be saved.");
                var added = "Add one copy of " + Name(main[100]);
                Click(Action(added)!); Click(Action(added)!); Click(Action(added)!); Click(Action(added)!);
                var row = cards.Items.OfType<DeckCardRow>().Single(item => item.Card?.CardId == main[100]);
                Require(row.Count == 3 && row.CountText == "×3" && (status.Text ?? "").Contains("at most 3 copies", StringComparison.Ordinal)
                    && save.IsEnabled && !problems.IsVisible && cards.Items.OfType<DeckCardRow>().First().Heading == "Main deck · 42 cards",
                    "Adding copies is not limited to three or not counted.");
                // A Fusion monster goes to the Fusion deck, wherever it was asked to go.
                var fusion = DeckFixtures.FusionCards("yugi")[5];
                Named<CheckBox>("OpponentDeckToSide").IsChecked = true;
                Named<TextBox>("OpponentDeckSearch").Text = Name(fusion);
                Dispatcher.UIThread.RunJobs();
                waitUntil(() => Action("Add one copy of " + Name(fusion)) is not null, "The Fusion monster is not offered.");
                Click(Action("Add one copy of " + Name(fusion))!);
                Require(cards.Items.OfType<DeckCardRow>().Any(item => item.Heading == "Fusion deck · 3 cards")
                    && cards.Items.OfType<DeckCardRow>().Any(item => item.Heading == "Side deck · 0 cards"), "A Fusion monster went to the wrong part.");
                Click(save);
                waitUntil(() => Stored().Count == 1 && status.Text == "Deck saved.", "The deck was not saved.");
                var stored = Stored()[0];
                Require(stored is { Name: "My deck", GameId: "yugi", Enabled: true, Main.Count: 42, Fusion.Count: 3, Side.Count: 0 }
                    && stored.Main.Count(card => card == main[100]) == 3 && !stored.Main.Contains(first.CardId) == (first.CardId != main[100]),
                    "The stored deck is not the deck that was built.");
                Require(title.Text == "My deck" && decks.IsEnabled && decks.ItemCount == 5 && (decks.SelectedItem as DeckListEntry)?.Custom?.Name == "My deck"
                    && Named<Button>("OpponentDeckRename").IsEnabled && Named<Button>("OpponentDeckDelete").IsEnabled && !save.IsEnabled,
                    "The saved deck is not selected among the user's decks.");
            });
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(picture);

            check("the tick decides whether the opponent may use a deck, and changing a deck, a second deck and deleting are stored", () =>
            {
                var box = window.GetVisualDescendants().OfType<CheckBox>().Single(item => AutomationProperties.GetName(item) == "Let the opponent use My deck");
                Require(box.IsChecked == true, "A new deck is not in use.");
                box.IsChecked = false;
                box.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                waitUntil(() => Stored() is [{ Enabled: false }], "Unticking a deck was not stored.");
                // An existing deck is changed in place and saved without asking for a name again.
                waitUntil(() => cards.ItemCount > 0 && window.GetVisualDescendants().OfType<Button>().Any(button => (AutomationProperties.GetName(button) ?? "").StartsWith("Remove one copy of ", StringComparison.Ordinal)),
                    "The saved deck cannot be changed.");
                Click(window.GetVisualDescendants().OfType<Button>().First(button => (AutomationProperties.GetName(button) ?? "").StartsWith("Remove one copy of ", StringComparison.Ordinal)));
                Require(save.IsEnabled && !decks.IsEnabled, "A changed deck does not ask to be saved.");
                Click(save);
                waitUntil(() => Stored() is [{ Name: "My deck", Main.Count: 41 }], "A changed deck was not stored in place.");
                Click(Named<Button>("OpponentDeckCopy"));
                Click(save);
                waitUntil(() => Stored().Count == 2 && Stored()[1] is { Name: "My second deck", Enabled: true, Main.Count: 41 }, "A second deck was not stored under another name.");
                Click(Named<Button>("OpponentDeckDelete"));
                waitUntil(() => Stored() is [{ Name: "My deck" }], "Deleting a deck was not stored.");
                Click(Named<Button>("OpponentDeckNew"));
                Require((title.Text ?? "").Contains("not saved yet", StringComparison.Ordinal) && !save.IsEnabled && problems.Text == "The main deck needs at least 40 cards; it has 0.",
                    "An empty new deck can be saved.");
                Click(Named<Button>("OpponentDeckDiscard"));
                Require(decks.IsEnabled && !(title.Text ?? "").Contains("not saved yet", StringComparison.Ordinal) && Stored().Count == 1, "Discarding a new deck changed the stored decks.");
                Require(window.DecksChanged, "The window does not report that decks changed.");
            });

            check("closing with an unsaved deck asks first, and a deck that failed to save leaves no trace", () =>
            {
                var asked = 0;
                var discard = false;
                window.ConfirmDiscard = () => { asked++; return Task.FromResult(discard); };
                Click(Named<Button>("OpponentDeckCopy"));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                Require(asked == 1 && window.IsVisible && (title.Text ?? "").Contains("not saved yet", StringComparison.Ordinal), "An unsaved deck was dropped by closing the window.");
                // The deck file cannot be written: nothing is taken over, and the name stays free.
                var file = Path.Combine(root, "data", OpponentDeckStore.FileName);
                var before = File.ReadAllBytes(file);
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Click(save);
                    waitUntil(() => status.Text == "Your opponent decks could not be saved.", "A failed save was not reported.");
                }
                Require(File.ReadAllBytes(file).SequenceEqual(before) && (title.Text ?? "").Contains("not saved yet", StringComparison.Ordinal) && save.IsEnabled
                    && decks.Items.OfType<DeckListEntry>().Count(entry => entry.Custom is not null) == 1, "A deck that could not be saved was taken over.");
                Click(save);
                waitUntil(() => Stored().Count == 2, "The deck could not be saved after the failed attempt.");
                Require(Stored()[1].Name == "My second deck" && decks.Items.OfType<DeckListEntry>().Count(entry => entry.Custom is not null) == 2,
                    "The name of the failed attempt was not free any more.");
                // Without unsaved changes the window closes without asking.
                Require(asked == 1, "Saving asked about discarding.");
            });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
