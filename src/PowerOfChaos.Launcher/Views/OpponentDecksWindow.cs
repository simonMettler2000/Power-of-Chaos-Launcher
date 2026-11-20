using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PowerOfChaos.Launcher.Core;
using PowerOfChaos.Launcher.Core.Artwork;
using PowerOfChaos.Launcher.Core.Decks;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Views;

/// <summary>One line of the deck list: a heading, one of the user's decks or one of the game's own.</summary>
public sealed class DeckListEntry
{
    public string? Heading { get; init; }
    public OpponentDeck? Custom { get; init; }
    public StockOpponentDeck? Stock { get; init; }
    public string Title => Heading ?? Custom?.Name ?? Stock?.Name ?? "";
    public int Cards => Custom?.Main.Count ?? Stock?.Main.Count ?? 0;
}

/// <summary>One line of the open deck: a section heading, or a card with the number of copies in that section.</summary>
public sealed class DeckCardRow : INotifyPropertyChanged
{
    private int _count;
    private string _heading = "";

    public DeckCard? Card { get; init; }
    public DeckSection Section { get; init; }
    public Bitmap? Picture { get; init; }
    public string Detail { get; init; } = "";
    public bool Editable { get; init; }
    public bool IsHeading => Card is null;
    public string Name => Card?.Name ?? "";
    public string CountText => "×" + _count;

    public int Count
    {
        get => _count;
        set { _count = value; PropertyChanged?.Invoke(this, new(nameof(CountText))); }
    }

    public string Heading
    {
        get => _heading;
        set { _heading = value; PropertyChanged?.Invoke(this, new(nameof(Heading))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One card of the game in the list cards are added from.</summary>
public sealed class DeckBrowserRow
{
    public required DeckCard Card { get; init; }
    public Bitmap? Picture { get; init; }
    public string Detail { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>Shows the decks a game's opponent can use, lets the user build their own and choose which of them are in use.</summary>
public sealed class OpponentDecksWindow : Window
{
    private readonly IReadOnlyList<GameInstallation> _installations;
    private readonly string _language;
    private readonly OpponentDeckStore _store;
    private readonly Dictionary<int, Bitmap> _pictures = [];
    private readonly ObservableCollection<DeckCardRow> _rows = [];
    private List<OpponentDeck> _decks = [];
    private GameInstallation? _installation;
    private OpponentDeckCatalog? _catalog;
    private OpponentDeckDraft? _draft;
    private CancellationTokenSource? _loading;
    private string? _editingId;
    private string _suggestedName = "";
    private bool _readOnly = true;
    private bool _newDeck;
    private bool _selecting;
    private bool _busy;
    private bool _closeConfirmed;

    private readonly ComboBox _games = new() { MinWidth = 240, Name = "OpponentDeckGames" };
    private readonly ListBox _deckList = new() { Name = "OpponentDeckList" };
    private readonly ListBox _deckCards = new() { Name = "OpponentDeckCards" };
    private readonly ListBox _browser = new() { Name = "OpponentDeckBrowser" };
    private readonly TextBox _search = new() { Name = "OpponentDeckSearch" };
    private readonly ComboBox _filter = new() { Name = "OpponentDeckFilter", MinWidth = 150 };
    private readonly CheckBox _toSide = new() { Name = "OpponentDeckToSide" };
    private readonly TextBlock _deckTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Name = "OpponentDeckTitle" };
    private readonly TextBlock _problems = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Name = "OpponentDeckProblems" };
    private readonly TextBlock _status = new() { Classes = { "quiet" }, TextWrapping = TextWrapping.Wrap, FontSize = 12, Name = "OpponentDeckStatus" };
    private readonly Button _newButton = new() { Classes = { "secondary" }, Name = "OpponentDeckNew" };
    private readonly Button _copyButton = new() { Classes = { "secondary" }, Name = "OpponentDeckCopy" };
    private readonly Button _renameButton = new() { Classes = { "secondary" }, Name = "OpponentDeckRename" };
    private readonly Button _deleteButton = new() { Classes = { "secondary" }, Name = "OpponentDeckDelete" };
    private readonly Button _saveButton = new() { Classes = { "primary" }, Name = "OpponentDeckSave" };
    private readonly Button _discardButton = new() { Classes = { "secondary" }, Name = "OpponentDeckDiscard" };
    private readonly Image _cardPicture = new() { Width = 100, Height = 144, Stretch = Stretch.Uniform };
    private readonly TextBlock _cardName = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Name = "OpponentDeckCardName" };
    private readonly TextBlock _cardDetail = new() { Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _cardText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, Name = "OpponentDeckCardText" };
    private readonly Border _browserPanel;

    public OpponentDecksWindow(IReadOnlyList<GameInstallation> installations, string language, OpponentDeckStore store)
    {
        _installations = installations.DistinctBy(game => game.GameId).ToArray();
        _language = language;
        _store = store;
        Title = T("Opponent decks");
        Width = 1240; Height = 800; MinWidth = 1040; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        _games.ItemsSource = _installations.Select(game => GameCatalog.Get(game.GameId).Title).ToArray();
        _games.SelectionChanged += async (_, _) => await GameChosenAsync();
        _filter.ItemsSource = new[] { T("All cards"), T("Monsters"), T("Spell cards"), T("Trap cards"), T("Fusion monsters") };
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => FillBrowser();
        _search.Watermark = T("Search by card name");
        _search.TextChanged += (_, _) => FillBrowser();
        _toSide.Content = T("Add to the side deck");
        _newButton.Content = T("New deck");
        _copyButton.Content = T("Copy to my decks");
        _renameButton.Content = T("Rename");
        _deleteButton.Content = T("Delete");
        _saveButton.Content = T("Save deck");
        _discardButton.Content = T("Discard changes");
        _newButton.Click += (_, _) => BeginNewDeck([], [], [], T("New deck"));
        _copyButton.Click += (_, _) => { if (_draft is { } draft) BeginNewDeck(draft.Main, draft.Fusion, draft.Side, F("{0} copy", _deckTitle.Tag as string ?? "")); };
        _renameButton.Click += async (_, _) => await RenameAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _discardButton.Click += (_, _) => { _newDeck = false; SelectEntry(_deckList.SelectedItem as DeckListEntry, force: true); };

        // The framework also asks a template for a line when it empties a recycled container: that gets an empty one.
        _deckList.ItemTemplate = new FuncDataTemplate<DeckListEntry>((entry, _) => entry is null ? new Panel() : DeckListLine(entry), false);
        _deckList.SelectionChanged += (_, _) => { if (!_selecting) SelectEntry(_deckList.SelectedItem as DeckListEntry, force: false); };
        _deckCards.ItemsSource = _rows;
        _deckCards.ItemTemplate = new FuncDataTemplate<DeckCardRow>((row, _) => row is null ? new Panel() : DeckCardLine(row), false);
        _deckCards.SelectionChanged += (_, _) => { if (_deckCards.SelectedItem is DeckCardRow { Card: { } card }) ShowCard(card); };
        _browser.ItemTemplate = new FuncDataTemplate<DeckBrowserRow>((row, _) => row is null ? new Panel() : BrowserLine(row), false);
        _browser.SelectionChanged += (_, _) => { if (_browser.SelectedItem is DeckBrowserRow row) ShowCard(row.Card); };

        var close = new Button { Classes = { "secondary" }, Content = T("Close"), Name = "OpponentDeckClose" };
        close.Click += (_, _) => Close();
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 14 };
        top.Children.Add(new TextBlock { Text = T("Opponent decks"), Classes = { "sectionTitle" }, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(At(_games, 1));
        top.Children.Add(At(_status, 2));
        top.Children.Add(At(close, 3));
        _status.VerticalAlignment = VerticalAlignment.Center;

        var deckButtons = new WrapPanel { Children = { _newButton, _copyButton, _renameButton, _deleteButton } };
        foreach (var button in deckButtons.Children) button.Margin = new Thickness(0, 0, 8, 8);
        var decks = Panel(new DockPanel { Children =
        {
            Docked(new TextBlock { Text = T("Decks"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, Dock.Top),
            Docked(new TextBlock { Text = T("Tick the decks the opponent may use. With none ticked, the game's own decks are used."), Classes = { "quiet" },
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }, Dock.Top),
            Docked(deckButtons, Dock.Bottom),
            _deckList
        } });

        var deckActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _saveButton, _discardButton } };
        var deck = Panel(new DockPanel { Children =
        {
            Docked(_deckTitle, Dock.Top),
            Docked(_problems, Dock.Top),
            Docked(deckActions, Dock.Bottom),
            _deckCards
        } });
        _problems.Margin = new Thickness(0, 6, 0, 8);
        deckActions.Margin = new Thickness(0, 10, 0, 0);

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        filters.Children.Add(_search);
        filters.Children.Add(At(_filter, 1));
        _browserPanel = Panel(new DockPanel { Children =
        {
            Docked(new TextBlock { Text = T("Add cards"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, Dock.Top),
            Docked(filters, Dock.Top),
            Docked(_toSide, Dock.Bottom),
            _browser
        } });

        var cardWords = new StackPanel { Spacing = 6, Children = { _cardName, _cardDetail, new ScrollViewer { MaxHeight = 96, Content = _cardText } } };
        var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        cardGrid.Children.Add(_cardPicture);
        cardGrid.Children.Add(At(cardWords, 1));
        var cardPanel = Panel(cardGrid);
        _cardName.Text = T("Select a card to read its text.");

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*,380"), ColumnSpacing = 14 };
        columns.Children.Add(decks);
        columns.Children.Add(At(deck, 1));
        columns.Children.Add(At(_browserPanel, 2));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14, Margin = new Thickness(22) };
        root.Children.Add(top);
        Grid.SetRow(columns, 1); root.Children.Add(columns);
        Grid.SetRow(cardPanel, 2); root.Children.Add(cardPanel);
        Content = root;

        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += (_, _) => { _loading?.Cancel(); ClearPictures(); };
        UpdateState();
    }

    /// <summary>True once the user's decks were changed, so the caller refreshes what it shows about them.</summary>
    public bool DecksChanged { get; private set; }

    /// <summary>How a deck name is asked for (suggestion, names taken); the name dialog unless an automated check replaces it.</summary>
    public Func<string, IReadOnlySet<string>, Task<string?>>? AskName { get; set; }

    /// <summary>How deleting a deck is confirmed; the confirmation dialog unless an automated check replaces it.</summary>
    public Func<Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>How closing with unsaved changes is confirmed; the confirmation dialog unless an automated check replaces it.</summary>
    public Func<Task<bool>>? ConfirmDiscard { get; set; }

    /// <summary>A deck with unsaved changes is never dropped by closing the window without asking.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !Unsaved) return;
        e.Cancel = true;
        var discard = ConfirmDiscard is { } confirm ? await confirm()
            : await new ConfirmDialog("Discard unsaved changes?", "This deck has changes that were not saved.", "Discard changes").ShowDialog<bool>(this);
        if (!discard) return;
        _closeConfirmed = true;
        Close();
    }

    private static string T(string english) => LauncherText.Current.Translate(english);
    private static string F(string english, params object[] arguments) => LauncherText.Current.Format(english, arguments);
    private static T At<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T Docked<T>(T control, Dock dock) where T : Control { DockPanel.SetDock(control, dock); return control; }
    private static Border Panel(Control content) => new() { Classes = { "panel" }, Padding = new Thickness(14), Child = content };

    private async Task StartAsync()
    {
        try { _decks = (await _store.LoadAsync()).ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = T("Your opponent decks could not be read. Nothing was changed.");
            _games.IsEnabled = false;
            return;
        }
        if (_installations.Count == 0) { _status.Text = T("Add a game first."); return; }
        _games.SelectedIndex = 0;
    }

    private async Task GameChosenAsync()
    {
        if (_games.SelectedIndex < 0 || _games.SelectedIndex >= _installations.Count) return;
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        _installation = _installations[_games.SelectedIndex];
        _catalog = null; _draft = null; _rows.Clear(); _deckList.ItemsSource = null; _browser.ItemsSource = null;
        ClearPictures();
        _busy = true;
        _status.Text = T("Reading the game's cards…");
        UpdateState();
        try
        {
            var catalog = await new OpponentDeckCatalogReader().LoadAsync(_installation, _language, loading.Token);
            // The small pictures are a help, not a need: decks can be read and built without them.
            try
            {
                var art = await new NativeCardArtworkReader().LoadAsync(_installation, catalog.Cards.Select(card => card.Slot).ToArray(), loading.Token);
                if (loading.IsCancellationRequested) return;
                foreach (var image in art.Images) if (ToBitmap(image) is { } bitmap) _pictures[image.Slot] = bitmap;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
            if (loading.IsCancellationRequested) return;
            _catalog = catalog;
            _status.Text = "";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (loading.IsCancellationRequested) return;
            _status.Text = T("The cards of this game could not be read.");
        }
        finally { if (_loading == loading) _busy = false; }
        FillBrowser();
        FillDeckList(null);
    }

    private void FillDeckList(string? selectId)
    {
        var entries = new List<DeckListEntry>();
        if (_catalog is { } catalog)
        {
            entries.Add(new DeckListEntry { Heading = T("Your decks") });
            entries.AddRange(_decks.Where(deck => deck.GameId == catalog.GameId).Select(deck => new DeckListEntry { Custom = deck }));
            entries.Add(new DeckListEntry { Heading = T("The game's own decks") });
            entries.AddRange(catalog.StockDecks.Select(deck => new DeckListEntry { Stock = deck }));
        }
        _selecting = true;
        _deckList.ItemsSource = entries;
        var chosen = entries.FirstOrDefault(entry => selectId is not null && entry.Custom?.Id == selectId)
            ?? entries.FirstOrDefault(entry => entry.Custom is not null) ?? entries.FirstOrDefault(entry => entry.Stock is not null);
        _deckList.SelectedItem = chosen;
        _selecting = false;
        SelectEntry(chosen, force: true);
    }

    private void SelectEntry(DeckListEntry? entry, bool force)
    {
        if (!force && Unsaved) return;
        _newDeck = false;
        if (_catalog is null || entry is null || entry.Heading is not null)
        {
            _draft = null; _editingId = null; _readOnly = true;
            _rows.Clear();
            _deckTitle.Text = ""; _deckTitle.Tag = null;
            UpdateState();
            return;
        }
        _editingId = entry.Custom?.Id;
        _readOnly = entry.Custom is null;
        _draft = entry.Custom is { } custom ? new OpponentDeckDraft(_catalog.ById, custom.Main, custom.Fusion, custom.Side)
            : new OpponentDeckDraft(_catalog.ById, entry.Stock!.Main, entry.Stock.Fusion, entry.Stock.Side);
        _deckTitle.Tag = entry.Title;
        _deckTitle.Text = _readOnly ? F("{0} (the game's own deck, read-only)", entry.Title) : entry.Title;
        FillRows();
        UpdateState();
    }

    private void BeginNewDeck(IReadOnlyList<ushort> main, IReadOnlyList<ushort> fusion, IReadOnlyList<ushort> side, string suggestedName)
    {
        if (_catalog is null || Unsaved) return;
        _draft = new OpponentDeckDraft(_catalog.ById, main.ToArray(), fusion.ToArray(), side.ToArray());
        _editingId = null; _readOnly = false; _newDeck = true;
        _suggestedName = suggestedName;
        _deckTitle.Tag = suggestedName;
        _deckTitle.Text = F("{0} (not saved yet)", suggestedName);
        FillRows();
        UpdateState();
        _status.Text = T("Add or remove cards, then save the deck and give it a name.");
    }

    private bool Unsaved => !_readOnly && _draft is { } draft && (_newDeck || draft.Changed);

    private void FillRows()
    {
        _rows.Clear();
        if (_draft is not { } draft || _catalog is not { } catalog) return;
        foreach (var section in new[] { DeckSection.Main, DeckSection.Fusion, DeckSection.Side })
        {
            _rows.Add(new DeckCardRow { Section = section });
            foreach (var group in draft.Cards(section).GroupBy(card => card)
                .Select(group => (Card: catalog.ById.GetValueOrDefault(group.Key), Id: group.Key, Count: group.Count()))
                .OrderBy(item => item.Card is null ? 99 : (int)item.Card.Kind).ThenBy(item => item.Card?.Name ?? "", StringComparer.CurrentCultureIgnoreCase))
                _rows.Add(Row(group.Card ?? Unknown(group.Id), section, group.Count));
        }
        RefreshHeadings();
    }

    private DeckCardRow Row(DeckCard card, DeckSection section, int count) => new()
    {
        Card = card, Section = section, Count = count, Picture = _pictures.GetValueOrDefault(card.Slot), Detail = Describe(card), Editable = !_readOnly
    };

    private static DeckCard Unknown(ushort id) => new(0, id, "#" + id, "", DeckCardKind.NormalMonster, 0, 0, 0, false);

    private void RefreshHeadings()
    {
        if (_draft is not { } draft) return;
        foreach (var row in _rows.Where(row => row.IsHeading))
            row.Heading = row.Section switch
            {
                DeckSection.Main => F("Main deck · {0} cards", draft.Main.Count),
                DeckSection.Fusion => F("Fusion deck · {0} cards", draft.Fusion.Count),
                _ => F("Side deck · {0} cards", draft.Side.Count)
            };
    }

    private void AddCard(DeckCard card, DeckSection asked)
    {
        if (_busy) return;
        if (_draft is not { } draft || _readOnly) { _status.Text = T("Copy this deck to your decks to change it."); return; }
        var section = draft.SectionFor(card.CardId, asked);
        if (!draft.Add(card.CardId, asked))
        {
            _status.Text = draft.Copies(card.CardId) >= OpponentDeckRules.MaximumCopies
                ? F("A deck can hold at most {0} copies of {1}.", OpponentDeckRules.MaximumCopies, card.Name)
                : T("That part of the deck is full.");
            return;
        }
        _status.Text = "";
        if (_rows.FirstOrDefault(row => row.Card?.CardId == card.CardId && row.Section == section) is { } existing) existing.Count++;
        else
        {
            // After the last card of its section that sorts before it, or right under the section heading.
            var index = _rows.IndexOf(_rows.First(row => row.IsHeading && row.Section == section)) + 1;
            while (index < _rows.Count && !_rows[index].IsHeading && Before(_rows[index].Card!, card)) index++;
            _rows.Insert(index, Row(card, section, 1));
        }
        RefreshHeadings();
        UpdateState();
    }

    private static bool Before(DeckCard left, DeckCard right) => left.Kind != right.Kind ? left.Kind < right.Kind
        : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase) <= 0;

    private void RemoveCard(DeckCardRow row)
    {
        if (_busy || _draft is not { } draft || _readOnly || row.Card is not { } card || !draft.Remove(card.CardId, row.Section)) return;
        if (row.Count > 1) row.Count--; else _rows.Remove(row);
        _status.Text = "";
        RefreshHeadings();
        UpdateState();
    }

    private void FillBrowser()
    {
        if (_catalog is not { } catalog) { _browser.ItemsSource = null; return; }
        var words = (_search.Text ?? "").Trim();
        IEnumerable<DeckCard> cards = catalog.Cards;
        cards = _filter.SelectedIndex switch
        {
            1 => cards.Where(card => card.IsMonster && !card.IsFusion),
            2 => cards.Where(card => card.Kind == DeckCardKind.Spell),
            3 => cards.Where(card => card.Kind == DeckCardKind.Trap),
            4 => cards.Where(card => card.IsFusion),
            _ => cards
        };
        if (words.Length > 0) cards = cards.Where(card => card.Name.Contains(words, StringComparison.CurrentCultureIgnoreCase));
        var note = T("Not in any of the game's own decks");
        _browser.ItemsSource = cards.OrderBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(card => new DeckBrowserRow { Card = card, Picture = _pictures.GetValueOrDefault(card.Slot), Detail = Describe(card), Note = card.UsedByGame ? "" : note })
            .ToArray();
    }

    private void ShowCard(DeckCard card)
    {
        _cardPicture.Source = _pictures.GetValueOrDefault(card.Slot);
        _cardName.Text = card.Name;
        _cardDetail.Text = Describe(card);
        _cardText.Text = card.Text.Length > 0 ? card.Text : T("This game has no text for this card.");
    }

    private static string Describe(DeckCard card)
    {
        var kind = T(card.Kind switch
        {
            DeckCardKind.NormalMonster => "Normal monster",
            DeckCardKind.EffectMonster => "Effect monster",
            DeckCardKind.FusionMonster => "Fusion monster",
            DeckCardKind.RitualMonster => "Ritual monster",
            DeckCardKind.Spell => "Spell card",
            _ => "Trap card"
        });
        return card.IsMonster ? F("{0} · Level {1} · ATK {2} / DEF {3}", kind, card.Level, card.Attack, card.Defense) : kind;
    }

    private void UpdateState()
    {
        var ready = _catalog is not null && !_busy;
        var problems = _draft?.Problems ?? [];
        _problems.Text = _readOnly || _draft is null ? "" : string.Join("\n", problems.Select(problem => F(problem.Message, problem.Arguments.ToArray())));
        _problems.IsVisible = _problems.Text.Length > 0;
        var unsaved = Unsaved;
        _games.IsEnabled = !unsaved && !_busy && _installations.Count > 0;
        _deckList.IsEnabled = ready && !unsaved;
        _newButton.IsEnabled = ready && !unsaved;
        _copyButton.IsEnabled = ready && !unsaved && _draft is not null;
        _renameButton.IsEnabled = ready && !unsaved && _editingId is not null;
        _deleteButton.IsEnabled = ready && !unsaved && _editingId is not null;
        _saveButton.IsEnabled = ready && unsaved && problems.Count == 0;
        _discardButton.IsEnabled = ready && unsaved;
        _saveButton.IsVisible = _discardButton.IsVisible = !_readOnly && _draft is not null;
        // The cards can always be looked through and read; adding needs a deck of the user's own.
        _browserPanel.IsEnabled = ready;
        _toSide.IsEnabled = ready && !_readOnly && _draft is not null;
    }

    private async Task SaveAsync()
    {
        if (_busy || _draft is not { } draft || _catalog is not { } catalog || _readOnly || draft.Problems.Count > 0) return;
        var changed = _decks.ToList();
        string id;
        if (_editingId is { } existing && changed.FindIndex(deck => deck.Id == existing) is var index and >= 0)
        {
            changed[index] = changed[index] with { Main = draft.Main.ToArray(), Fusion = draft.Fusion.ToArray(), Side = draft.Side.ToArray() };
            id = existing;
        }
        else
        {
            if (changed.Count >= OpponentDeckStore.MaximumDecks)
            {
                _status.Text = F("You can keep at most {0} decks. Delete one first.", OpponentDeckStore.MaximumDecks);
                return;
            }
            var name = await AskNameAsync(_suggestedName, null);
            if (name is null) return;
            id = Guid.NewGuid().ToString("N");
            // A deck the user just made is in use straight away while the game has room for one more; the tick in the list turns it off.
            var inUse = changed.Count(deck => deck.GameId == catalog.GameId && deck.Enabled) < OpponentDeckRules.MaximumDecksInUse;
            changed.Add(new OpponentDeck(id, catalog.GameId, name, draft.Main.ToArray(), draft.Fusion.ToArray(), draft.Side.ToArray(), inUse));
        }
        if (!await StoreAsync(changed)) return;
        draft.MarkSaved();
        _newDeck = false;
        FillDeckList(id);
        _status.Text = T("Deck saved.");
    }

    private async Task RenameAsync()
    {
        var changed = _decks.ToList();
        if (_busy || _editingId is not { } id || changed.FindIndex(deck => deck.Id == id) is not (var index and >= 0)) return;
        var name = await AskNameAsync(changed[index].Name, id);
        if (name is null) return;
        changed[index] = changed[index] with { Name = name };
        if (await StoreAsync(changed)) FillDeckList(id);
    }

    private async Task DeleteAsync()
    {
        var changed = _decks.ToList();
        if (_busy || _editingId is not { } id || changed.FindIndex(deck => deck.Id == id) is not (var index and >= 0)) return;
        var confirmed = ConfirmDelete is { } confirm ? await confirm()
            : await new ConfirmDialog("Delete this deck?", "The deck is removed from the launcher. The game's own decks are not affected.", "Delete deck").ShowDialog<bool>(this);
        if (!confirmed) return;
        changed.RemoveAt(index);
        if (await StoreAsync(changed)) FillDeckList(null);
    }

    private async Task ToggleAsync(OpponentDeck deck, CheckBox box)
    {
        var changed = _decks.ToList();
        var index = changed.FindIndex(item => item.Id == deck.Id);
        if (_busy || index < 0 || _catalog is not { } catalog) { box.IsChecked = index >= 0 && changed[index].Enabled; return; }
        var enable = box.IsChecked == true;
        if (enable && OpponentDeckRules.Check(changed[index].Main, changed[index].Fusion, changed[index].Side, catalog.ById).Count > 0)
        {
            box.IsChecked = false;
            _status.Text = T("This deck cannot be used as it is. Open it and correct what is listed above its cards.");
            return;
        }
        if (enable && changed.Count(item => item.GameId == deck.GameId && item.Enabled) >= OpponentDeckRules.MaximumDecksInUse)
        {
            box.IsChecked = false;
            _status.Text = F("At most {0} decks can be in use for one game.", OpponentDeckRules.MaximumDecksInUse);
            return;
        }
        changed[index] = changed[index] with { Enabled = enable };
        if (await StoreAsync(changed)) FillDeckList(_editingId ?? deck.Id);
        else box.IsChecked = !enable;
    }

    /// <summary>Writes a changed list of decks. The window takes the list over only once it is on disk, so a failed write changes nothing.</summary>
    private async Task<bool> StoreAsync(List<OpponentDeck> changed)
    {
        _busy = true;
        UpdateState();
        try
        {
            await _store.SaveAsync(changed);
            _decks = changed;
            DecksChanged = true;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = T("Your opponent decks could not be saved.");
            return false;
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async Task<string?> AskNameAsync(string suggestion, string? ownId)
    {
        var gameId = _catalog?.GameId;
        var taken = _decks.Where(deck => deck.GameId == gameId && deck.Id != ownId).Select(deck => deck.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return AskName is { } ask ? await ask(suggestion, taken) : await new DeckNameDialog(suggestion, taken).ShowDialog<string?>(this);
    }

    private Control DeckListLine(DeckListEntry entry)
    {
        if (entry.Heading is { } heading)
            return new TextBlock { Text = heading, Classes = { "quiet" }, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        if (entry.Custom is { } deck)
        {
            var box = new CheckBox { IsChecked = deck.Enabled, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
            AutomationProperties.SetName(box, F("Let the opponent use {0}", deck.Name));
            ToolTip.SetTip(box, T("The opponent may use this deck"));
            box.Click += async (_, _) => await ToggleAsync(deck, box);
            line.Children.Add(box);
        }
        line.Children.Add(At(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 1));
        line.Children.Add(At(new TextBlock { Text = entry.Cards.ToString(LauncherText.Current.Culture), Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, 2));
        return line;
    }

    private Control DeckCardLine(DeckCardRow row)
    {
        if (row.IsHeading)
        {
            var heading = new TextBlock { Classes = { "quiet" }, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
            heading.Bind(TextBlock.TextProperty, new Binding(nameof(DeckCardRow.Heading)) { Source = row });
            return heading;
        }
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        line.Children.Add(Picture(row.Picture));
        line.Children.Add(At(Words(row.Name, row.Detail, ""), 1));
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, MinWidth = 26 };
        count.Bind(TextBlock.TextProperty, new Binding(nameof(DeckCardRow.CountText)) { Source = row });
        line.Children.Add(At(count, 2));
        if (row.Editable && row.Card is { } card)
        {
            line.Children.Add(At(Small("−", F("Remove one copy of {0}", card.Name), () => RemoveCard(row)), 3));
            line.Children.Add(At(Small("+", F("Add one copy of {0}", card.Name), () => AddCard(card, row.Section)), 4));
        }
        if (row.Card is { } shown) line.Children.Add(At(Small("i", F("Show the text of {0}", shown.Name), () => ShowCard(shown)), 5));
        return line;
    }

    private Control BrowserLine(DeckBrowserRow row)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        line.Children.Add(Picture(row.Picture));
        line.Children.Add(At(Words(row.Card.Name, row.Detail, row.Note), 1));
        line.Children.Add(At(Small("+", F("Add one copy of {0}", row.Card.Name),
            () => AddCard(row.Card, _toSide.IsChecked == true ? DeckSection.Side : DeckSection.Main)), 2));
        line.Children.Add(At(Small("i", F("Show the text of {0}", row.Card.Name), () => ShowCard(row.Card)), 3));
        return line;
    }

    private static Image Picture(Bitmap? picture)
    {
        var image = new Image { Source = picture, Width = 34, Height = 49, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }

    private static StackPanel Words(string name, string detail, string note)
    {
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        words.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = detail, Classes = { "quiet" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        if (note.Length > 0) words.Children.Add(new TextBlock { Text = note, Classes = { "quiet" }, FontSize = 11, FontStyle = FontStyle.Italic });
        return words;
    }

    private static Button Small(string text, string name, Action action)
    {
        var button = new Button
        {
            Content = text, Classes = { "secondary" }, MinWidth = 30, Width = 30, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private static WriteableBitmap? ToBitmap(NativeCardImage image)
    {
        if (image.Width is < 1 or > 64 || image.Height is < 1 or > 96 || image.BgraPixels is null
            || image.BgraPixels.Length != image.Width * image.Height * 4) return null;
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = bitmap.Lock();
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(image.BgraPixels, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void ClearPictures()
    {
        _cardPicture.Source = null;
        foreach (var picture in _pictures.Values) picture.Dispose();
        _pictures.Clear();
    }
}

/// <summary>Asks for the name of a deck. Returns null when the user cancels.</summary>
public sealed class DeckNameDialog : Window
{
    public DeckNameDialog(string suggestion, IReadOnlySet<string> taken)
    {
        var text = LauncherText.Current;
        Title = text.Translate("Name of the deck");
        Width = 460; Height = 250; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var name = new TextBox { Text = OpponentDeckRules.CleanName(suggestion) ?? "", MaxLength = OpponentDeckRules.MaximumNameLength, Name = "DeckName" };
        var message = new TextBlock { Classes = { "quiet" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Name = "DeckNameMessage" };
        var save = new Button { Classes = { "primary" }, Content = text.Translate("Save deck"), IsDefault = true, Name = "DeckNameSave" };
        var cancel = new Button { Classes = { "secondary" }, Content = text.Translate("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) =>
        {
            var cleaned = OpponentDeckRules.CleanName(name.Text);
            if (cleaned is null) { message.Text = text.Format("Give the deck a name of up to {0} characters.", OpponentDeckRules.MaximumNameLength); return; }
            if (taken.Contains(cleaned)) { message.Text = text.Translate("Another deck of this game already has that name."); return; }
            Close(cleaned);
        };
        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 16,
            Children =
            {
                new TextBlock { Text = text.Translate("Name of the deck"), FontSize = 21, FontWeight = FontWeight.SemiBold },
                name, message,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, save } }
            }
        };
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
