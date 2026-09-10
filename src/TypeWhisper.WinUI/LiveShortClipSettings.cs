using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

internal static class LiveShortClipSettings
{
    internal static void Configure(string category, StackPanel content, LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = FindRow(content) ?? throw new InvalidOperationException("Short clip settings row is missing.");
        row.Children.Clear();
        var store = session.TextPreferences;
        var toggle = AppToggleSwitch.Create(store.Current.TranscribeShortQuietClipsAggressively);
        AutomationProperties.SetName(toggle, "Recognize short, quiet clips");
        row.Children.Add(SettingsHelp.Label("Recognize short, quiet clips",
            "Enable to transcribe very quiet audio; silence may produce unwanted text. Clips shorter than 40 ms are always skipped. Changes apply to the next recording."));
        row.Children.Add(toggle);
        var status = new TextBlock { Text = store.Error ?? "Saved for the next recording.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        row.Children.Add(status);
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            store.Save(store.Current with { TranscribeShortQuietClipsAggressively = toggle.IsOn });
            restoring = true;
            toggle.IsOn = store.Current.TranscribeShortQuietClipsAggressively;
            restoring = false;
            status.Text = store.Error ?? "Saved for the next recording.";
        };
    }

    private static StackPanel? FindRow(StackPanel root)
    {
        if (Equals(root.Tag, "TranscribeShortQuietClipsAggressively")) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child) is { } row) return row;
        return null;
    }
}
