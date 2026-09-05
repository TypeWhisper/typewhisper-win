using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed class PrototypeLexiconView : UserControl
{
    private readonly PrototypeLexicon _store = PrototypeLexicon.CreateSamples();
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly PrototypeBreadcrumbs _crumbs = new();
    private readonly TextBlock _heading = Text("Dictionary", 22);
    private readonly TextBlock _notice = Text("Sample data · changes last for this session only.", 11, true);
    private readonly TextBlock _count = Text("", 11, true);
    private readonly ScrollViewer _scroll;
    private PrototypeLexiconKind _kind;
    private PrototypeLexiconEntry? _original;
    private PrototypeLexiconEntry? _draft;
    private Action? _pending;
    private bool _confirmDelete;
    private string _query = "";
    internal event Action? ExitRequested;

    public PrototypeLexiconView()
    {
        var root = new Grid { Background = Brush("InkBrush"), Padding = new Thickness(24, 8, 24, 0), RowSpacing = 12 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(_heading, AutomationHeadingLevel.Level1); root.Children.Add(_heading);
        _scroll = new ScrollViewer { Content = _body, Padding = new Thickness(0, 0, 8, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite); Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        var footer = new Grid { MinHeight = 52, ColumnSpacing = 10 }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_crumbs); Grid.SetColumn(_actions, 1); footer.Children.Add(_actions);
        var border = new Border { Child = footer, BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(border, 3); root.Children.Add(border); Content = root;
    }

    internal void Present(bool snippets)
    {
        _kind = snippets ? PrototypeLexiconKind.Snippet : PrototypeLexiconKind.Word;
        _draft = _original = null; _pending = null; _query = ""; Render();
    }

    internal void GoBack()
    {
        if (_confirmDelete) { _confirmDelete = false; RenderActions(); _notice.Text = "Entry kept."; return; }
        if (_pending is not null) { _pending = null; Render(); return; }
        Navigate(_draft is not null ? CloseEditor : () => ExitRequested?.Invoke());
    }

    private void Navigate(Action next)
    {
        if (_draft is not null && _draft != _original)
        {
            _pending = next; RenderActions(); _notice.Text = "You have unsaved changes. Keep editing or discard them to leave.";
            _actions.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);
        }
        else next();
    }

    private void CloseEditor() { _draft = _original = null; _pending = null; _confirmDelete = false; Render(); }
    private string Section => _kind switch { PrototypeLexiconKind.Word => "Words", PrototypeLexiconKind.Correction => "Corrections", _ => "Snippets" };
    private string Singular => _kind switch { PrototypeLexiconKind.Word => "word", PrototypeLexiconKind.Correction => "correction", _ => "snippet" };
    private string Icon => _kind == PrototypeLexiconKind.Snippet ? "text" : "dictionary";

    private void Render()
    {
        _body.Children.Clear(); _rows.Children.Clear();
        _heading.Text = _draft is null ? (_kind == PrototypeLexiconKind.Snippet ? "Snippets" : "Dictionary") :
            $"{(_store.Entries.Any(entry => entry.Id == _draft.Id) ? "Edit" : "New")} {Singular}";
        var launch = new PrototypeCrumb("Quick Launch", () => Navigate(() => { _draft = _original = null; ExitRequested?.Invoke(); }));
        if (_draft is null) _crumbs.SetItems(launch, new(Section));
        else _crumbs.SetItems(launch, new(Section, () => Navigate(CloseEditor)), new("Editor"));
        _notice.Text = "Sample data · changes last for this session only.";
        if (_draft is null) RenderList(); else RenderEditor();
        RenderActions(); _scroll.ChangeView(null, 0, null, true);
    }

    private void RenderList()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var kind in Enum.GetValues<PrototypeLexiconKind>())
        {
            var label = kind switch { PrototypeLexiconKind.Word => "Words", PrototypeLexiconKind.Correction => "Corrections", _ => "Snippets" };
            var tab = Button(label, () => { _kind = kind; _query = ""; Render(); }, primary: kind == _kind);
            AutomationProperties.SetName(tab, label + (kind == _kind ? ", selected" : "")); tabs.Children.Add(tab);
        }
        _body.Children.Add(tabs);
        _body.Children.Add(Text(_kind switch
        {
            PrototypeLexiconKind.Word => "Names and specialist terms you want TypeWhisper to recognize.",
            PrototypeLexiconKind.Correction => "Replace commonly misheard phrases with the spelling you prefer.",
            _ => "Turn a short spoken phrase into a reusable block of text."
        }, 13, true));
        var search = Input(_query, "Search " + Section.ToLowerInvariant(), false);
        var searchGrid = new Grid { ColumnSpacing = 8 }; searchGrid.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); searchGrid.ColumnDefinitions.Add(new());
        searchGrid.Children.Add(new TypeWhisperGlyph { Kind = "search", Width = 18, Height = 18 });
        var placeholder = Text("Search " + Section.ToLowerInvariant() + "…", 14, true); placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(12, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(search, 1); Grid.SetColumn(placeholder, 1); searchGrid.Children.Add(search); searchGrid.Children.Add(placeholder);
        void SearchChanged() { _query = search.Text; placeholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RenderRows(); }
        search.TextChanged += (_, _) => SearchChanged();
        _body.Children.Add(Surface(searchGrid, 6)); _body.Children.Add(_count); _body.Children.Add(_rows); SearchChanged();
    }

    private void RenderRows()
    {
        _rows.Children.Clear(); var entries = _store.Search(_kind, _query).ToArray();
        _count.Text = $"{entries.Length} of {_store.Entries.Count(entry => entry.Kind == _kind)} {Section.ToLowerInvariant()}";
        if (entries.Length == 0)
        {
            var empty = new StackPanel { Spacing = 10, Padding = new Thickness(16, 24, 16, 24) };
            empty.Children.Add(new TypeWhisperGlyph { Kind = "search", Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center });
            var title = Text(_query.Length == 0 ? $"Your first {Singular} starts here" : "No matching entries", 16); title.TextAlignment = TextAlignment.Center; empty.Children.Add(title);
            var hint = Text(_query.Length == 0 ? "Add a term or phrase with the button below." : "Try a different word, phrase, or tag.", 12, true); hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            _rows.Children.Add(empty); return;
        }
        foreach (var entry in entries)
        {
            var content = new Grid { ColumnSpacing = 14, Padding = new Thickness(2, 6, 2, 6) };
            content.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(new TypeWhisperGlyph { Kind = Icon, Width = 20, Height = 20 });
            var labels = new StackPanel { Spacing = 5 }; var title = Text(entry.Key, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; labels.Children.Add(title);
            if (_kind != PrototypeLexiconKind.Word)
            {
                var description = Text((_kind == PrototypeLexiconKind.Correction ? "→  " : "") + entry.Value.Replace('\n', ' '), 12, true);
                description.MaxLines = 1; description.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(description);
            }
            if (entry.Tags.Length > 0) labels.Children.Add(Text(entry.Tags, 11, true));
            Grid.SetColumn(labels, 1); content.Children.Add(labels);
            var trailing = Text(entry.Enabled ? "Edit  ›" : "Off  ·  Edit  ›", 11, true); trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2); content.Children.Add(trailing);
            var row = Button("", () => OpenEditor(entry)); row.Content = content; row.HorizontalContentAlignment = HorizontalAlignment.Stretch; row.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Style = (Style)Application.Current.Resources["PrototypeMenuButtonStyle"];
            AutomationProperties.SetName(row, $"Edit {Singular}: {entry.Key}"); _rows.Children.Add(row);
        }
    }

    private void OpenEditor(PrototypeLexiconEntry entry)
    {
        _original = _draft = entry; Render();
        DispatcherQueue.TryEnqueue(() => _body.Children.OfType<StackPanel>().SelectMany(panel => panel.Children).OfType<Border>()
            .Select(border => border.Child).OfType<TextBox>().FirstOrDefault()?.Focus(FocusState.Programmatic));
    }

    private void RenderEditor()
    {
        _body.Children.Add(Text(_kind switch
        {
            PrototypeLexiconKind.Word => "Save the exact spelling of a name or specialist term.",
            PrototypeLexiconKind.Correction => "When this phrase is recognized, use your preferred spelling instead.",
            _ => "Say the trigger phrase to insert the text. Expansion is not connected in this preview."
        }, 13, true));
        AddField(_kind == PrototypeLexiconKind.Word ? "Word or phrase" : _kind == PrototypeLexiconKind.Correction ? "Recognized phrase" : "Spoken trigger", _draft!.Key, value => _draft = _draft! with { Key = value }, 160);
        if (_kind != PrototypeLexiconKind.Word)
            AddField(_kind == PrototypeLexiconKind.Snippet ? "Insert this text" : "Replace with", _draft.Value, value => _draft = _draft! with { Value = value }, 10000, _kind == PrototypeLexiconKind.Snippet);
        if (_kind == PrototypeLexiconKind.Snippet)
        {
            AddField("Tags · optional, separated by commas", _draft.Tags, value => _draft = _draft! with { Tags = value }, 300);
            _body.Children.Add(Text("Placeholders such as {date} are kept as text here. No clipboard content is accessed.", 11, true));
        }
        AddToggle("Enabled", "Keep this entry available without removing it.", _draft.Enabled, value => _draft = _draft! with { Enabled = value });
        if (_kind != PrototypeLexiconKind.Word)
            AddToggle("Match capitalization", "Only match the trigger with this exact capitalization.", _draft.CaseSensitive, value => _draft = _draft! with { CaseSensitive = value });
    }

    private void AddField(string label, string value, Action<string> update, int maxLength, bool multiline = false)
    {
        var field = new StackPanel { Spacing = 7 }; field.Children.Add(Text(label, 12, true));
        var input = Input(value, label, multiline); input.MaxLength = maxLength;
        input.TextChanged += (_, _) => update(input.Text); field.Children.Add(Surface(input, 2)); _body.Children.Add(field);
    }

    private void AddToggle(string title, string hint, bool value, Action<bool> update)
    {
        var row = new Grid { ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4 }; text.Children.Add(Text(title, 13)); text.Children.Add(Text(hint, 11, true)); row.Children.Add(text);
        var toggle = PrototypeToggleSwitch.Create(value); AutomationProperties.SetName(toggle, title); toggle.Toggled += (_, _) => update(toggle.IsOn);
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle); _body.Children.Add(row);
    }

    private void RenderActions()
    {
        _confirmDelete = false;
        _actions.Children.Clear();
        if (_pending is not null)
        {
            _actions.Children.Add(Button("Keep editing", () => { _pending = null; _notice.Text = "Your changes are still here."; RenderActions(); }));
            _actions.Children.Add(Button("Discard", () => { var next = _pending; _pending = null; next?.Invoke(); }, destructive: true)); return;
        }
        if (_draft is null)
        {
            _actions.Children.Add(Button("+ Add " + Singular, () => OpenEditor(new(Guid.NewGuid(), _kind, "")), primary: true)); return;
        }
        if (_store.Entries.Any(entry => entry.Id == _draft.Id))
            _actions.Children.Add(Button("Delete", () =>
            {
                _confirmDelete = true;
                _notice.Text = "Delete this entry from the preview? Production data is unchanged.";
                _actions.Children.Clear();
                _actions.Children.Add(Button("Keep entry", () => { RenderActions(); _notice.Text = "Entry kept."; }));
                _actions.Children.Add(Button("Delete entry", () => { _store.Remove(_draft!.Id); CloseEditor(); _notice.Text = "Entry deleted from this session only."; }, destructive: true));
            }, destructive: true));
        _actions.Children.Add(Button("Cancel", () => Navigate(CloseEditor)));
        _actions.Children.Add(Button("Save", () =>
        {
            var error = _store.Save(_draft!);
            if (error is not null) { _notice.Text = error; return; }
            CloseEditor(); _notice.Text = "Saved for this session. Production data is unchanged.";
        }, primary: true));
    }

    private static TextBox Input(string value, string name, bool multiline)
    {
        var input = new TextBox { MinHeight = multiline ? 120 : 36, MaxHeight = multiline ? 220 : 36,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Style = (Style)Application.Current.Resources[multiline ? "PrototypeLexiconMultilineStyle" : "PrototypeSearchTextBoxStyle"],
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
    private static HandCursorButton Button(string label, Action click, bool primary = false, bool destructive = false)
    {
        var button = new HandCursorButton { Content = label, Style = (Style)Application.Current.Resources[destructive ? "PrototypeDestructiveButtonStyle" : primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
        button.Click += (_, _) => click(); return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
