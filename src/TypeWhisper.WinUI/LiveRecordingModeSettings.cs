using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveRecordingModeSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = FindModeRow(content) ?? throw new InvalidOperationException("Recording mode settings row is missing.");
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.Children.Add(SettingsHelp.Label("Recording mode",
            "Toggle starts and stops with a press. Hold records while the shortcut is held. Hybrid toggles on a tap, or stops on release after a hold of at least 300 ms."));
        var picker = new ChoicePicker();
        picker.Configure("Recording mode", "microphone", "Recording mode");
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        string? selectionError = null;
        void Refresh()
        {
            picker.SetOptions([
                new("Hybrid", "Hybrid", "Tap to toggle. Hold for at least 300 ms, then release to stop."),
                new("Toggle", "Toggle", "Press once to start and again to stop."),
                new("Hold", "Hold to record", "Record while the shortcut is held. Release to stop.")
            ], session.RecordingModePreferences.Current.ToString());
            picker.IsEnabled = session.CanChangeProvider;
            hint.Text = selectionError ?? session.RecordingModePreferences.Error ??
                (session.CanChangeProvider ? "Saved for this profile. Release all shortcut keys before using a new mode."
                    : "Finish or cancel the current dictation before changing recording mode.");
        }
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() => { if (row.IsLoaded) Refresh(); });
        picker.SelectionChanged += id =>
        {
            if (Enum.TryParse<RecordingMode>(id, out var mode)) selectionError = session.SelectRecordingMode(mode);
            Refresh();
        };
        row.Loaded += (_, _) => { session.Changed += OnChanged; Refresh(); };
        row.Unloaded += (_, _) => session.Changed -= OnChanged;
        row.Children.Add(picker); row.Children.Add(hint); pickers.Add(picker);
        Refresh();
    }

    private static StackPanel? FindModeRow(StackPanel root)
    {
        if (Equals(root.Tag, "Mode")) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindModeRow(child) is { } row) return row;
        return null;
    }
}
