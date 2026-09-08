using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal static class LiveOutputSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        var store = session.OutputPreferences;
        if (category == "Dictation")
        {
            var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "AutoPaste"));
            foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
            row.Children.Clear();
            row.Children.Add(SettingsHelp.Label("After recording", "Review first works even when history saving is off. Turning automatic paste off also applies to a dictation in progress."));
            var picker = new ChoicePicker();
            picker.Configure("After recording", "text", "Preference AutoPaste");
            void Refresh() => picker.SetOptions([
                new("On", "Insert directly", "Paste the finished text into the original app."),
                new("Off", "Review first", "Open the result for review and copying. No automatic paste.")
            ], store.Current.AutoPaste ? "On" : "Off");
            Refresh();
            var status = Label(store.Error ?? "Saved.");
            picker.SelectionChanged += id =>
            {
                status.Text = store.Save(store.Current with { AutoPaste = id == "On" })
                    ?? "Saved.";
                Refresh();
            };
            row.Children.Add(picker); row.Children.Add(status); pickers.Add(picker);
            DisablePreview(content, "LockPasteToFocusedField");
        }
        if (category != "Privacy") return;
        var previewNote = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Text.StartsWith("Settings preview"));
        if (previewNote is not null) previewNote.Text = "History saving is saved for this development profile. Unavailable controls are disabled.";
        var saveRow = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "SaveToHistoryEnabled"));
        saveRow.Children.Clear();
        var toggle = AppToggleSwitch.Create(store.Current.SaveToHistory);
        AutomationProperties.SetName(toggle, "Save to history");
        saveRow.Children.Add(SettingsHelp.Label("Save to history", "Keep new dictation results on this device. Turning this off leaves existing history unchanged. Results can still be pasted or reviewed. Changes also apply to a dictation that has not been saved yet.", 14));
        saveRow.Children.Add(toggle);
        var hint = Label(store.Error ?? "Saved.");
        saveRow.Children.Add(hint);
        var audioRow = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "SaveHistoryAudio"));
        audioRow.Children.Clear();
        var audioToggle = AppToggleSwitch.Create(store.Current.SaveHistoryAudio);
        AutomationProperties.SetName(audioToggle, "Keep dictation audio in history");
        audioToggle.IsEnabled = store.Current.SaveToHistory;
        audioRow.Children.Add(SettingsHelp.Label("Keep dictation audio", "Save a local audio copy with new dictation entries so you can listen again. Audio is deleted with its history entry and follows history retention. File imports and recorder files are separate. Off by default. Turning this off keeps existing recordings and also applies to a dictation that has not been saved yet.", 14));
        audioRow.Children.Add(audioToggle);
        var audioHint = Label(store.Current.SaveToHistory ? "Saved." : "Requires Save to history.");
        audioRow.Children.Add(audioHint);
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            hint.Text = store.Save(store.Current with { SaveToHistory = toggle.IsOn })
                ?? "Saved.";
            restoring = true; toggle.IsOn = store.Current.SaveToHistory; restoring = false;
            audioToggle.IsEnabled = store.Current.SaveToHistory;
            audioHint.Text = store.Current.SaveToHistory ? "Saved." : "Requires Save to history.";
        };
        audioToggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            audioHint.Text = store.Save(store.Current with { SaveHistoryAudio = audioToggle.IsOn })
                ?? "Saved.";
            restoring = true; audioToggle.IsOn = store.Current.SaveHistoryAudio; restoring = false;
        };
        foreach (var key in new[] { "HistoryRetentionMode", "HistoryRetentionMinutes", "MemoryEnabled" })
            DisablePreview(content, key);
        content.Children.Add(Label("Automatic history deletion and personal memory are not available yet."));
    }

    private static void DisablePreview(StackPanel root, string key)
    {
        foreach (var child in root.Children.OfType<StackPanel>())
        {
            if (Equals(child.Tag, key))
            {
                child.IsHitTestVisible = false;
                child.Children.Add(Label("Not available yet."));
                DisableControls(child);
            }
            else DisablePreview(child, key);
        }
    }

    private static void DisableControls(DependencyObject root)
    {
        if (root is Control control) control.IsEnabled = false;
        // Settings are configured before loading, when visual children may not exist.
        if (root is Panel panel) foreach (var child in panel.Children) DisableControls(child);
        else if (root is Border border && border.Child is not null) DisableControls(border.Child);
    }

    private static TextBlock Label(string text, double size = 12) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[size > 12 ? "TextBrush" : "MutedBrush"]
    };
}
