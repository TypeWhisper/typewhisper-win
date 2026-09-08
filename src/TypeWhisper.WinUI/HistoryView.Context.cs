using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

public sealed partial class HistoryView
{
    private IEnumerable<EntryActionMenu.Action> HistoryContextActions()
    {
        if (IsReading || _selecting || Entries.SelectedItem is not Transcript entry)
            return EntryActionMenu.FromButtons(ContextActionsFooter);
        void Run(System.Action action)
        {
            if (_closing || _acting || _loading) return;
            Entries.SelectedItem = entry;
            OpenSelected();
            if (_opened == entry) action();
        }
        var enabled = !_closing && !_acting && !_loading;
        return [
            new("Open · Enter", () => Run(() => { }), enabled),
            new("Copy text", () => Run(() => Copy_Click(this, new RoutedEventArgs())), enabled),
            new("Edit text · E", () => Run(() => Edit_Click(this, new RoutedEventArgs())), enabled && entry.Entry.PersistedRecordId is not null),
            new("Export… · X", () => Run(() => Export_Click(this, new RoutedEventArgs())), enabled),
            new("Delete… · Del", () => Run(() => Delete_Click(this, new RoutedEventArgs())), enabled && entry.Entry.PersistedRecordId is not null)
        ];
    }
}
