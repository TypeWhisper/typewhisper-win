using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// Groups that modules add to the sidebar. A group lists its newest entries below its name, more on request, and the
// detail pane, which then takes the list's place, shows the module's view of the chosen entry.
public sealed partial class HistoryWindow
{
    // Entries a group shows in the sidebar until the user asks for more.
    private const int GroupEntriesShown = 5;
    private ModuleHistorySidebarGroup? _group;
    private string? _groupEntry;
    // Whether the chosen entry was listed; an entry that was listed and no longer is was deleted.
    private bool _groupEntryListed;
    private IReadOnlyList<ModuleHistorySidebarEntry> _groupEntries = [];
    // The group and entry whose view the detail pane shows, so a refresh keeps the view and its state.
    private (string Group, string? Entry)? _groupView;
    // Groups whose entries the sidebar shows all of.
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
    // The "Show more" button of each group as last drawn.
    private readonly Dictionary<string, HandCursorButton> _showMoreRows = new(StringComparer.Ordinal);
    // The row of the chosen entry as last drawn, so keyboard selection can scroll it into view.
    private Grid? _chosenGroupRow;
    // The buttons of the group names and entries as last drawn, so the keyboard can be put back after a deletion.
    private readonly Dictionary<(string Group, string? Entry), Control> _groupRowButtons = [];

    private static IReadOnlyList<ModuleHistorySidebarGroup> SidebarGroups => HostModules.ActiveCatalog.HistorySidebarGroups;

    private void SubscribeGroups()
    {
        foreach (var group in SidebarGroups) group.Changed += GroupChanged;
        Closed += (_, _) =>
        {
            foreach (var group in SidebarGroups) group.Changed -= GroupChanged;
        };
    }

    private void GroupChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        if (_group is { } group && !IsShown(group))
        {
            LeaveGroup();
            Render();
            return;
        }
        if (_group is not null) Refilter();
        else RenderSidebar();
    });

    private static void ReportGroupFailure(ModuleHistorySidebarGroup group, string stage, Exception ex) =>
        AppDiagnostics.WriteFailure($"modules.history-group.{stage}.failed id={group.Id}", ex);

    private static bool IsShown(ModuleHistorySidebarGroup group)
    {
        try { return group.IsVisible?.Invoke() ?? true; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportGroupFailure(group, "visible", ex);
            return false;
        }
    }

    private static IReadOnlyList<ModuleHistorySidebarEntry> EntriesOf(ModuleHistorySidebarGroup group) => TryEntriesOf(group) ?? [];

    // Null when the module could not list its entries, which says nothing about whether an entry still exists.
    private static IReadOnlyList<ModuleHistorySidebarEntry>? TryEntriesOf(ModuleHistorySidebarGroup group)
    {
        try { return [.. (group.Entries() ?? []).Where(entry => entry is not null && !string.IsNullOrEmpty(entry.Id))]; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportGroupFailure(group, "entries", ex);
            return null;
        }
    }

    // The list column gives way to the detail pane while a group is chosen.
    private void ShowListColumn(bool show)
    {
        _listColumn.Width = new GridLength(show ? 380 : 0);
        _listHeaderSurface.Visibility = _listSurface.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderGroupRows()
    {
        var groups = SidebarGroups.Where(IsShown).ToArray();
        if (groups.Length == 0) return;
        _sidebar.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new Thickness(10, 12, 10, 4) });
        _chosenGroupRow = null;
        _groupRowButtons.Clear();
        foreach (var group in groups)
        {
            _sidebar.Children.Add(GroupHeader(group));
            var entries = EntriesOf(group);
            var chosen = ReferenceEquals(_group, group) ? _groupEntry : null;
            var expanded = _expandedGroups.Contains(group.Id);
            foreach (var entry in expanded ? entries : entries.Take(GroupEntriesShown))
            {
                var row = GroupEntryRow(group, entry, entry.Id == chosen);
                if (entry.Id == chosen) _chosenGroupRow = row;
                _sidebar.Children.Add(row);
            }
            if (entries.Count > GroupEntriesShown) _sidebar.Children.Add(ShowMoreRow(group, expanded));
        }
    }

    // The group's name, which starts a new entry when the group offers that, and its new-entry button.
    private Grid GroupHeader(ModuleHistorySidebarGroup group)
    {
        // Chosen while nothing it lists is open, such as a new entry the module does not list yet.
        var selected = ReferenceEquals(_group, group) && !_groupEntryListed;
        var icon = new FontIcon { Glyph = group.Glyph, FontSize = 16, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        if (selected) icon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var row = SidebarRow(icon, group.Title, 0, selected, 0, () => StartGroupEntry(group));
        _groupRowButtons[(group.Id, null)] = row;
        Grid.SetColumnSpan(row, 2);
        header.Children.Add(row);
        if (group.CreateNew is not null)
        {
            var title = NewTitle(group);
            var add = SidebarIconButton("", title, selected);
            add.Margin = new Thickness(0, 0, 4, 0);
            add.Click += (_, _) => StartGroupEntry(group);
            Grid.SetColumn(add, 1);
            header.Children.Add(add);
        }
        return header;
    }

    private static string NewTitle(ModuleHistorySidebarGroup group) =>
        string.IsNullOrWhiteSpace(group.NewTitle) ? Loc.T("New") : group.NewTitle;

    // An entry below its group's name, with a menu of its actions on the right while the pointer or focus is on it.
    private Grid GroupEntryRow(ModuleHistorySidebarGroup group, ModuleHistorySidebarEntry entry, bool selected)
    {
        var titleText = entry.Title.Length == 0 ? Loc.T("Untitled") : entry.Title;
        var title = Text(titleText, 13, bold: selected);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        if (selected) title.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var button = new HandCursorButton
        {
            Content = title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32, Padding = new Thickness(38, 6, group.Delete is null ? 10 : 36, 6),
            Style = (Style)Application.Current.Resources[selected ? "SidebarSelectedButtonStyle" : "MenuButtonStyle"]
        };
        var details = string.Join(", ", new[] { titleText, entry.Subtitle, Elapsed(entry.Date.UtcDateTime, DateTimeOffset.Now) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        ToolTipService.SetToolTip(button, details);
        AutomationProperties.SetName(button, details);
        AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
        button.Click += (_, _) => OpenGroupEntry(group, entry.Id, listed: true);
        _groupRowButtons[(group.Id, entry.Id)] = button;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumnSpan(button, 2);
        row.Children.Add(button);
        if (group.Delete is null) return row;

        var menu = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = Loc.T("Delete…"), Icon = new FontIcon { Glyph = "" } };
        delete.Click += (_, _) => _ = DeleteGroupEntryAsync(group, entry);
        menu.Items.Add(delete);
        button.ContextFlyout = menu;
        var more = SidebarIconButton("", Loc.T("More actions"), selected);
        // Each menu button says which entry it belongs to.
        AutomationProperties.SetName(more, string.Join(", ", Loc.T("More actions"), titleText));
        more.Margin = new Thickness(0, 0, 4, 0);
        more.Flyout = menu;
        Grid.SetColumn(more, 1);
        row.Children.Add(more);
        // Shown for the chosen entry, and for another while the pointer is over it or the keyboard is on it.
        var hovered = false;
        var focused = false;
        void Update()
        {
            var shown = selected || hovered || focused || menu.IsOpen;
            more.Opacity = shown ? 1 : 0;
            // A tap on a hidden button opens the entry, as on touch, where nothing hovers.
            more.IsHitTestVisible = shown;
        }
        Update();
        row.PointerEntered += (_, _) => { hovered = true; Update(); };
        row.PointerExited += (_, _) => { hovered = false; Update(); };
        row.GotFocus += (_, _) => { focused = true; Update(); };
        // Also raised when the focus moves between the row's own buttons.
        row.LostFocus += (_, _) => { focused = HasFocus(row); Update(); };
        menu.Closed += (_, _) => Update();
        return row;
    }

    private bool HasFocus(UIElement element)
    {
        // Before the window is shown there is no focus to ask about.
        if (Content?.XamlRoot is not { } root) return false;
        for (var focused = FocusManager.GetFocusedElement(root) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, element)) return true;
        return false;
    }

    private static HandCursorButton SidebarIconButton(string glyph, string name, bool onSelected)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
        if (onSelected) icon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var button = new HandCursorButton
        {
            Content = icon, Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["MenuButtonStyle"]
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private HandCursorButton ShowMoreRow(ModuleHistorySidebarGroup group, bool expanded)
    {
        var label = Text(expanded ? Loc.T("Show less") : Loc.T("Show more"), 13, muted: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        var button = new HandCursorButton
        {
            Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 32, Padding = new Thickness(38, 6, 10, 6), Style = (Style)Application.Current.Resources["MenuButtonStyle"]
        };
        AutomationProperties.SetName(button, label.Text);
        button.Click += (_, _) =>
        {
            // Collapsing may hide an older chosen entry from the sidebar; it stays open in the detail pane.
            if (!_expandedGroups.Remove(group.Id)) _expandedGroups.Add(group.Id);
            RenderSidebar();
            // The keyboard stays on the button, which the sidebar has drawn anew.
            if (_showMoreRows.TryGetValue(group.Id, out var drawn)) drawn.Focus(FocusState.Keyboard);
        };
        _showMoreRows[group.Id] = button;
        return button;
    }

    // Chooses the group, leaving History's own scopes; false when the user kept editing an entry.
    private async Task<bool> EnterGroupAsync(ModuleHistorySidebarGroup group)
    {
        if (ReferenceEquals(group, _group)) return true;
        if (!await ConfirmLeaveEditAsync() || _closing) return false;
        StopAudioPlayback();
        _group = group;
        _groupEntry = null;
        _groupEntryListed = false;
        _groupView = null;
        _selection.Clear();
        _anchor = null;
        _notice.Text = "";
        return true;
    }

    // Starts a new entry, when the group offers that, and opens it.
    private async void StartGroupEntry(ModuleHistorySidebarGroup group)
    {
        if (!await EnterGroupAsync(group)) return;
        if (group.CreateNew is { } create)
        {
            string? id = null;
            try { id = create(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ReportGroupFailure(group, "new", ex); }
            if (id is not null)
            {
                // The module may list it only once it holds something; a refresh then keeps it chosen.
                _groupView = null;
                _groupEntry = id;
                _groupEntryListed = EntriesOf(group).Any(entry => entry.Id == id);
                _notice.Text = "";
            }
        }
        Refilter();
    }

    private async void OpenGroupEntry(ModuleHistorySidebarGroup group, string id, bool listed)
    {
        if (ReferenceEquals(group, _group) && id == _groupEntry) return;
        var entering = !ReferenceEquals(group, _group);
        if (!await EnterGroupAsync(group)) return;
        SelectGroupEntry(group, id, listed);
        // Entering hides the list column, clears History's toolbar and lists the entries for the keyboard.
        if (entering) Refilter();
    }

    // Back to History's own scopes; the caller draws again.
    private void LeaveGroup()
    {
        _group = null;
        _groupEntry = null;
        _groupEntryListed = false;
        _groupEntries = [];
        _groupView = null;
        _filterButton.IsEnabled = _sortButton.IsEnabled = true;
    }

    private void RefilterGroup(ModuleHistorySidebarGroup group)
    {
        // Search narrows History's own list, which is hidden while a group is chosen.
        _filteredSearch = _search.Text;
        var listed = TryEntriesOf(group);
        _groupEntries = listed ?? [];
        // Only a list the module could give tells that the chosen entry is gone; after a failure its view stays.
        if (_groupEntry is { } chosen && listed is not null)
        {
            var isListed = listed.Any(entry => entry.Id == chosen);
            // Deleted, also by the module itself: the view goes with it.
            if (_groupEntryListed && !isListed) _groupEntry = null;
            else _groupEntryListed = isListed;
        }
        _filterButton.IsEnabled = _sortButton.IsEnabled = false;
        RenderGroupDetail(group);
        RenderGroupToolbar(group);
        RenderSidebar();
    }

    private void SelectGroupEntry(ModuleHistorySidebarGroup group, string? id, bool listed)
    {
        if (!ReferenceEquals(group, _group)) return;
        if (id == _groupEntry && _groupView is not null) return;
        _groupEntry = id;
        _groupEntryListed = listed;
        _notice.Text = "";
        // An entry chosen with the keyboard may be older than those the sidebar shows first.
        if (id is not null && EntriesOf(group).Select(entry => entry.Id).ToList().IndexOf(id) >= GroupEntriesShown) _expandedGroups.Add(group.Id);
        RenderGroupDetail(group);
        RenderSidebar();
    }

    private void MoveGroupSelection(ModuleHistorySidebarGroup group, int offset)
    {
        if (_groupEntries.Count == 0) return;
        var index = _groupEntry is { } chosen ? _groupEntries.ToList().FindIndex(entry => entry.Id == chosen) : -1;
        index = index < 0 ? (offset > 0 ? 0 : _groupEntries.Count - 1) : Math.Clamp(index + offset, 0, _groupEntries.Count - 1);
        SelectGroupEntry(group, _groupEntries[index].Id, listed: true);
        BringChosenGroupRowIntoView();
    }

    // The sidebar scrolls; an entry chosen with the keyboard may lie outside its viewport.
    private void BringChosenGroupRowIntoView() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || _chosenGroupRow is not { } row) return;
        row.UpdateLayout();
        row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    });

    private void RenderGroupDetail(ModuleHistorySidebarGroup group)
    {
        if (_groupView is { } shown && shown.Group == group.Id && shown.Entry == _groupEntry) return;
        _groupView = (group.Id, _groupEntry);
        _editor = null;
        _editedId = null;
        if (_groupEntry is not { } id)
        {
            _detailHost.Child = EmptyState("file", Loc.T("Select an Entry"), Loc.T("Choose an entry to open it."));
            return;
        }
        try { _detailHost.Child = group.CreateView(id); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportGroupFailure(group, "view", ex);
            _detailHost.Child = EmptyState("info", Loc.T("Entry Unavailable"), Loc.T("This entry could not be shown."));
            // The next refresh tries again; only a view that was created is kept.
            _groupView = null;
        }
    }

    // A group's actions sit in the sidebar, next to its name and its entries.
    private void RenderGroupToolbar(ModuleHistorySidebarGroup group) => _toolbarActions.Children.Clear();

    private async Task DeleteGroupEntryAsync(ModuleHistorySidebarGroup group, ModuleHistorySidebarEntry entry)
    {
        if (group.Delete is not { } delete || _dialogOpen) return;
        _dialogOpen = true;
        bool confirmed;
        try
        {
            confirmed = await Dialogs.ConfirmAsync((FrameworkElement)Content, Loc.T("Delete this entry?"),
                Loc.T("“{0}” is removed from this PC. This cannot be undone.", entry.Title.Length == 0 ? Loc.T("Untitled") : entry.Title),
                Loc.T("Delete"), destructive: true);
        }
        finally { _dialogOpen = false; }
        if (!confirmed || _closing) return;
        // The entry that takes the deleted one's place in the sidebar, or the one above it at the end.
        var entries = EntriesOf(group).Select(listed => listed.Id).ToList();
        var index = entries.IndexOf(entry.Id);
        var neighbor = index < 0 ? null : index + 1 < entries.Count ? entries[index + 1] : index > 0 ? entries[index - 1] : null;
        bool deleted;
        try { deleted = delete(entry.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportGroupFailure(group, "delete", ex);
            deleted = false;
        }
        _notice.Text = deleted ? Loc.T("Entry deleted.") : Loc.T("The entry could not be deleted.");
        if (deleted && ReferenceEquals(group, _group) && entry.Id == _groupEntry)
        {
            _groupEntry = null;
            _groupEntryListed = false;
        }
        if (ReferenceEquals(group, _group)) RefilterGroup(group);
        else RenderSidebar();
        // The row whose menu or key deleted it is gone; the keyboard stays in the sidebar. After the redraw a module's
        // NotifyChanged from within Delete has queued, which replaces the buttons again.
        if (deleted) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closing || FocusInDetail()) return;
            var target = (neighbor is not null ? _groupRowButtons.GetValueOrDefault((group.Id, neighbor)) : null)
                ?? _groupRowButtons.GetValueOrDefault((group.Id, null));
            target?.Focus(FocusState.Programmatic);
        });
    }

    // Whether the keyboard focus is in the module's view, whose keys belong to it.
    private bool FocusInDetail() => HasFocus(_detailHost);
}
