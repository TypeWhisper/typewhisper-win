using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// Groups that modules add to the sidebar. While one is chosen, the list shows its entries and the detail pane the
// module's view of the chosen entry; search narrows the entries by title and subtitle.
public sealed partial class HistoryWindow
{
    private ModuleHistorySidebarGroup? _group;
    private string? _groupEntry;
    // Whether the chosen entry was listed; an entry that was listed and no longer is was deleted.
    private bool _groupEntryListed;
    private IReadOnlyList<ModuleHistorySidebarEntry> _groupEntries = [];
    // The group and entry whose view the detail pane shows, so a refresh keeps the view and its state.
    private (string Group, string? Entry)? _groupView;

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
        RenderSidebar();
        if (_group is not null) Refilter();
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

    private void RenderGroupRows()
    {
        var groups = SidebarGroups.Where(IsShown).ToArray();
        if (groups.Length == 0) return;
        _sidebar.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new Thickness(10, 12, 10, 4) });
        foreach (var group in groups)
        {
            var selected = ReferenceEquals(_group, group);
            var icon = new FontIcon { Glyph = group.Glyph, FontSize = 16, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            if (selected) icon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            _sidebar.Children.Add(SidebarRow(icon, group.Title, EntriesOf(group).Count, selected, 0, () => SelectGroup(group)));
        }
    }

    private async void SelectGroup(ModuleHistorySidebarGroup group)
    {
        if (ReferenceEquals(group, _group) || !await ConfirmLeaveEditAsync()) return;
        StopAudioPlayback();
        _group = group;
        _groupEntry = null;
        _groupEntryListed = false;
        _selection.Clear();
        _anchor = null;
        _notice.Text = "";
        RenderSidebar();
        Refilter();
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
        _filteredSearch = _search.Text;
        var search = _search.Text.Trim();
        var listed = TryEntriesOf(group);
        var all = listed ?? [];
        _groupEntries = search.Length == 0 ? all : [.. all.Where(entry =>
            entry.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
            entry.Subtitle?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)];
        // Only a list the module could give tells that the chosen entry is gone; after a failure its view stays.
        if (_groupEntry is { } chosen && listed is not null)
        {
            var isListed = all.Any(entry => entry.Id == chosen);
            // Deleted, also by the module itself: the view goes with it.
            if (_groupEntryListed && !isListed) _groupEntry = null;
            else _groupEntryListed = isListed;
        }
        _filterButton.IsEnabled = _sortButton.IsEnabled = false;
        _listTitle.Text = group.Title;
        _listCount.Text = _groupEntries.Count == 1 ? Loc.T("1 entry") : Loc.T("{0:N0} entries", _groupEntries.Count);
        RenderGroupList(group);
        RenderGroupDetail(group);
        RenderGroupToolbar(group);
    }

    private void RenderGroupList(ModuleHistorySidebarGroup group)
    {
        var rows = new List<Func<UIElement>>();
        _rowIndex.Clear();
        if (_groupEntries.Count == 0)
        {
            rows.Add(_search.Text.Trim().Length > 0
                ? () => EmptyState("search", Loc.T("No Results"), Loc.T("No entries match your search."))
                : () => EmptyState("history", Loc.T("No Entries"), ""));
        }
        var now = DateTimeOffset.Now;
        var first = true;
        foreach (var entry in _groupEntries)
        {
            if (!first) rows.Add(() => new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new Thickness(12, 0, 12, 0) });
            first = false;
            _rowIndex[entry.Id] = rows.Count;
            rows.Add(() => GroupEntryRow(group, entry, now));
        }
        _list.ItemsSource = rows;
    }

    private HandCursorButton GroupEntryRow(ModuleHistorySidebarGroup group, ModuleHistorySidebarEntry entry, DateTimeOffset now)
    {
        var selected = entry.Id == _groupEntry;
        var foreground = selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : Brush("TextBrush");
        var secondary = selected ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)) : Brush("MutedBrush");
        var content = new StackPanel { Spacing = 3 };
        var title = Text(entry.Title.Length == 0 ? Loc.T("Untitled") : entry.Title, 14, bold: true);
        title.Foreground = foreground; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(entry.Subtitle))
        {
            var subtitle = Text(entry.Subtitle, 12);
            subtitle.Foreground = secondary; subtitle.TextWrapping = TextWrapping.NoWrap; subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(subtitle);
        }
        var when = Text(Elapsed(entry.Date.UtcDateTime, now), 12);
        when.Foreground = secondary;
        content.Children.Add(when);
        var button = new HandCursorButton
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 1, 0, 1),
            Style = (Style)Application.Current.Resources[selected ? "SidebarSelectedButtonStyle" : "MenuButtonStyle"]
        };
        AutomationProperties.SetName(button, string.Join(", ", new[] { title.Text, entry.Subtitle, when.Text }.Where(part => !string.IsNullOrWhiteSpace(part))));
        AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
        button.Click += (_, _) => SelectGroupEntry(group, entry.Id, listed: true);
        if (group.Delete is not null)
        {
            var menu = new MenuFlyout();
            var delete = new MenuFlyoutItem { Text = Loc.T("Delete…") };
            delete.Click += (_, _) => _ = DeleteGroupEntryAsync(group, entry);
            menu.Items.Add(delete);
            button.ContextFlyout = menu;
        }
        return button;
    }

    private void SelectGroupEntry(ModuleHistorySidebarGroup group, string? id, bool listed)
    {
        if (!ReferenceEquals(group, _group) || id == _groupEntry) return;
        _groupEntry = id;
        _groupEntryListed = listed;
        _notice.Text = "";
        RenderGroupList(group);
        RenderGroupDetail(group);
        RenderGroupToolbar(group);
    }

    private void MoveGroupSelection(ModuleHistorySidebarGroup group, int offset)
    {
        if (_groupEntries.Count == 0) return;
        var index = _groupEntry is { } chosen ? _groupEntries.ToList().FindIndex(entry => entry.Id == chosen) : -1;
        index = index < 0 ? (offset > 0 ? 0 : _groupEntries.Count - 1) : Math.Clamp(index + offset, 0, _groupEntries.Count - 1);
        var id = _groupEntries[index].Id;
        SelectGroupEntry(group, id, listed: true);
        BringIntoView(id);
    }

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

    private void RenderGroupToolbar(ModuleHistorySidebarGroup group)
    {
        _toolbarActions.Children.Clear();
        if (group.CreateNew is { } create)
        {
            var title = string.IsNullOrWhiteSpace(group.NewTitle) ? Loc.T("New") : group.NewTitle;
            _toolbarActions.Children.Add(ActionButton(title, () =>
            {
                string? id;
                try { id = create(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ReportGroupFailure(group, "new", ex);
                    return;
                }
                if (id is null) return;
                // The module may list it only once it holds something; a refresh then keeps it chosen.
                _groupView = null;
                SelectGroupEntry(group, id, listed: EntriesOf(group).Any(entry => entry.Id == id));
            }));
        }
        if (group.Delete is not null && _groupEntryListed && _groupEntries.FirstOrDefault(entry => entry.Id == _groupEntry) is { } chosen)
        {
            var delete = ActionButton(Loc.T("Delete…"), () => _ = DeleteGroupEntryAsync(group, chosen));
            delete.Style = (Style)Application.Current.Resources["DestructiveButtonStyle"];
            _toolbarActions.Children.Add(delete);
        }
    }

    private async Task DeleteGroupEntryAsync(ModuleHistorySidebarGroup group, ModuleHistorySidebarEntry entry)
    {
        if (group.Delete is not { } delete || _dialogOpen) return;
        _dialogOpen = true;
        bool confirmed;
        try
        {
            confirmed = await Dialogs.ConfirmAsync((FrameworkElement)Content, Loc.T("Delete this entry?"),
                Loc.T("“{0}” is removed from this PC. This cannot be undone.", entry.Title), Loc.T("Delete"), destructive: true);
        }
        finally { _dialogOpen = false; }
        if (!confirmed || _closing) return;
        bool deleted;
        try { deleted = delete(entry.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportGroupFailure(group, "delete", ex);
            deleted = false;
        }
        _notice.Text = deleted ? Loc.T("Entry deleted.") : Loc.T("The entry could not be deleted.");
        if (deleted && entry.Id == _groupEntry)
        {
            _groupEntry = null;
            _groupEntryListed = false;
        }
        if (ReferenceEquals(group, _group)) RefilterGroup(group);
        RenderSidebar();
    }

    // Whether the keyboard focus is in the module's view, whose keys belong to it.
    private bool FocusInDetail()
    {
        for (var element = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, _detailHost)) return true;
        return false;
    }
}
