using System.Text.Json;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HashSet<string> _pinnedCommands = new(StringComparer.Ordinal);
    private static string LauncherPinsPath => WinUIProfile.DataPath("quick-launch-pins.json");

    private void LoadLauncherPins()
    {
        try
        {
            _pinnedCommands = File.Exists(LauncherPinsPath)
                ? new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(LauncherPinsPath)) ?? [], StringComparer.Ordinal)
                : Commands.Where(command => command.Category == "Pinned").Select(command => command.Title).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Debug.WriteLine(error); }
    }

    private IEnumerable<EntryActionMenu.Action> LauncherActions()
    {
        if (_selected is not { } command) yield break;
        yield return new("Run command · Enter", () => { _selected = command; RunSelected(); });
        yield return new(_pinnedCommands.Contains(command.Title) ? "Unpin from Quick Launch" : "Pin to Quick Launch",
            () => ToggleLauncherPin(command));
    }

    private void ToggleLauncherPin(Command command)
    {
        var next = new HashSet<string>(_pinnedCommands, StringComparer.Ordinal);
        if (!next.Remove(command.Title)) next.Add(command.Title);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherPinsPath)!);
            var temporary = LauncherPinsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next.Order(StringComparer.Ordinal)));
            File.Move(temporary, LauncherPinsPath, overwrite: true);
            _pinnedCommands = next;
            SearchBox_TextChanged(SearchBox, null!);
            CompactResults.SelectedItem = FilteredItems.FirstOrDefault(item => item.Title == command.Title);
            MetricsText.Text = next.Contains(command.Title) ? "Pinned to Quick Launch." : "Removed from pinned commands.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { MetricsText.Text = "The pin could not be saved. Please try again."; }
    }

    private IEnumerable<Command> OrderedLauncherCommands(IEnumerable<Command> commands) =>
        commands.OrderByDescending(command => _pinnedCommands.Contains(command.Title))
            .Select(command => command with { IsPinned = _pinnedCommands.Contains(command.Title) });
}
