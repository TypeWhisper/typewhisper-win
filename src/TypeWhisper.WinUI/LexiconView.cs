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
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Breadcrumbs _crumbs = new();
    private readonly TextBlock _heading = Text(Loc.T("Dictionary"), 22);
    private readonly HandCursorButton _headingHelp = SettingsHelp.Button(Loc.T("Dictionary"), Loc.T("Dictionary and snippets are saved in this profile."));
    private readonly TextBlock _notice = Text(Loc.T("Dictionary and snippets are saved in this profile."), 11, true);
    private readonly TextBlock _count = Text("", 11, true);
    private readonly ScrollViewer _scroll;
    private LexiconKind _kind;
    private LexiconEntry? _original;
    private LexiconEntry? _draft;
    private Action? _pending;
    private bool _confirmDelete;
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
        _heading.FontSize = 20; _heading.MinHeight = 32; _heading.Margin = new Thickness(4, 0, 0, 0);
        // Settings list Dictionary and Snippets as separate pages, so only the dictionary has tabs.
        _tabs.SetItems([new("Word", Loc.T("Words")), new("Correction", Loc.T("Corrections")), new("packs", Loc.T("Term packs"))], "Word");
        _tabs.SelectionChanged += id =>
        {
            _showPacks = id == "packs";
            if (!_showPacks) { _kind = Enum.Parse<LexiconKind>(id); _query = ""; }
            Render();
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(_heading); titleRow.Children.Add(_headingHelp);
        header.Children.Add(_crumbs);
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
        _draft = _original = null; _pending = null; _query = ""; Render();
    }

    internal void GoBack()
    {
        if (_confirmDelete) { _confirmDelete = false; RenderActions(); _notice.Text = Loc.T("Entry kept."); return; }
        if (_pending is not null) { _pending = null; Render(); return; }
        Navigate(_draft is not null ? CloseEditor : () => ExitRequested?.Invoke());
    }

    private void Navigate(Action next)
    {
        if (_draft is not null && _draft != _original)
        {
            _pending = next; RenderActions(); _notice.Text = Loc.T("You have unsaved changes. Keep editing or discard them to leave.");
            _actions.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);
        }
        else next();
    }

    private void CloseEditor() { _draft = _original = null; _pending = null; _confirmDelete = false; Render(); }
    private string Section => _kind switch { LexiconKind.Word => Loc.T("Words"), LexiconKind.Correction => Loc.T("Corrections"), _ => Loc.T("Snippets") };
    private string Icon => _kind == LexiconKind.Snippet ? "text" : "dictionary";

    private void Render()
    {
        _body.Children.Clear(); _rows.Children.Clear();
        _tabs.Visibility = _draft is null && !(_kind == LexiconKind.Snippet && !_showPacks) ? Visibility.Visible : Visibility.Collapsed;
        if (_draft is null) RenderTabs();
        if (_showPacks) { RenderPacks(); return; }
        _heading.Text = _draft is null ? (_kind == LexiconKind.Snippet ? Loc.T("Snippets") : Loc.T("Dictionary")) :
            _store.Entries.Any(entry => entry.Id == _draft.Id)
                ? _kind switch { LexiconKind.Word => Loc.T("Edit word"), LexiconKind.Correction => Loc.T("Edit correction"), _ => Loc.T("Edit snippet") }
                : _kind switch { LexiconKind.Word => Loc.T("New word"), LexiconKind.Correction => Loc.T("New correction"), _ => Loc.T("New snippet") };
        if (_draft is null) _crumbs.SetItems(new Crumb(Section));
        else _crumbs.SetItems(new(Section, () => Navigate(CloseEditor)), new(Loc.T("Editor")));
        _notice.Text = _store.LastError ?? (_kind == LexiconKind.Snippet ? Loc.T("Saved snippets are applied to your next dictation.") : Loc.T("Saved · applied to the next dictation using existing Windows dictionary rules."));
        AutomationProperties.SetName(_headingHelp, Loc.T("About {0}", _draft is null ? Section : _heading.Text));
        if (_draft is null) RenderList(); else RenderEditor();
        RenderActions(); _scroll.ChangeView(null, 0, null, true);
    }

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
        _body.Children.Add(Surface(searchGrid, 6)); _body.Children.Add(_count); _body.Children.Add(_rows); SearchChanged();
    }

    private void RenderRows()
    {
        _rows.Children.Clear(); var entries = _store.Search(_kind, _query).ToArray();
        var total = _store.Entries.Count(entry => entry.Kind == _kind);
        _count.Text = _kind switch { LexiconKind.Word => Loc.T("{0} of {1} words", entries.Length, total), LexiconKind.Correction => Loc.T("{0} of {1} corrections", entries.Length, total), _ => Loc.T("{0} of {1} snippets", entries.Length, total) };
        if (entries.Length == 0)
        {
            var empty = new StackPanel { Spacing = 10, Padding = new Thickness(16, 24, 16, 24) };
            empty.Children.Add(new TypeWhisperGlyph { Kind = "search", Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center });
            var title = Text(_query.Length == 0 ? _kind switch { LexiconKind.Word => Loc.T("Your first word starts here"), LexiconKind.Correction => Loc.T("Your first correction starts here"), _ => Loc.T("Your first snippet starts here") } : Loc.T("No matching entries"), 16); title.TextAlignment = TextAlignment.Center; empty.Children.Add(title);
            var hint = Text(_query.Length == 0 ? Loc.T("Add a term or phrase with the button below.") : Loc.T("Try a different word, phrase, or tag."), 12, true); hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            _rows.Children.Add(empty); return;
        }
        if (_kind == LexiconKind.Correction) { RenderCorrectionGroups(entries); return; }
        foreach (var entry in entries)
        {
            var content = new Grid { ColumnSpacing = 14, Padding = new Thickness(2, 6, 2, 6) };
            content.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(new TypeWhisperGlyph { Kind = Icon, Width = 20, Height = 20 });
            var labels = new StackPanel { Spacing = 5 }; var title = Text(_kind == LexiconKind.Correction ? entry.Value : entry.Key, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; labels.Children.Add(title);
            if (_kind != LexiconKind.Word)
            {
                var description = Text(_kind == LexiconKind.Correction ? Loc.T("Recognized as: {0}", entry.Key) : entry.Value.Replace('\n', ' '), 12, true);
                description.MaxLines = 1; description.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(description);
            }
            if (entry.Tags.Length > 0) labels.Children.Add(Text(entry.Tags, 11, true));
            if (entry.Kind == LexiconKind.Snippet) labels.Children.Add(Text(entry.UsageCount == 1 ? Loc.T("Used 1 time") : Loc.T("Used {0} times", entry.UsageCount), 11, true));
            Grid.SetColumn(labels, 1); content.Children.Add(labels);
            var trailing = Text(entry.Enabled ? Loc.T("Edit  ›") : Loc.T("Off  ·  Edit  ›"), 11, true); trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2); content.Children.Add(trailing);
            var row = Button("", () => { if (entry.FromPack) { _showPacks = true; Render(); } else OpenEditor(entry); }); row.ContextFlyout = LexiconEntryMenu(entry); row.Content = content; row.HorizontalContentAlignment = HorizontalAlignment.Stretch; row.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (entry.FromPack) trailing.Text = Loc.T("Term packs  ›");
            row.Style = (Style)Application.Current.Resources["MenuButtonStyle"];
            AutomationProperties.SetName(row, entry.FromPack ? Loc.T("Manage term pack for {0}", entry.Key) : entry.Kind == LexiconKind.Correction ? Loc.T("Edit correction: {0}, recognized as {1}", entry.Value, entry.Key) : _kind == LexiconKind.Snippet ? Loc.T("Edit snippet: {0}", entry.Key) : Loc.T("Edit word: {0}", entry.Key)); _rows.Children.Add(row);
        }
    }

    private void RenderCorrectionGroups(LexiconEntry[] matches)
    {
        var matchingTargets = matches.Select(entry => entry.Value).ToHashSet(StringComparer.Ordinal);
        var groups = _store.Entries.Where(entry => entry.Kind == LexiconKind.Correction && matchingTargets.Contains(entry.Value))
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        _count.Text = groups.Length == 1 ? Loc.T("{0} spelling · {1} variants", groups.Length, groups.Sum(group => group.Count()))
            : Loc.T("{0} spellings · {1} variants", groups.Length, groups.Sum(group => group.Count()));
        foreach (var group in groups)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var title = Text(group.Key, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            header.Children.Add(title);
            header.Children.Add(Text(group.Count() == 1 ? Loc.T("1 variant") : Loc.T("{0} variants", group.Count()), 12, true));
            var aliases = new StackPanel { Spacing = 6 };
            foreach (var alias in group.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var current = alias;
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var edit = Button("", () => OpenEditor(current));
                EntryActionMenu.Attach(edit, () => [new(Loc.T("Edit variant"), () => OpenEditor(current)), new(Loc.T("Delete variant…"), () => { OpenEditor(current); EntryActionMenu.FromButtons(_actions).FirstOrDefault(action => action.Label == Loc.T("Delete"))?.Invoke(); })]);
                var label = Text(alias.Key, 13);
                label.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
                edit.Content = label;
                edit.Style = (Style)Application.Current.Resources["MenuButtonStyle"];
                edit.HorizontalAlignment = HorizontalAlignment.Stretch;
                edit.HorizontalContentAlignment = HorizontalAlignment.Left;
                AutomationProperties.SetName(edit, Loc.T("Edit variant {0} for {1}", alias.Key, group.Key));
                ToolTipService.SetToolTip(edit, Loc.T("Edit variant"));
                row.Children.Add(edit);
                var toggle = AppToggleSwitch.Create(alias.Enabled);
                AutomationProperties.SetName(toggle, Loc.T("Enable correction from {0} to {1}", alias.Key, group.Key));
                var restoring = false;
                toggle.Toggled += (_, _) =>
                {
                    if (restoring || _closing) return;
                    var updated = current with { Enabled = toggle.IsOn };
                    var error = _store.Save(updated);
                    if (error is null) { current = updated; _notice.Text = Loc.T("Correction saved."); }
                    else
                    {
                        restoring = true; toggle.IsOn = current.Enabled; restoring = false;
                        _notice.Text = error;
                    }
                };
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle); aliases.Children.Add(row);
            }
            var panel = new StackPanel { Spacing = 4 };
            var headingRow = new Grid { ColumnSpacing = 12 };
            headingRow.ColumnDefinitions.Add(new());
            headingRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var chevron = Text("", 12, true);
            header.Children.Insert(0, chevron);
            void UpdateExpansion()
            {
                var expanded = _expandedCorrections.Contains(group.Key);
                aliases.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = expanded ? "⌄" : "›";
            }
            var expand = Button("", () =>
            {
                if (!_expandedCorrections.Add(group.Key)) _expandedCorrections.Remove(group.Key);
                UpdateExpansion();
            });
            expand.Content = header;
            expand.Style = (Style)Application.Current.Resources["MenuButtonStyle"];
            expand.HorizontalAlignment = HorizontalAlignment.Stretch;
            expand.HorizontalContentAlignment = HorizontalAlignment.Left;
            expand.MinHeight = 32;
            expand.Padding = new Thickness(4, 4, 4, 4);
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
            Grid.SetColumn(add, 1); headingRow.Children.Add(add);
            panel.Children.Add(headingRow);
            panel.Children.Add(aliases);
            UpdateExpansion();
            var card = Surface(panel, 6);
            card.Padding = new Thickness(8, 5, 8, 5);
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
                        DefaultButton = ContentDialogButton.Close
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
            _rows.Children.Add(card);
        }
    }

    private MenuFlyout LexiconEntryMenu(LexiconEntry entry)
    {
        if (entry.FromPack)
            return EntryActionMenu.Create([new(Loc.T("Show term pack"), () => { _showPacks = true; Render(); })]);
        return EntryActionMenu.Create([
            new(Loc.T("Edit"), () => OpenEditor(entry)),
            .. entry.Kind == LexiconKind.Word ? new EntryActionMenu.Action[] { new(Loc.T("Suggest aliases…"), () => StartAliasSuggestions(entry.Key)) } : [],
            new(entry.Enabled ? Loc.T("Disable") : Loc.T("Enable"), () =>
            {
                var error = _store.Save(entry with { Enabled = !entry.Enabled });
                _notice.Text = error ?? Loc.T("Entry updated.");
                if (error is null) Render();
            }),
            new(Loc.T("Delete…"), () =>
            {
                OpenEditor(entry);
                EntryActionMenu.FromButtons(_actions).FirstOrDefault(action => action.Label == Loc.T("Delete"))?.Invoke();
            })
        ]);
    }

    private void OpenEditor(LexiconEntry entry)
    {
        _apiEditorBaseline = _store.Entries.FirstOrDefault(current => current.Id == entry.Id);
        _original = _draft = entry; Render();
        DispatcherQueue.TryEnqueue(() => _body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children).OfType<Border>()
            .Select(border => border.Child).OfType<TextBox>().FirstOrDefault()?.Focus(FocusState.Programmatic));
    }

    private void RenderEditor()
    {
        SettingsHelp.Update(_headingHelp, _kind switch
        {
            LexiconKind.Word => Loc.T("Save the exact spelling of a name or specialist term."),
            LexiconKind.Correction => Loc.T("When this phrase is recognized, use your preferred spelling instead."),
            _ => Loc.T("Say the trigger phrase to insert this text when dictation finishes.")
        });
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
        AddToggle(Loc.T("Enabled"), Loc.T("Keep this entry available without removing it."), _draft.Enabled, value => _draft = _draft! with { Enabled = value });
        if (_kind == LexiconKind.Word) AddBoostingOptions();
        if (_kind != LexiconKind.Word)
            AddToggle(Loc.T("Match capitalization"), Loc.T("Only match the trigger with this exact capitalization."), _draft.CaseSensitive, value => _draft = _draft! with { CaseSensitive = value });
    }

    private void AddBoostingOptions()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(SettingsHelp.Label(Loc.T("Boosting"), Loc.T("Lower similarity considers more spellings. CTC compares acoustic scores with a vocabulary bonus. Auto uses 52–60%, depending on dictionary size.")));
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var slider = new Slider { Minimum = 40, Maximum = 95, StepFrequency = 1, Value = (_draft!.CtcMinSimilarity ?? .65f) * 100 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, Loc.T("Minimum CTC similarity in percent"));
        var valueLabel = Text("", 12, true);
        var advanced = new StackPanel { Spacing = 4 }; advanced.Children.Add(slider); advanced.Children.Add(valueLabel);
        var choices = new (string Name, float? Value)[] { (Loc.T("Auto"), null), (Loc.T("Strong"), .5f), (Loc.T("Balanced"), .65f), (Loc.T("Precise"), .8f), (Loc.T("Advanced"), null) };
        var selected = _draft.CtcMinSimilarity is null ? 0 : Array.FindIndex(choices, 1, 3, c => Math.Abs(c.Value!.Value - _draft.CtcMinSimilarity.Value) < .001f);
        if (selected < 0) selected = 4;
        void Refresh()
        {
            options.Children.Clear();
            for (var index = 0; index < choices.Length; index++)
            {
                var choice = index;
                options.Children.Add(Button(choices[index].Name, () =>
                {
                    selected = choice;
                    _draft = _draft! with { CtcMinSimilarity = choice == 4 ? (float)(slider.Value / 100) : choices[choice].Value };
                    Refresh();
                }, primary: index == selected));
            }
            advanced.Visibility = selected == 4 ? Visibility.Visible : Visibility.Collapsed;
            valueLabel.Text = Loc.T("Minimum similarity: {0:0}%", slider.Value);
        }
        slider.ValueChanged += (_, _) =>
        {
            if (selected == 4) _draft = _draft! with { CtcMinSimilarity = (float)(slider.Value / 100) };
            valueLabel.Text = Loc.T("Minimum similarity: {0:0}%", slider.Value);
        };
        Refresh(); panel.Children.Add(options); panel.Children.Add(advanced);
        _body.Children.Add(Surface(panel, 14));
    }

    private void AddField(string label, string value, Action<string> update, int maxLength, bool multiline = false, string? help = null)
    {
        var field = new StackPanel { Spacing = 7 }; field.Children.Add(help is null ? Text(label, 12, true) : SettingsHelp.Label(label, help, 12));
        var input = Input(value, label, multiline); input.MaxLength = maxLength;
        input.TextChanged += (_, _) => update(input.Text); field.Children.Add(Surface(input, 2)); _body.Children.Add(field);
    }

    private void AddToggle(string title, string hint, bool value, Action<bool> update)
    {
        var row = new Grid { ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(SettingsHelp.Label(title, hint, 13));
        var toggle = AppToggleSwitch.Create(value); AutomationProperties.SetName(toggle, title); toggle.Toggled += (_, _) => update(toggle.IsOn);
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle); _body.Children.Add(row);
    }

    private void RenderActions()
    {
        _confirmDelete = false;
        _actions.Children.Clear();
        if (_pending is not null)
        {
            _actions.Children.Add(Button(Loc.T("Keep editing"), () => { _pending = null; _notice.Text = Loc.T("Your changes are still here."); RenderActions(); }));
            _actions.Children.Add(Button(Loc.T("Discard"), () => { var next = _pending; _pending = null; next?.Invoke(); }, destructive: true)); return;
        }
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
        if (_store.Entries.Any(entry => entry.Id == _draft.Id))
            _actions.Children.Add(Button(Loc.T("Delete"), () =>
            {
                _confirmDelete = true;
                _notice.Text = Loc.T("Delete this entry? Installed production data is unchanged.");
                _actions.Children.Clear();
                _actions.Children.Add(Button(Loc.T("Keep entry"), () => { RenderActions(); _notice.Text = Loc.T("Entry kept."); }));
                _actions.Children.Add(Button(Loc.T("Delete entry"), () => { if (!CanSaveApiEditor()) return; if (!_store.Remove(_draft!.Id)) { _notice.Text = _store.LastError ?? Loc.T("Could not delete entry."); return; } CloseEditor(); _notice.Text = Loc.T("Entry deleted."); }, destructive: true));
            }, destructive: true));
        _actions.Children.Add(Button(Loc.T("Cancel"), () => Navigate(CloseEditor)));
        _actions.Children.Add(Button(Loc.T("Save"), () =>
        {
            if (!CanSaveApiEditor()) return;
            var error = _store.Save(_draft!);
            if (error is not null) { _notice.Text = error; return; }
            CloseEditor(); _notice.Text = _kind == LexiconKind.Snippet ? Loc.T("Snippet saved for the next dictation.") : Loc.T("Dictionary saved for the next dictation.");
        }, primary: true));
    }

    private void RenderPacks()
    {
        _heading.Text = Loc.T("Dictionary");
        AutomationProperties.SetName(_headingHelp, Loc.T("About term packs"));
        _crumbs.SetItems(new(Loc.T("Dictionary"), () => { _showPacks = false; Render(); }), new(Loc.T("Term packs")));
        _actions.Children.Clear();
        _actions.Children.Add(Button(Loc.T("Back to Dictionary"), () => { _showPacks = false; _kind = LexiconKind.Word; Render(); }));
        _notice.Text = _store.LastError ?? (DictionaryBoostingPreferences.Load()
            ? Loc.T("Saved packs provide dictionary terms for enabled vocabulary processing.")
            : Loc.T("Saved packs · enable Vocabulary boosting in Settings > Dictation > More options to use them."));
        SettingsHelp.Update(_headingHelp, Loc.T("Add specialist vocabulary from the existing TypeWhisper packs. Personal words stay untouched when you turn a pack off."));
        foreach (var pack in TypeWhisper.Core.Models.TermPack.AllPacks.Where(p => !p.RequiresCommercialLicense))
        {
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TypeWhisperGlyph { Kind = "dictionary", Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Spacing = 5 };
            labels.Children.Add(Text(Loc.T("{0} · {1} terms", pack.Name, pack.Terms.Length), 14));
            labels.Children.Add(Text(string.Join(", ", pack.Terms.Take(8)) + (pack.Terms.Length > 8 ? "…" : ""), 12, true));
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var toggle = AppToggleSwitch.Create(_store.PackEnabled(pack.Id));
            AutomationProperties.SetName(toggle, Loc.T("Enable term pack {0}", pack.Name));
            var restoring = false;
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                if (_closing) return;
                var error = _store.SetPackEnabled(pack, toggle.IsOn);
                _notice.Text = error ?? (toggle.IsOn ? Loc.T("{0} enabled · saved.", pack.Name) : Loc.T("{0} disabled · saved.", pack.Name));
                if (error is not null) { restoring = true; toggle.IsOn = _store.PackEnabled(pack.Id); restoring = false; }
            };
            Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
            _body.Children.Add(Surface(row, 14));
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
