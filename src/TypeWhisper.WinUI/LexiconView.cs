using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace TypeWhisper.WinUI;

public sealed partial class LexiconView : UserControl
{
    // Dispatcher-owned state; shutdown closes admission before waiting for native pickers.
    private bool _closing;
    private TaskCompletionSource? _transferCompletion;
    private Action? _cancelPicker;
    internal Task ShutdownAsync()
    {
        _closing = true;
        IsEnabled = false;
        _trainingDialog?.Hide();
        _aliasCancellation?.Cancel();
        _aliasDialog?.Hide();
        _editorDialog?.Hide();
        _appImportFlow?.Cancel();
        try { _cancelPicker?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Lexicon picker cancellation failed: " + ex); }
        return Task.WhenAll(_transferCompletion?.Task ?? Task.CompletedTask, _trainingTask ?? Task.CompletedTask,
            _aliasTask ?? Task.CompletedTask);
    }

    private readonly Lexicon _store = new(DictationDictionarySnapshot.StoragePath, DictationSnippetSnapshot.StoragePath);
    private bool _showPacks;
    private readonly TabBar _tabs = new();
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private StackPanel? _editor;
    private ContentDialog? _editorDialog;
    private TextBox? _firstInput;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _heading = Text(Loc.T("Dictionary"), 22);
    private readonly HandCursorButton _headingHelp = SettingsHelp.Button(Loc.T("Dictionary"), Loc.T("Dictionary and snippets are saved in this profile."));
    private readonly TextBlock _notice = Text(Loc.T("Dictionary and snippets are saved in this profile."), 11, true);
    private readonly TextBlock _count = Text("", 11, true);
    private readonly ScrollViewer _scroll;
    private LexiconKind _kind;
    private LexiconEntry? _original;
    private LexiconEntry? _draft;
    private string _query = "";
    private readonly HashSet<string> _expandedCorrections = new(StringComparer.Ordinal);
    internal event Action? ExitRequested;

    public LexiconView()
    {
        CorrectionLearning.DictionaryChanged += () => { _store.ReloadDictionary(); if (_draft is null && !_closing) Render(); };
        var root = new Grid { Background = Brush("InkBrush"), Padding = new Thickness(8, 0, 8, 0), RowSpacing = 12 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(_heading, AutomationHeadingLevel.Level1);
        var header = new StackPanel { Spacing = 12 };
        _heading.FontSize = 24; _heading.MinHeight = 32; _heading.Margin = new Thickness(4, 0, 0, 0);
        // Settings list Dictionary and Snippets as separate pages, so only the dictionary has tabs.
        _tabs.SetItems([new("Word", Loc.T("Words")), new("Correction", Loc.T("Corrections")), new("packs", Loc.T("Term packs"))], "Word");
        _tabs.SelectionChanged += id =>
        {
            _showPacks = id == "packs";
            if (!_showPacks) { _kind = Enum.Parse<LexiconKind>(id); _query = ""; }
            Render(); _scroll!.ChangeView(null, 0, null, true);
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(_heading); titleRow.Children.Add(_headingHelp);
        header.Children.Add(_tabs); header.Children.Add(titleRow); root.Children.Add(header);
        _scroll = new ScrollViewer { Content = _body, Padding = new Thickness(0, 0, 20, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite); Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        var footer = new Grid { MinHeight = 52, ColumnSpacing = 10 }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(_actions, 1); footer.Children.Add(_actions);
        var border = new Border { Child = footer, BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(border, 3); root.Children.Add(border); Content = root;
        EntryActionMenu.Attach(this, () => EntryActionMenu.FromButtons(_actions));
    }

    internal void Present(bool snippets, string? section = null)
    {
        _store.ReloadDictionary();
        _store.ReloadSnippets();
        _kind = section == "corrections" ? LexiconKind.Correction : snippets ? LexiconKind.Snippet : LexiconKind.Word;
        _showPacks = section == "packs";
        _query = ""; Render(); _scroll.ChangeView(null, 0, null, true);
    }

    internal void GoBack() => ExitRequested?.Invoke();

    private string Section => _kind switch { LexiconKind.Word => Loc.T("Words"), LexiconKind.Correction => Loc.T("Corrections"), _ => Loc.T("Snippets") };
    private string Icon => _kind == LexiconKind.Snippet ? "text" : "dictionary";

    private void Render()
    {
        _body.Children.Clear(); _rows.Children.Clear();
        _tabs.Visibility = _kind == LexiconKind.Snippet && !_showPacks ? Visibility.Collapsed : Visibility.Visible;
        RenderTabs();
        if (_showPacks) { RenderPacks(); return; }
        _heading.Text = _kind == LexiconKind.Snippet ? Loc.T("Snippets") : Loc.T("Dictionary");
        _notice.Text = _store.LastError ?? "";
        AutomationProperties.SetName(_headingHelp, Loc.T("About {0}", Section));
        RenderList();
        RenderActions();
    }

    private LexiconEntry? Stored(LexiconEntry? entry) => entry is null ? null : _store.Entries.FirstOrDefault(current => current.Id == entry.Id);

    private void RenderTabs() => _tabs.SetSelected(_showPacks ? "packs" : _kind.ToString());

    private void RenderList()
    {
        SettingsHelp.Update(_headingHelp, _kind switch
        {
            LexiconKind.Word => Loc.T("Names and specialist terms you want TypeWhisper to recognize."),
            LexiconKind.Correction => Loc.T("Replace commonly misheard phrases with the spelling you prefer."),
            _ => Loc.T("Turn a short spoken phrase into a reusable block of text.")
        });
        var searchLabel = _kind switch { LexiconKind.Word => Loc.T("Search words"), LexiconKind.Correction => Loc.T("Search corrections"), _ => Loc.T("Search snippets") };
        var search = Input(_query, searchLabel, false);
        var searchGrid = new Grid { ColumnSpacing = 8 }; searchGrid.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); searchGrid.ColumnDefinitions.Add(new());
        searchGrid.Children.Add(new TypeWhisperGlyph { Kind = "search", Width = 18, Height = 18 });
        var placeholder = Text(searchLabel + "…", 14, true); placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(12, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(search, 1); Grid.SetColumn(placeholder, 1); searchGrid.Children.Add(search); searchGrid.Children.Add(placeholder);
        void SearchChanged() { _query = search.Text; placeholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RenderRows(); }
        search.TextChanged += (_, _) => SearchChanged();
        var searchBox = Surface(searchGrid, 2); searchBox.Padding = new Thickness(12, 2, 4, 2);
        _body.Children.Add(searchBox); _count.Margin = new Thickness(4, 0, 0, 0); _body.Children.Add(_count); _body.Children.Add(_rows); SearchChanged();
    }

    private void RenderRows()
    {
        _rows.Children.Clear(); var entries = _store.Search(_kind, _query).ToArray();
        var total = _store.Entries.Count(entry => entry.Kind == _kind);
        _count.Text = _kind switch { LexiconKind.Word => Loc.T("{0} of {1} words", entries.Length, total), LexiconKind.Correction => Loc.T("{0} of {1} corrections", entries.Length, total), _ => Loc.T("{0} of {1} snippets", entries.Length, total) };
        if (entries.Length == 0)
        {
            var empty = new StackPanel { Spacing = 10, Padding = new Thickness(16, 28, 16, 28) };
            empty.Children.Add(new TypeWhisperGlyph { Kind = _query.Length == 0 ? Icon : "search", Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center });
            var title = Text(_query.Length == 0 ? _kind switch { LexiconKind.Word => Loc.T("Your first word starts here"), LexiconKind.Correction => Loc.T("Your first correction starts here"), _ => Loc.T("Your first snippet starts here") } : Loc.T("No matching entries"), 16); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.TextAlignment = TextAlignment.Center; empty.Children.Add(title);
            var hint = Text(_query.Length == 0 ? Loc.T("Add a term or phrase with the button below.") : Loc.T("Try a different word, phrase, or tag."), 12, true); hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            var emptyCard = ListCard(); emptyCard.Add(empty);
            _rows.Children.Add(emptyCard);
        }
        if (entries.Length == 0) return;
        if (_kind == LexiconKind.Correction) { RenderCorrectionGroups(entries); return; }
        var card = ListCard();
        foreach (var entry in entries)
        {
            var current = entry;
            var corners = RowCorners(ReferenceEquals(entry, entries[0]), ReferenceEquals(entry, entries[^1]));
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var title = Text(entry.Key, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; labels.Children.Add(title);
            if (_kind == LexiconKind.Snippet)
            {
                var description = Text(entry.Value.Replace('\n', ' '), 12, true);
                description.MaxLines = 1; description.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(description);
            }
            content.Children.Add(labels);
            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (var tag in entry.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) badges.Children.Add(Badge(tag));
            if (entry.Kind == LexiconKind.Snippet && entry.UsageCount > 0) badges.Children.Add(Badge(entry.UsageCount == 1 ? Loc.T("Used 1 time") : Loc.T("Used {0} times", entry.UsageCount)));
            if (entry.FromPack) badges.Children.Add(Badge(Loc.T("Term pack"), true));
            // Entries switched off earlier stay recognizable; the context menu turns them on again.
            if (!entry.Enabled) badges.Children.Add(Badge(Loc.T("Off")));
            Grid.SetColumn(badges, 1); content.Children.Add(badges);
            var open = Button("", () => { if (current.FromPack) { _showPacks = true; Render(); } else OpenEditor(current); });
            open.ContextFlyout = LexiconEntryMenu(entry); open.Content = content;
            open.HorizontalContentAlignment = HorizontalAlignment.Stretch; open.HorizontalAlignment = HorizontalAlignment.Stretch;
            open.MinHeight = 44; open.Padding = new Thickness(18, 8, 18, 8);
            open.Style = (Style)Application.Current.Resources["MenuButtonStyle"];
            open.CornerRadius = corners;
            AutomationProperties.SetName(open, entry.FromPack ? Loc.T("Manage term pack for {0}", entry.Key) : _kind == LexiconKind.Snippet ? Loc.T("Edit snippet: {0}", entry.Key) : Loc.T("Edit word: {0}", entry.Key));
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(open);
            // A term pack is switched on its own page.
            if (!entry.FromPack)
            {
                var delete = DeleteButton(entry);
                Grid.SetColumn(delete, 1); row.Children.Add(delete);
                FillRow(row, open, delete, new Thickness(18, 8, 62, 8));
            }
            card.Add(row);
        }
        _rows.Children.Add(card);
    }

    private HandCursorButton DeleteButton(LexiconEntry entry)
    {
        var button = new HandCursorButton { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32,
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["IconButtonStyle"] };
        AutomationProperties.SetName(button, Loc.T("Delete entry") + ": " + entry.Key);
        ToolTipService.SetToolTip(button, Loc.T("Delete"));
        button.Click += (_, _) => DeleteEntry(entry);
        return button;
    }

    private async void DeleteEntry(LexiconEntry entry)
    {
        if (_closing) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = Loc.T("Delete this entry?"), Content = entry.Key,
            PrimaryButtonText = Loc.T("Delete entry"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close, PrimaryButtonStyle = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DestructiveConfirmButtonStyle"]
        };
        try { if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing) return; }
        // Another dialog is still open.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return; }
        if (!_store.Remove(entry.Id)) { _notice.Text = _store.LastError ?? Loc.T("Could not delete entry."); return; }
        Render();
    }

    // The button fills the row under the control on its right, so the hover covers the whole row.
    // Over that control the row draws the same shade itself.
    private static void FillRow(Grid row, HandCursorButton main, FrameworkElement trailing, Thickness padding)
    {
        Grid.SetColumnSpan(main, 2);
        main.Padding = padding;
        row.CornerRadius = main.CornerRadius;
        var hover = new SolidColorBrush(((SolidColorBrush)Brush("StateOverlayBrush")).Color) { Opacity = 0.09 };
        trailing.PointerEntered += (_, _) => row.Background = hover;
        trailing.PointerExited += (_, _) => row.Background = null;
        trailing.PointerCaptureLost += (_, _) => row.Background = null;
    }

    // One card for the whole list; rows fill it from edge to edge so the hover covers a row.
    private static SettingsCard ListCard() => new() { Padding = new Thickness(0) };

    // The hover is square like the row, and follows the card's corners on its first and last row.
    private static CornerRadius RowCorners(bool first, bool last) => new(first ? 11 : 0, first ? 11 : 0, last ? 11 : 0, last ? 11 : 0);

    private static Border Badge(string text, bool accent = false)
    {
        var label = Text(text, 11, !accent); label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        if (accent) label.Foreground = Brush("AccentBrush");
        return new Border { Child = label, Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(6), Background = Brush("ElevatedBrush"), VerticalAlignment = VerticalAlignment.Center };
    }

    private void RenderCorrectionGroups(LexiconEntry[] matches)
    {
        var matchingTargets = matches.Select(entry => entry.Value).ToHashSet(StringComparer.Ordinal);
        var groups = _store.Entries.Where(entry => entry.Kind == LexiconKind.Correction && matchingTargets.Contains(entry.Value))
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        _count.Text = groups.Length == 1 ? Loc.T("{0} spelling · {1} variants", groups.Length, groups.Sum(group => group.Count()))
            : Loc.T("{0} spellings · {1} variants", groups.Length, groups.Sum(group => group.Count()));
        var groupCard = ListCard();
        _rows.Children.Add(groupCard);
        foreach (var group in groups)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var title = Text(group.Key, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            header.Children.Add(title);
            header.Children.Add(Badge(group.Count() == 1 ? Loc.T("1 variant") : Loc.T("{0} variants", group.Count())));
            // Variants span the card like the rows above them; only their text is indented.
            var aliases = new StackPanel();
            var ordered = group.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var alias in ordered)
            {
                var current = alias;
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var edit = Button("", () => OpenEditor(current));
                EntryActionMenu.Attach(edit, () => [new(Loc.T("Edit variant"), () => OpenEditor(current)),
                    // A variant switched off earlier has no other way back.
                    .. current.Enabled ? [] : new EntryActionMenu.Action[] { new(Loc.T("Enable"), () =>
                    {
                        var error = _store.Save(current with { Enabled = true });
                        _notice.Text = error ?? "";
                        if (error is null) Render();
                    }) },
                    new(Loc.T("Delete variant…"), () => DeleteEntry(current))]);
                var label = Text(alias.Enabled ? alias.Key : alias.Key + "  ·  " + Loc.T("Off"), 13, !alias.Enabled);
                label.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
                edit.Content = label;
                edit.Style = (Style)Application.Current.Resources["MenuButtonStyle"];
                edit.CornerRadius = RowCorners(false, ReferenceEquals(group, groups[^1]) && ReferenceEquals(alias, ordered[^1]));
                edit.MinHeight = 38;
                edit.HorizontalAlignment = HorizontalAlignment.Stretch;
                edit.HorizontalContentAlignment = HorizontalAlignment.Left;
                AutomationProperties.SetName(edit, Loc.T("Edit variant {0} for {1}", alias.Key, group.Key));
                ToolTipService.SetToolTip(edit, Loc.T("Edit variant"));
                row.Children.Add(edit);
                var remove = DeleteButton(alias);
                Grid.SetColumn(remove, 1); row.Children.Add(remove); aliases.Children.Add(row);
                FillRow(row, edit, remove, new Thickness(40, 7, 62, 7));
            }
            var panel = new StackPanel();
            var headingRow = new Grid { ColumnSpacing = 12 };
            headingRow.ColumnDefinitions.Add(new());
            headingRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var chevron = Text("", 14, true); chevron.Width = 12; chevron.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Insert(0, chevron);
            HandCursorButton? expand = null;
            void UpdateExpansion()
            {
                var expanded = _expandedCorrections.Contains(group.Key);
                aliases.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = expanded ? "⌄" : "›";
                headingRow.CornerRadius = expand!.CornerRadius = RowCorners(ReferenceEquals(group, groups[0]), ReferenceEquals(group, groups[^1]) && !expanded);
            }
            expand = Button("", () =>
            {
                if (!_expandedCorrections.Add(group.Key)) _expandedCorrections.Remove(group.Key);
                UpdateExpansion();
            });
            expand.Content = header;
            expand.Style = (Style)Application.Current.Resources["MenuButtonStyle"];
            expand.HorizontalAlignment = HorizontalAlignment.Stretch;
            expand.HorizontalContentAlignment = HorizontalAlignment.Left;
            expand.MinHeight = 44;
            expand.Padding = new Thickness(18, 8, 18, 8);
            AutomationProperties.SetName(expand, Loc.T("Expand or collapse corrections for {0}", group.Key));
            headingRow.Children.Add(expand);
            var add = Button("+", () => OpenEditor(new LexiconEntry(Guid.NewGuid(), LexiconKind.Correction, "") with { Value = group.Key }));
            add.VerticalAlignment = VerticalAlignment.Center;
            add.Width = 32;
            add.Height = add.MinHeight = 32;
            add.MinWidth = 32;
            add.Padding = new Thickness(4);
            AutomationProperties.SetName(add, Loc.T("Add variant for {0}", group.Key));
            ToolTipService.SetToolTip(add, Loc.T("Add variant"));
            add.Margin = new Thickness(0, 0, 18, 0);
            Grid.SetColumn(add, 1); headingRow.Children.Add(add);
            FillRow(headingRow, expand, add, new Thickness(18, 8, 62, 8));
            panel.Children.Add(headingRow);
            panel.Children.Add(aliases);
            UpdateExpansion();
            var card = SettingsRow.Host(panel);
            MenuFlyout GroupMenu()
            {
                var menu = new MenuFlyout();
                var suggest = new MenuFlyoutItem { Text = Loc.T("Suggest aliases…") };
                suggest.Click += (_, _) => StartAliasSuggestions(group.Key);
                menu.Items.Add(suggest);
                var delete = new MenuFlyoutItem { Text = Loc.T("Delete group…") };
                delete.Click += async (_, _) =>
                {
                    if (_closing) return;
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                        Title = Loc.T("Delete corrections for {0}?", group.Key),
                        Content = Loc.T("This deletes all {0} variants in this group. This cannot be undone.", group.Count()),
                        PrimaryButtonText = Loc.T("Delete group"), CloseButtonText = Loc.T("Cancel"),
                        DefaultButton = ContentDialogButton.Close, PrimaryButtonStyle = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DestructiveConfirmButtonStyle"]
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing) return;
                    if (!_store.RemoveCorrectionGroup(group.Key))
                    { _notice.Text = _store.LastError ?? Loc.T("Could not delete correction group."); return; }
                    _expandedCorrections.Remove(group.Key);
                    Render();
                    _notice.Text = Loc.T("Correction group deleted.");
                };
                menu.Items.Add(delete);
                return menu;
            }
            card.ContextFlyout = GroupMenu();
            expand.ContextFlyout = GroupMenu();
            groupCard.Children.Add(card);
        }
    }

    private MenuFlyout LexiconEntryMenu(LexiconEntry entry)
    {
        if (entry.FromPack)
            return EntryActionMenu.Create([new(Loc.T("Show term pack"), () => { _showPacks = true; Render(); })]);
        return EntryActionMenu.Create([
            new(Loc.T("Edit"), () => OpenEditor(entry)),
            .. entry.Kind == LexiconKind.Word ? new EntryActionMenu.Action[] { new(Loc.T("Suggest aliases…"), () => StartAliasSuggestions(entry.Key)) } : [],
            .. entry.Enabled ? [] : new EntryActionMenu.Action[] { new(Loc.T("Enable"), () =>
            {
                var error = _store.Save(entry with { Enabled = true });
                _notice.Text = error ?? "";
                if (error is null) Render();
            }) },
            new(Loc.T("Delete…"), () => DeleteEntry(entry))
        ]);
    }

    // Editing happens in a dialog, so the list stays as it is behind it.
    private async void OpenEditor(LexiconEntry entry)
    {
        if (_closing || _editorDialog is not null) return;
        _apiEditorBaseline = Stored(entry);
        _original = _draft = entry;
        _firstInput = null;
        _editor = new StackPanel { Spacing = 14, MinWidth = 420 };
        RenderEditor();
        var error = Text("", 12, true); error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        _editor.Children.Add(error);
        var dialog = _editorDialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Content = _editor,
            Title = _apiEditorBaseline is not null
                ? _kind switch { LexiconKind.Word => Loc.T("Edit word"), LexiconKind.Correction => Loc.T("Edit correction"), _ => Loc.T("Edit snippet") }
                : _kind switch { LexiconKind.Word => Loc.T("New word"), LexiconKind.Correction => Loc.T("New correction"), _ => Loc.T("New snippet") },
            PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        // A failed save keeps the dialog and its text open.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = CanSaveApiEditor() ? _store.Save(_draft!) : _notice.Text;
            if (message is null) return;
            args.Cancel = true;
            error.Text = message; error.Visibility = Visibility.Visible;
        };
        dialog.Opened += (_, _) => _firstInput?.Focus(FocusState.Programmatic);
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another dialog is still open; this one did not show.
        }
        finally { _editorDialog = null; _draft = _original = null; }
        if (!_closing) Render();
    }

    private void RenderEditor()
    {
        if (_kind == LexiconKind.Correction)
        {
            AddField(Loc.T("Correct spelling"), _draft!.Value, value => _draft = _draft! with { Value = value }, 10000, help:
                Loc.T("Type \\n for a line break, \\n\\n for a new paragraph, \\t for a tab or \\s for a space, e.g. map \"new line\" to \\n. Use \\\\ for a literal backslash. English and German built-in commands such as \"new line\" are available by choosing Replace spoken commands in Dictation settings."));
            AddField(Loc.T("Recognized as"), _draft.Key, value => _draft = _draft! with { Key = value }, 160);
        }
        else
        {
            AddField(_kind == LexiconKind.Word ? Loc.T("Word or phrase") : Loc.T("Spoken trigger"), _draft!.Key, value => _draft = _draft! with { Key = value }, 160);
            if (_kind == LexiconKind.Snippet)
                AddField(Loc.T("Insert this text"), _draft.Value, value => _draft = _draft! with { Value = value }, 10000, true, Loc.T("Use {date}, {time}, {datetime}, {day}, {year}, or a format such as {date:dd.MM.yyyy}. {clipboard} inserts clipboard text when the spoken trigger matches."));
        }
        if (_kind == LexiconKind.Snippet)
        {
            AddField(Loc.T("Tags · optional, separated by commas"), _draft.Tags, value => _draft = _draft! with { Tags = value }, 300);
        }
        if (_kind == LexiconKind.Word) AddBoostingOptions();
        if (_kind != LexiconKind.Word)
            AddToggle(Loc.T("Match capitalization"), Loc.T("Only match the trigger with this exact capitalization."), _draft.CaseSensitive, value => _draft = _draft! with { CaseSensitive = value });
    }

    private void AddBoostingOptions()
    {
        var picker = new ChoicePicker { Width = 220 };
        picker.Configure(Loc.T("Boosting"), "signal", Loc.T("Boosting"));
        var slider = new Slider { Minimum = 40, Maximum = 95, StepFrequency = 1, Value = (_draft!.CtcMinSimilarity ?? .65f) * 100 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, Loc.T("Minimum CTC similarity in percent"));
        var valueLabel = Text("", 12, true);
        var advanced = new StackPanel { Spacing = 4 }; advanced.Children.Add(slider); advanced.Children.Add(valueLabel);
        var choices = new (string Name, float? Value)[] { (Loc.T("Auto"), null), (Loc.T("Strong"), .5f), (Loc.T("Balanced"), .65f), (Loc.T("Precise"), .8f), (Loc.T("Advanced"), null) };
        var selected = _draft.CtcMinSimilarity is null ? 0 : Array.FindIndex(choices, 1, 3, c => Math.Abs(c.Value!.Value - _draft.CtcMinSimilarity.Value) < .001f);
        if (selected < 0) selected = 4;
        void Refresh()
        {
            picker.SetOptions(choices.Select((choice, index) => new Choice(index.ToString(), choice.Name,
                choice.Value is { } value ? Loc.T("Minimum similarity: {0:0}%", value * 100) : "")).ToArray(), selected.ToString());
            advanced.Visibility = selected == 4 ? Visibility.Visible : Visibility.Collapsed;
            valueLabel.Text = Loc.T("Minimum similarity: {0:0}%", slider.Value);
        }
        picker.SelectionChanged += id =>
        {
            selected = int.Parse(id);
            _draft = _draft! with { CtcMinSimilarity = selected == 4 ? (float)(slider.Value / 100) : choices[selected].Value };
            Refresh();
        };
        slider.ValueChanged += (_, _) =>
        {
            if (selected == 4) _draft = _draft! with { CtcMinSimilarity = (float)(slider.Value / 100) };
            valueLabel.Text = Loc.T("Minimum similarity: {0:0}%", slider.Value);
        };
        Refresh();
        _editor!.Children.Add(new SettingsRow { Padding = new Thickness(0) }.Set(Loc.T("Boosting"), "",
            Loc.T("Lower similarity considers more spellings. CTC compares acoustic scores with a vocabulary bonus. Auto uses 52–60%, depending on dictionary size."), picker).Below(advanced));
    }

    private void AddField(string label, string value, Action<string> update, int maxLength, bool multiline = false, string? help = null)
    {
        var field = new StackPanel { Spacing = 6 }; field.Children.Add(help is null ? Text(label, 12, true) : SettingsHelp.Label(label, help, 12));
        var input = Input(value, label, multiline); input.MaxLength = maxLength;
        input.TextChanged += (_, _) => update(input.Text);
        _firstInput ??= input;
        var box = Surface(input, 2); box.Background = Brush("InkBrush");
        field.Children.Add(box); _editor!.Children.Add(field);
    }

    private void AddToggle(string title, string hint, bool value, Action<bool> update)
    {
        var toggle = AppToggleSwitch.Create(value); AutomationProperties.SetName(toggle, title); toggle.Toggled += (_, _) => update(toggle.IsOn);
        _editor!.Children.Add(new SettingsRow { Padding = new Thickness(0) }.Set(title, hint, toggle));
    }

    private void RenderActions()
    {
        _actions.Children.Clear();
        if (_draft is null)
        {
            if (_kind != LexiconKind.Snippet)
            {
                _actions.Children.Add(Button(Loc.T("Suggest aliases…"), () => StartAliasSuggestions()));
                _actions.Children.Add(Button(Loc.T("Train word…"), () =>
                {
                    if (_trainingTask is null || _trainingTask.IsCompleted) _trainingTask = TrainWordAsync();
                }));
            }
            var import = Button(Loc.T("Import"), () => { });
            var importMenu = new MenuFlyout();
            var jsonImport = new MenuFlyoutItem { Text = "TypeWhisper JSON…" };
            jsonImport.Click += (_, _) => _ = ImportAsync();
            var appImport = new MenuFlyoutItem { Text = Loc.T("From another app…") };
            appImport.Click += (_, _) => _ = ImportFromAppAsync();
            importMenu.Items.Add(jsonImport); importMenu.Items.Add(appImport);
            import.Flyout = importMenu;
            _actions.Children.Add(import);
            _actions.Children.Add(Button(Loc.T("Export"), () => _ = ExportAsync()));
            _actions.Children.Add(Button(_kind switch { LexiconKind.Word => Loc.T("+ Add word"), LexiconKind.Correction => Loc.T("+ Add correction"), _ => Loc.T("+ Add snippet") }, () => OpenEditor(new(Guid.NewGuid(), _kind, "")), primary: true)); return;
        }
    }

    private void RenderPacks()
    {
        _heading.Text = Loc.T("Dictionary");
        AutomationProperties.SetName(_headingHelp, Loc.T("About term packs"));
        _actions.Children.Clear();
        _actions.Children.Add(Button(Loc.T("Back to Dictionary"), () => { _showPacks = false; _kind = LexiconKind.Word; Render(); }));
        _notice.Text = _store.LastError ?? (DictionaryBoostingPreferences.Load() ? ""
            : Loc.T("Vocabulary boosting is off. Turn it on in Settings > Dictation to use term packs."));
        var packs = new SettingsCard();
        _body.Children.Add(packs);
        SettingsHelp.Update(_headingHelp, Loc.T("Add specialist vocabulary from the existing TypeWhisper packs. Personal words stay untouched when you turn a pack off."));
        foreach (var pack in TypeWhisper.Core.Models.TermPack.AllPacks.Where(p => !p.RequiresCommercialLicense))
        {
            var toggle = AppToggleSwitch.Create(_store.PackEnabled(pack.Id));
            AutomationProperties.SetName(toggle, Loc.T("Enable term pack {0}", pack.Name));
            var restoring = false;
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                if (_closing) return;
                var error = _store.SetPackEnabled(pack, toggle.IsOn);
                if (error is not null) _notice.Text = error;
                if (error is not null) { restoring = true; toggle.IsOn = _store.PackEnabled(pack.Id); restoring = false; }
            };
            var terms = string.Join(", ", pack.Terms.Take(8)) + (pack.Terms.Length > 8 ? "…" : "");
            packs.Children.Add(new SettingsRow().Set(Loc.T("{0} · {1} terms", pack.Name, pack.Terms.Length), terms, "", toggle));
        }
        _scroll.ChangeView(null, 0, null, true);
    }

    private async Task ImportAsync()
    {
        if (_closing || _transferCompletion is { Task.IsCompleted: false }) return;
        var completion = _transferCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var snippets = _kind == LexiconKind.Snippet;
        IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { Title = snippets ? Loc.T("Import snippets") : Loc.T("Import personal dictionary") };
            picker.FileTypeFilter.Add(".json");
            var operation = picker.PickSingleFileAsync();
            _cancelPicker = () => operation.Cancel();
            var file = await operation;
            _cancelPicker = null;
            if (_closing) return;
            if (file is null) { _notice.Text = Loc.T("Import canceled."); return; }
            if (new FileInfo(file.Path).Length > 20_000_000) throw new InvalidDataException("Choose a JSON file smaller than 20 MB.");
            var json = await File.ReadAllTextAsync(file.Path);
            if (_closing) return;
            var count = _store.PreviewImport(json, snippets);
            _notice.Text = snippets
                ? Loc.T("Validated {0} snippets from {1}. Add rejects conflicts. Replace removes existing snippets. Term packs stay unchanged.", count, Path.GetFileName(file.Path))
                : Loc.T("Validated {0} personal words and corrections from {1}. Add rejects conflicts. Replace removes existing personal words and corrections. Term packs stay unchanged.", count, Path.GetFileName(file.Path));
            _actions.Children.Clear();
            _actions.Children.Add(Button(Loc.T("Cancel import"), () => { Render(); _notice.Text = Loc.T("Import canceled."); }));
            void Apply(bool replace)
            {
                if (_closing) return;
                var error = _store.Import(json, snippets, replace);
                if (error is not null) { _notice.Text = error; return; }
                Render(); _notice.Text = snippets ? Loc.T("Imported {0} snippets. Saved for the next dictation.", count)
                    : Loc.T("Imported {0} personal words and corrections. Saved for the next dictation.", count);
            }
            _actions.Children.Add(Button(Loc.T("Add entries"), () => Apply(false), primary: true));
            _actions.Children.Add(Button(snippets ? Loc.T("Replace snippets") : Loc.T("Replace personal dictionary"), () =>
            {
                _notice.Text = snippets
                    ? Loc.T("Replace all existing snippets with the {0} validated entries? This cannot be undone. Export a backup first if needed.", count)
                    : Loc.T("Replace all existing personal words and corrections with the {0} validated entries? This cannot be undone. Export a backup first if needed.", count);
                _actions.Children.Clear();
                _actions.Children.Add(Button(Loc.T("Cancel import"), () => Render()));
                _actions.Children.Add(Button(Loc.T("Replace now"), () => Apply(true), destructive: true));
            }, destructive: true));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _notice.Text = Loc.T("Import canceled: {0}", ex.Message); }
        finally
        {
            _cancelPicker = null;
            try { IsEnabled = !_closing; }
            finally { completion.TrySetResult(); }
        }
    }

    private async Task ExportAsync()
    {
        if (_closing || _transferCompletion is { Task.IsCompleted: false }) return;
        var completion = _transferCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var snippets = _kind == LexiconKind.Snippet;
        IsEnabled = false;
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                Title = snippets ? Loc.T("Export snippets") : Loc.T("Export personal dictionary (words and corrections, without term packs)"),
                SuggestedFileName = snippets ? "typewhisper-snippets" : "typewhisper-dictionary"
            };
            picker.FileTypeChoices.Add("TypeWhisper JSON", new List<string> { ".json" });
            var operation = picker.PickSaveFileAsync();
            _cancelPicker = () => operation.Cancel();
            var file = await operation;
            _cancelPicker = null;
            if (_closing) return;
            if (file is null) { _notice.Text = Loc.T("Export canceled."); return; }
            _notice.Text = _store.Export(file.Path, snippets) ?? (snippets
                ? Loc.T("Snippets exported with tags, timestamps and usage counts.")
                : Loc.T("Personal words and corrections exported with metadata. Installed term packs are managed separately."));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { _notice.Text = Loc.T("Export failed: {0}", ex.Message); }
        finally
        {
            _cancelPicker = null;
            try { IsEnabled = !_closing; }
            finally { completion.TrySetResult(); }
        }
    }

    private static TextBox Input(string value, string name, bool multiline)
    {
        var input = new TextBox { MinHeight = multiline ? 120 : 36, MaxHeight = multiline ? 220 : 36,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Style = (Style)Application.Current.Resources[multiline ? "LexiconMultilineStyle" : "SearchTextBoxStyle"],
            Padding = new Thickness(12, 8, 12, 8), IsSpellCheckEnabled = multiline };
        // Set content only after AcceptsReturn: WinUI otherwise truncates initial multiline values.
        input.Text = value;
        AutomationProperties.SetName(input, name); return input;
    }
    private static Border Surface(UIElement child, double padding)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.GotFocus += (_, _) => border.BorderBrush = Brush("FocusBrush");
        border.LostFocus += (_, _) => border.BorderBrush = Brush("HairlineBrush");
        return border;
    }
    private HandCursorButton Button(string label, Action click, bool primary = false, bool destructive = false)
    {
        var button = new HandCursorButton { Content = label, Style = (Style)Application.Current.Resources[destructive ? "DestructiveButtonStyle" : primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"] };
        button.Click += (_, _) => { if (!_closing) click(); }; return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
