using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal static class LiveOutputSettings
{
    internal static void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers,
        LocalDictationSession session)
    {
        var store = session.OutputPreferences;
        if (category == "Dictation")
        {
            var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "AutoPaste"));
            foreach (var old in row.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
            row.Children.Clear();
            row.Children.Add(Label("After recording", 14));
            var picker = new PrototypeChoicePicker();
            picker.Configure("After recording", "text", "Preference AutoPaste");
            void Refresh() => picker.SetOptions([
                new("On", "Insert directly", "Paste the finished text into the original app."),
                new("Off", "Review first", "Open the result for review and copying. No automatic paste.")
            ], store.Current.AutoPaste ? "On" : "Off");
            Refresh();
            var status = Label(store.Error ?? "Saved. Review first also works when history saving is off.");
            picker.SelectionChanged += id =>
            {
                status.Text = store.Save(store.Current with { AutoPaste = id == "On" })
                    ?? "Saved. Turning automatic paste off also applies to a dictation in progress.";
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
        var toggle = PrototypeToggleSwitch.Create(store.Current.SaveToHistory);
        AutomationProperties.SetName(toggle, "Save to history");
        saveRow.Children.Add(Label("Save to history", 14));
        saveRow.Children.Add(Label("Keep new dictation results on this device. Turning this off leaves existing history unchanged."));
        saveRow.Children.Add(toggle);
        var hint = Label(store.Error ?? "Saved. When off, results can still be pasted or reviewed without a history entry.");
        saveRow.Children.Add(hint);
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            hint.Text = store.Save(store.Current with { SaveToHistory = toggle.IsOn })
                ?? "Saved. Turning history off also applies to a dictation in progress that has not been saved yet.";
            restoring = true; toggle.IsOn = store.Current.SaveToHistory; restoring = false;
        };
        foreach (var key in new[] { "HistoryRetentionMode", "HistoryRetentionMinutes", "MemoryEnabled", "TargetAppCorrectionLearningEnabled" })
            DisablePreview(content, key);
        content.Children.Add(Label("Automatic history deletion, personal memory and correction learning are not available yet."));
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
