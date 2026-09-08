using System.Text.Json;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Data;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<LauncherCommandGroup> _launcherGroups = [];
    private readonly CollectionViewSource _launcherSource = new() { IsSourceGrouped = true, ItemsPath = new Microsoft.UI.Xaml.PropertyPath("Items") };
    private Dictionary<string, QuickLaunchUsage> _launcherUsage = new(StringComparer.Ordinal);
    private static string LauncherUsagePath => WinUIProfile.DataPath("quick-launch-usage.json");
    private bool _suggestionsExpanded;
    private List<string> _pinOrder = [];
    private string? _draggedPin;
    private HashSet<string> _pinnedCommands = new(StringComparer.Ordinal);
    private static string LauncherPinsPath => WinUIProfile.DataPath("quick-launch-pins.json");

    private void LoadLauncherPins()
    {
        LoadCommandShortcuts();
        CompactResults.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer is not null) e.ItemContainer.MinHeight = e.Item is Command { IsSuggestionsToggle: true } ? 30 : 50;
        };
        _launcherSource.Source = _launcherGroups;
        CompactResults.ItemsSource = _launcherSource.View;
        CompactResults.PointerWheelChanged += (_, e) =>
        {
            // Group headers and custom row templates can leave wheel input unhandled.
            // Route those events to this list's own scroll viewer.
            if (e.Handled) return;
            Microsoft.UI.Xaml.Controls.ScrollViewer? FindScroll(Microsoft.UI.Xaml.DependencyObject node)
            {
                if (node is Microsoft.UI.Xaml.Controls.ScrollViewer scroll) return scroll;
                for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    if (FindScroll(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i)) is { } found) return found;
                return null;
            }
            if (FindScroll(CompactResults) is not { ScrollableHeight: > 0 } viewer) return;
            var delta = e.GetCurrentPoint(CompactResults).Properties.MouseWheelDelta;
            if (delta == 0) return;
            viewer.ChangeView(null, Math.Clamp(viewer.VerticalOffset - delta, 0, viewer.ScrollableHeight), null, disableAnimation: true);
            e.Handled = true;
        };
        try
        {
            if (File.Exists(LauncherUsagePath))
                _launcherUsage = JsonSerializer.Deserialize<Dictionary<string, QuickLaunchUsage>>(File.ReadAllText(LauncherUsagePath)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Debug.WriteLine(error); }

        try
        {
            _pinOrder = File.Exists(LauncherPinsPath)
                ? (JsonSerializer.Deserialize<string[]>(File.ReadAllText(LauncherPinsPath)) ?? []).Distinct(StringComparer.Ordinal).ToList()
                : ["Settings", "History", "Dictionary"];
            _pinnedCommands = _pinOrder.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Debug.WriteLine(error); }
    }

    private IEnumerable<EntryActionMenu.Action> LauncherActions()
    {
        if (_selected is not { } command || command.IsSuggestionsToggle) yield break;
        yield return new("Run command · Enter", () => { _selected = command; RunSelected(); });
        yield return new("Set shortcut…", () => { _ = ConfigureCommandShortcutAsync(command); });
        yield return new(_pinnedCommands.Contains(command.Key) ? "Unpin from Quick Launch" : "Pin to Quick Launch",
            () => ToggleLauncherPin(command));
        if (_pinnedCommands.Contains(command.Key))
        {
            var pins = OrderedLauncherCommands(LauncherCommands()).Where(item => item.IsPinned).ToArray();
            var index = Array.FindIndex(pins, item => item.Key == command.Key);
            yield return new("Move up", () => MovePin(command.Key, pins[index - 1].Key, false), index > 0);
            yield return new("Move down", () => MovePin(command.Key, pins[index + 1].Key, true), index >= 0 && index < pins.Length - 1);
        }
    }

    private void ToggleLauncherPin(Command command)
    {
        var next = new HashSet<string>(_pinnedCommands, StringComparer.Ordinal);
        if (!next.Remove(command.Key)) next.Add(command.Key);
        var order = _pinOrder.Where(next.Contains).ToList();
        if (next.Contains(command.Key) && !order.Contains(command.Key)) order.Add(command.Key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherPinsPath)!);
            var temporary = LauncherPinsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(order));
            File.Move(temporary, LauncherPinsPath, overwrite: true);
            _pinnedCommands = next;
            _pinOrder = order;
            if (LauncherCommandsVisible)
            {
                SearchBox_TextChanged(SearchBox, null!);
                CompactResults.SelectedItem = FilteredItems.FirstOrDefault(item => item.Key == command.Key);
            }
            MetricsText.Text = next.Contains(command.Key) ? "Pinned to Quick Launch." : "Removed from pinned commands.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { MetricsText.Text = "The pin could not be saved. Please try again."; }
    }

    private void MovePin(string key, string target, bool after)
    {
        if (key == target || !_pinnedCommands.Contains(key) || !_pinnedCommands.Contains(target)) return;
        var next = _pinOrder.Where(id => id != key).ToList();
        var index = next.IndexOf(target);
        if (index < 0) return;
        next.Insert(index + (after ? 1 : 0), key);
        try
        {
            var temporary = LauncherPinsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, LauncherPinsPath, overwrite: true);
            _pinOrder = next;
            SearchBox_TextChanged(SearchBox, null!);
            CompactResults.SelectedItem = FilteredItems.FirstOrDefault(item => item.Key == key);
            MetricsText.Text = "Pinned order saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { MetricsText.Text = "The order could not be saved. Please try again."; }
    }

    private global::Windows.Foundation.Point _pinDragOrigin;
    private bool _pinDragMoved;

    private void Pin_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement { DataContext: Command command } row ||
            !_pinnedCommands.Contains(command.Key) || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        _draggedPin = command.Key;
        _pinDragMoved = false;
        _pinDragOrigin = e.GetCurrentPoint(CompactResults).Position;
        row.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private (Command Command, bool After)? PinTarget(global::Windows.Foundation.Point point)
    {
        for (var index = 0; index < CompactResults.Items.Count; index++)
        {
            if (CompactResults.ContainerFromIndex(index) is not Microsoft.UI.Xaml.Controls.ListViewItem row ||
                CompactResults.ItemFromContainer(row) is not Command target || !_pinnedCommands.Contains(target.Key)) continue;
            var bounds = row.TransformToVisual(CompactResults).TransformBounds(
                new global::Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
            if (bounds.Contains(point)) return (target, point.Y > bounds.Y + bounds.Height / 2);
        }
        return null;
    }

    private void Pin_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_draggedPin is null) return;
        var point = e.GetCurrentPoint(CompactResults);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (Math.Abs(point.Position.Y - _pinDragOrigin.Y) < 6 && Math.Abs(point.Position.X - _pinDragOrigin.X) < 6 && !_pinDragMoved) return;
        _pinDragMoved = true;
        if (PinTarget(point.Position) is { } target)
            MetricsText.Text = (target.After ? "Move below " : "Move above ") + target.Command.Title;
        e.Handled = true;
    }

    private void Pin_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var key = _draggedPin;
        var moved = _pinDragMoved;
        var target = PinTarget(e.GetCurrentPoint(CompactResults).Position);
        _draggedPin = null;
        if (sender is Microsoft.UI.Xaml.UIElement row) row.ReleasePointerCapture(e.Pointer);
        if (key is null) return;
        if (!moved)
        {
            e.Handled = true;
            if (target?.Command.Key == key)
            {
                _selected = target.Value.Command;
                RunSelected();
            }
            return;
        }
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (target is { } destination) MovePin(key, destination.Command.Key, destination.After);
            _pinDragMoved = false;
        });
    }

    private void Pin_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _draggedPin = null;
    }

    private void PinRow_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement row || row.Tag is true) return;
        row.Tag = true;
        row.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pin_PointerPressed), true);
        row.AddHandler(Microsoft.UI.Xaml.UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pin_PointerMoved), true);
        row.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pin_PointerReleased), true);
        row.PointerCaptureLost += Pin_PointerCaptureLost;
    }

    private IEnumerable<Command> LauncherCommands() => Commands.Concat(WorkflowsView.LauncherEntries
        .Where(command => _pinnedCommands.Contains(command.Key)));

    private IEnumerable<Command> OrderedLauncherCommands(IEnumerable<Command> commands)
    {
        var candidates = commands.ToArray();
        var byTitle = candidates.ToDictionary(command => command.Key, StringComparer.Ordinal);
        return QuickLaunchRanking.Order(candidates.Select(command => command.Key), _pinnedCommands, _launcherUsage,
                !string.IsNullOrWhiteSpace(SearchBox.Text), _pinOrder)
            .Select(title => byTitle[title] with { IsPinned = _pinnedCommands.Contains(title), Shortcut = CommandShortcut(byTitle[title]) });
    }

    private void PopulateLauncherItems(IEnumerable<Command> candidates)
    {
        var ordered = OrderedLauncherCommands(candidates).ToArray();
        FilteredItems.Clear();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            foreach (var command in ordered) FilteredItems.Add(command);
            return;
        }
        foreach (var command in ordered.Where(command => command.IsPinned)) FilteredItems.Add(command);
        var others = ordered.Where(command => !command.IsPinned).ToArray();
        if (others.Length == 0) return;
        FilteredItems.Add(new Command("Suggestions", _suggestionsExpanded ? "chevron-up" : "chevron-down",
            "Suggestions", "", "", "")
            { IsSuggestionsToggle = true });
        if (_suggestionsExpanded)
            foreach (var command in others) FilteredItems.Add(command);
    }

    private void ToggleSuggestions()
    {
        _suggestionsExpanded = !_suggestionsExpanded;
        SearchBox_TextChanged(SearchBox, null!);
        CompactResults.SelectedItem = FilteredItems.FirstOrDefault(command => command.IsSuggestionsToggle);
        CompactResults.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
    }

    private void RebuildLauncherGroups()
    {
        _launcherGroups.Clear();
        var pinned = FilteredItems.Where(command => command.IsPinned).ToArray();
        var others = FilteredItems.Where(command => !command.IsPinned).ToArray();
        if (pinned.Length > 0) _launcherGroups.Add(new("Pinned", pinned, false));
        if (others.Length > 0) _launcherGroups.Add(new(
            string.IsNullOrWhiteSpace(SearchBox.Text) ? "Suggestions" : "Results", others, pinned.Length > 0));
    }

    private void RecordLauncherUsage(Command command)
    {
        var count = _launcherUsage.GetValueOrDefault(command.Key)?.Count ?? 0;
        _launcherUsage[command.Key] = new(count == long.MaxValue ? count : count + 1, DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherUsagePath)!);
            var temporary = LauncherUsagePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_launcherUsage));
            File.Move(temporary, LauncherUsagePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine(error); }
    }
}
