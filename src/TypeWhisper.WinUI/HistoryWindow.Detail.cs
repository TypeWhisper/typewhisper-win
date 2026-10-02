using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class HistoryWindow
{
    private string _detailMode = "final";
    private TextBox? _editor;
    private string? _editedId;
    private string _savedText = "";
    private bool _saving;

    private bool Dirty => _editor is not null && _editedId is not null && _editor.Text.ReplaceLineEndings("\n") != _savedText.ReplaceLineEndings("\n");
    private TranscriptionRecord? Opened => _selection is [var id] ? _records.FirstOrDefault(record => record.Id == id) : null;

    private void RenderDetail()
    {
        // Filtering or a refresh must not replace text the user is editing.
        if (Dirty && _selection is [var editing] && editing == _editedId) return;
        _editor = null;
        _editedId = null;
        if (_selection.Count > 1) { _detailHost.Child = MultipleSelection(); return; }
        if (Opened is not { } record)
        {
            _detailHost.Child = EmptyState("file", Loc.T("Select an Entry"), Loc.T("Choose an entry to read and edit its text or inspect its details."));
            return;
        }
        var body = new StackPanel { Spacing = 16, Padding = new Thickness(28, 22, 28, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        var local = DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc).ToLocalTime();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var date = Text($"{local:D} · {local:t}", 18, bold: true);
        AutomationProperties.SetHeadingLevel(date, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        header.Children.Add(date);
        if (record.InboxState is { } inbox) header.Children.Add(Tag(inbox == HistoryWorkspace.InboxOpen ? Loc.T("Inbox") : Loc.T("Completed"), inbox == HistoryWorkspace.InboxOpen));
        body.Children.Add(header);
        var source = HistoryWorkspace.IsLocal(record, LocalDeviceId()) ? SourceName(HistoryWorkspace.SourceOf(record)) : OriginName(record);
        body.Children.Add(Text(string.Join(" · ", new[] { source, Duration(record.DurationSeconds), HistoryWorkspace.AppOf(record) }.OfType<string>()), 13, muted: true));
        body.Children.Add(Details(record));
        if (record.Status != TranscriptionRecordStatus.Succeeded) body.Children.Add(FailureStrip(record));
        if (!string.IsNullOrWhiteSpace(record.AudioFileName)) body.Children.Add(AudioStrip(record));
        var processed = HistoryWorkspace.WasProcessed(record);
        if (processed)
        {
            var tabs = new TabBar();
            tabs.SetItems([new("final", Loc.T("Final")), new("original", Loc.T("Original")), new("changes", Loc.T("Changes"))], _detailMode);
            tabs.SelectionChanged += async id =>
            {
                if (id == _detailMode) return;
                if (!await ConfirmLeaveEditAsync()) { tabs.SetSelected(_detailMode); return; }
                _detailMode = id;
                RenderDetail();
                RenderToolbar();
            };
            body.Children.Add(tabs);
        }
        else _detailMode = "final";
        body.Children.Add(_detailMode switch
        {
            "original" => ReadOnlyText(record.RawText),
            "changes" => Changes(record),
            _ => Editor(record)
        });
        _detailHost.Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement Editor(TranscriptionRecord record)
    {
        _editedId = record.Id;
        _savedText = record.DisplayText;
        // Only finished entries with text can be edited, as on macOS.
        var editor = _editor = new TextBox
        {
            // AcceptsReturn must precede Text; otherwise the TextBox drops everything after the first line break.
            AcceptsReturn = true, Text = TextBoxText(_savedText), TextWrapping = TextWrapping.Wrap, FontSize = 15, MinHeight = 220,
            Padding = new Thickness(14, 12, 14, 12), IsReadOnly = record.Status != TranscriptionRecordStatus.Succeeded && string.IsNullOrWhiteSpace(record.DisplayText)
        };
        AutomationProperties.SetName(editor, Loc.T("Transcript text"));
        editor.TextChanged += (_, _) => RenderToolbar();
        return editor;
    }

    // TextBox stores line breaks as a single carriage return.
    private static string TextBoxText(string text) => text.ReplaceLineEndings("\r");

    private static UIElement ReadOnlyText(string text)
    {
        var block = Text(string.IsNullOrWhiteSpace(text) ? Loc.T("No original text was saved.") : text, 15);
        block.IsTextSelectionEnabled = true;
        block.LineHeight = 24;
        return new Border { Child = block, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) };
    }

    private static UIElement Changes(TranscriptionRecord record)
    {
        if (HistoryWorkspace.WordDiff(record.RawText, record.DisplayText) is not { } diff)
            return EmptyState("info", Loc.T("Changes Unavailable"), Loc.T("This entry is too long to compare word by word. Open Final or Original to read it."));
        var paragraph = new Paragraph { LineHeight = 24 };
        var removed = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0x6B, 0x6B));
        var added = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x4C, 0xD9, 0x64));
        foreach (var token in diff)
        {
            var run = new Run { Text = token.Text };
            if (token.Kind == HistoryDiffKind.Removed) { run.Foreground = removed; run.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough; }
            else if (token.Kind == HistoryDiffKind.Added) { run.Foreground = added; run.TextDecorations = global::Windows.UI.Text.TextDecorations.Underline; }
            paragraph.Inlines.Add(run);
        }
        var text = new RichTextBlock { FontSize = 15, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextBrush") };
        text.Blocks.Add(paragraph);
        AutomationProperties.SetName(text, Loc.T("Changes between original and final text. Removed words are struck through, added words are underlined."));
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new Border { Child = text, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) });
        panel.Children.Add(Text(Loc.T("Struck-through words were removed from what you said; underlined words were added."), 12, muted: true));
        return panel;
    }

    private Expander Details(TranscriptionRecord record)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = new List<(string, string)>
        {
            (Loc.T("Language"), string.IsNullOrWhiteSpace(record.Language) ? Loc.T("Automatic") : record.Language!),
            (Loc.T("Engine"), HistoryWorkspace.ModelLabel(record.EngineUsed, record.ModelUsed)),
            (Loc.T("Words"), record.WordCount.ToString("N0")),
            (Loc.T("Origin"), OriginName(record))
        };
        if (!string.IsNullOrWhiteSpace(record.ProfileName)) rows.Add((Loc.T("Workflow"), record.ProfileName!));
        if (!string.IsNullOrWhiteSpace(record.AppUrl)) rows.Add((Loc.T("Website"), record.AppUrl!));
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Text(label, 13, muted: true);
            var text = Text(value, 13); text.IsTextSelectionEnabled = true;
            Grid.SetRow(name, grid.RowDefinitions.Count - 1);
            Grid.SetRow(text, grid.RowDefinitions.Count - 1); Grid.SetColumn(text, 1);
            grid.Children.Add(name); grid.Children.Add(text);
        }
        return new Expander { Header = Loc.T("Details"), Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static UIElement FailureStrip(TranscriptionRecord record)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Text(Loc.T("Processing Failed"), 14, bold: true));
        panel.Children.Add(Text(record.Status == TranscriptionRecordStatus.TextProcessorFailed
            ? Loc.T("Text processing failed. The preceding transcript was kept.")
            : record.WorkflowFailureMessage ?? Loc.T("The workflow could not finish. The transcript was kept."), 13, muted: true));
        return new Border { Child = panel, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0x9F, 0x0A)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0x9F, 0x0A)), BorderThickness = new Thickness(1) };
    }

    private static Border Tag(string text, bool accent) => new()
    {
        Child = Text(text, 11, bold: true), Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
        Background = accent ? Brush("SelectionFillBrush") : Brush("ElevatedBrush")
    };

    private UIElement MultipleSelection()
    {
        var panel = EmptyState("history", Loc.T("{0:N0} Entries Selected", _selection.Count), Loc.T("Copy, export, complete or delete them together."));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(ActionButton(Loc.T("Copy"), CopySelection));
        if (SelectedRecords().Any(record => record.InboxState == HistoryWorkspace.InboxOpen))
            actions.Children.Add(ActionButton(Loc.T("Mark Complete"), () => _ = SetInboxAsync(true)));
        actions.Children.Add(ActionButton(Loc.T("Export…"), () => _ = ExportAsync(".md")));
        var delete = ActionButton(Loc.T("Delete…"), () => _ = DeleteSelectionAsync());
        delete.Style = (Style)Application.Current.Resources["DestructiveButtonStyle"];
        actions.Children.Add(delete);
        panel.Children.Add(actions);
        return panel;
    }

    private void RenderToolbar()
    {
        _toolbarActions.Children.Clear();
        if (_selection.Count == 0) return;
        var records = SelectedRecords();
        if (records.Any(record => record.InboxState == HistoryWorkspace.InboxOpen))
            _toolbarActions.Children.Add(ActionButton(Loc.T("Mark Complete"), () => _ = SetInboxAsync(true)));
        else if (records.Length == 1 && records[0].InboxState == HistoryWorkspace.InboxCompleted)
            _toolbarActions.Children.Add(ActionButton(Loc.T("Reopen"), () => _ = SetInboxAsync(false)));
        _toolbarActions.Children.Add(ActionButton(Loc.T("Copy"), CopySelection));
        if (!Dirty) return;
        _toolbarActions.Children.Add(ActionButton(Loc.T("Discard"), DiscardEdit));
        var save = ActionButton(Loc.T("Save"), () => _ = SaveAsync());
        save.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
        save.IsEnabled = !_saving && _editor?.Text.Trim().Length > 0;
        ToolTipService.SetToolTip(save, Loc.T("Save (Ctrl+S)"));
        _toolbarActions.Children.Add(save);
    }

    private void DiscardEdit()
    {
        if (_editor is null) return;
        _editor.Text = TextBoxText(_savedText);
        RenderToolbar();
    }

    private async Task SaveAsync()
    {
        if (!Dirty || _saving || _editor is null || _editedId is not { } id) return;
        var text = _editor.Text.ReplaceLineEndings("\n");
        if (text.Trim().Length == 0) { _notice.Text = Loc.T("Enter transcript text before saving."); return; }
        _saving = true;
        RenderToolbar();
        try
        {
            if (await _actions.EditAsync(id, text) is { } updated)
            {
                _savedText = updated.DisplayText;
                _records = _records.Select(record => record.Id == id ? updated : record).ToArray();
                _notice.Text = Loc.T("Saved.");
            }
            else _notice.Text = Loc.T("The entry could not be saved. It may have been deleted; your text is still here.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("History save failed: {0}", ex);
            _notice.Text = Loc.T("The entry could not be saved. Your text is still here; try again.");
        }
        finally
        {
            _saving = false;
            RenderToolbar();
        }
    }

    // Asks before discarding edits. Returns false when the user cancels.
    private async Task<bool> ConfirmLeaveEditAsync()
    {
        if (!Dirty || _closing) return true;
        if (_dialogOpen) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Loc.T("Save Changes?"), Content = Loc.T("You edited this entry. Save your changes before leaving it?"),
            PrimaryButtonText = Loc.T("Save"), SecondaryButtonText = Loc.T("Discard"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary,
            SecondaryButtonStyle = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DestructiveConfirmButtonStyle"]
        };
        _dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary)
        {
            await SaveAsync();
            if (Dirty) return false;
        }
        _editor = null;
        _editedId = null;
        return true;
    }
}
