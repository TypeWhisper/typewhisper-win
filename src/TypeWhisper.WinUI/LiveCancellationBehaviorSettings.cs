using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveCancellationBehaviorSettings
{
    private const string Hint = "Double: press Esc twice to cancel. Single: press Esc once. Both show a cancellation banner for 1.5 seconds. Instant: press Esc once without a banner. Applies to recording and processing.";

    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers, LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = FindRow(content) ?? throw new InvalidOperationException("Cancellation behavior settings row is missing.");
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.Children.Add(SettingsHelp.Label("Cancellation behavior", Hint));
        var picker = new ChoicePicker();
        picker.Configure("Cancellation behavior", "microphone", "Cancellation behavior");
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void Refresh()
        {
            picker.SetOptions([
                new("Double", "Double", "Press Esc twice within 3 seconds to cancel."),
                new("Single", "Single", "Press Esc once to cancel."),
                new("Instant", "Instant", "Press Esc once to cancel, without a banner.")
            ], session.EscapeCancelPreferences.Current.ToString());
            status.Text = session.EscapeCancelPreferences.Error ?? "Saved for this profile. Esc reaches the focused app while TypeWhisper is idle.";
        }
        picker.SelectionChanged += id =>
        {
            if (Enum.TryParse<EscapeCancelBehavior>(id, out var behavior)) session.SelectEscapeCancelBehavior(behavior);
            Refresh();
        };
        row.Children.Add(picker); row.Children.Add(status); pickers.Add(picker);
        Refresh();
    }

    private static StackPanel? FindRow(StackPanel root)
    {
        if (Equals(root.Tag, "CancellationBehavior")) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child) is { } row) return row;
        return null;
    }
}
