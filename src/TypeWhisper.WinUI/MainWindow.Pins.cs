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
    private List<string> _pinOrder = [];
    private string? _draggedPin;
    private HashSet<string> _pinnedCommands = new(StringComparer.Ordinal);
    private static string LauncherPinsPath => WinUIProfile.DataPath("quick-launch-pins.json");

    private void LoadLauncherPins()
    {
        LoadCommandShortcuts();
        ConfigurePinDragging();
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
                : Commands.Where(command => command.Category == "Pinned").Select(command => command.Key).ToList();
            _pinnedCommands = _pinOrder.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Debug.WriteLine(error); }
    }

    private IEnumerable<EntryActionMenu.Action> LauncherActions()
    {
        if (_selected is not { } command) yield break;
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

    private void ConfigurePinDragging()
    {
        CompactResults.CanDragItems = true;
        CompactResults.AllowDrop = true;
        CompactResults.DragItemsStarting += (_, e) =>
        {
            _draggedPin = e.Items.OfType<Command>().FirstOrDefault()?.Key;
            if (_draggedPin is null || !_pinnedCommands.Contains(_draggedPin))
            { _draggedPin = null; e.Cancel = true; return; }
            e.Data.Properties["TypeWhisper.Pin"] = _draggedPin;
            e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        CompactResults.DragItemsCompleted += (_, _) => _draggedPin = null;
        Microsoft.UI.Xaml.Controls.ListViewItem? Row(object source)
        {
            for (var node = source as Microsoft.UI.Xaml.DependencyObject; node is not null && node != CompactResults;
                 node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (node is Microsoft.UI.Xaml.Controls.ListViewItem row) return row;
            return null;
        }
        CompactResults.DragOver += (_, e) =>
        {
            var row = Row(e.OriginalSource);
            var target = row is null ? null : CompactResults.ItemFromContainer(row) as Command;
            e.AcceptedOperation = _draggedPin is not null && target is not null && _pinnedCommands.Contains(target.Key)
                ? global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
                : global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            e.Handled = true;
        };
        CompactResults.Drop += (_, e) =>
        {
            var row = Row(e.OriginalSource);
            if (_draggedPin is { } key && row is not null && CompactResults.ItemFromContainer(row) is Command target)
                MovePin(key, target.Key, e.GetPosition(row).Y > row.ActualHeight / 2);
            _draggedPin = null;
            e.Handled = true;
        };
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
