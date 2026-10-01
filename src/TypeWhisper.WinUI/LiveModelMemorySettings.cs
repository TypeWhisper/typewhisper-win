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
        row.Children.Add(SettingsHelp.Label(Loc.T("Unload idle models"),
            Loc.T("Releases the memory of local models after they were not used for this long. They load again automatically when needed, so the next dictation or text workflow may start a little slower. Parakeet, whisper.cpp, Qwen3 and local text models are affected; cloud providers are not.")));
        var picker = new ChoicePicker();
        picker.Configure(Loc.T("Unload idle models"), "history", Loc.T("Unload idle models"));
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        string? selectionError = null;
        void Refresh()
        {
            picker.SetOptions([
                new("0", Loc.T("Never"), Loc.T("Keep local models loaded until you unload them or close TypeWhisper.")),
                new("-1", Loc.T("Immediately"), Loc.T("Release memory right after each use. Every use loads the model again.")),
                new("120", Loc.T("After 2 minutes"), Loc.T("Release memory after 2 minutes without use.")),
                new("300", Loc.T("After 5 minutes"), Loc.T("Release memory after 5 minutes without use.")),
                new("600", Loc.T("After 10 minutes"), Loc.T("Release memory after 10 minutes without use. This is the default.")),
                new("1800", Loc.T("After 30 minutes"), Loc.T("Release memory after 30 minutes without use.")),
                new("3600", Loc.T("After 1 hour"), Loc.T("Release memory after 1 hour without use."))
            ], session.ModelMemoryPreferences.AutoUnloadSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            hint.Text = selectionError ?? session.ModelMemoryPreferences.Error ?? Loc.T("Saved for this profile. Applies to models that are loaded now.");
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
