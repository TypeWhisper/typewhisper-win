using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveModelMemorySettings
{
    // Fills the Advanced page's Recording row.
    internal static void Configure(StackPanel content, List<ChoicePicker> pickers, LocalDictationSession session)
    {
        var row = SettingsRow.Require(content, "ModelAutoUnloadSeconds").Reset(pickers);
        var picker = new ChoicePicker();
        picker.Configure(Loc.T("Unload idle models"), "history", Loc.T("Unload idle models"));
        row.Set(Loc.T("Unload idle models"), Loc.T("Release the memory of local models after inactivity. They load again when needed."),
            Loc.T("Releases the memory of local models after they were not used for this long. They load again automatically when needed, so the next dictation or text workflow may start a little slower. Parakeet, whisper.cpp, Qwen3 and local text models are affected; cloud providers are not."), picker);
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
            row.Status = selectionError ?? session.ModelMemoryPreferences.Error ?? "";
        }
        picker.SelectionChanged += id =>
        {
            if (int.TryParse(id, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                selectionError = session.SelectModelAutoUnload(seconds);
            Refresh();
        };
        pickers.Add(picker);
        Refresh();
    }
}
