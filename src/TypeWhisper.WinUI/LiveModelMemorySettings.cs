using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveModelMemorySettings
{
    // Fills the Advanced page's Recording row.
    internal static void Configure(StackPanel content, List<ChoicePicker> pickers, LocalDictationSession session)
    {
        var row = FindRow(content) ?? throw new InvalidOperationException("Model memory settings row is missing.");
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.Children.Add(SettingsHelp.Label("Unload idle models",
            "Releases the memory of local models after they were not used for this long. They load again automatically when needed, " +
            "so the next dictation or text workflow may start a little slower. Parakeet, whisper.cpp, Qwen3 and local text models are affected; cloud providers are not."));
        var picker = new ChoicePicker();
        picker.Configure("Unload idle models", "history", "Unload idle models");
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        string? selectionError = null;
        void Refresh()
        {
            picker.SetOptions([
                new("0", "Never", "Keep local models loaded until you unload them or close TypeWhisper."),
                new("-1", "Immediately", "Release memory right after each use. Every use loads the model again."),
                new("120", "After 2 minutes", "Release memory after 2 minutes without use."),
                new("300", "After 5 minutes", "Release memory after 5 minutes without use."),
                new("600", "After 10 minutes", "Release memory after 10 minutes without use. This is the default."),
                new("1800", "After 30 minutes", "Release memory after 30 minutes without use."),
                new("3600", "After 1 hour", "Release memory after 1 hour without use.")
            ], session.ModelMemoryPreferences.AutoUnloadSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            hint.Text = selectionError ?? session.ModelMemoryPreferences.Error ?? "Saved for this profile. Applies to models that are loaded now.";
        }
        picker.SelectionChanged += id =>
        {
            if (int.TryParse(id, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                selectionError = session.SelectModelAutoUnload(seconds);
            Refresh();
        };
        row.Children.Add(picker); row.Children.Add(hint); pickers.Add(picker);
        Refresh();
    }

    private static StackPanel? FindRow(StackPanel root)
    {
        if (Equals(root.Tag, "ModelAutoUnloadSeconds")) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child) is { } row) return row;
        return null;
    }
}
