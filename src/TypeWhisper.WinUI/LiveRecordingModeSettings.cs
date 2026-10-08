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
        var row = SettingsRow.Require(content, "Mode").Reset(pickers);
        var picker = new ChoicePicker();
        picker.Configure(Loc.T("Recording mode"), "microphone", Loc.T("Recording mode"));
        row.Set(Loc.T("Recording mode"),
            Loc.T("Toggle starts and stops with a press. Hold records while the shortcut is held. Hybrid toggles on a tap, or stops on release after a hold of at least 300 ms."), picker);
        string? selectionError = null;
        void Refresh()
        {
            picker.SetOptions([
                new("Hybrid", Loc.T("Hybrid"), Loc.T("Tap to toggle. Hold for at least 300 ms, then release to stop.")),
                new("Toggle", Loc.T("Toggle"), Loc.T("Press once to start and again to stop.")),
                new("Hold", Loc.T("Hold to record"), Loc.T("Record while the shortcut is held. Release to stop."))
            ], session.RecordingModePreferences.Current.ToString());
            picker.IsEnabled = session.CanChangeProvider;
            row.Status = selectionError ?? session.RecordingModePreferences.Error ??
                (session.CanChangeProvider ? "" : Loc.T("Finish or cancel the current dictation before changing recording mode."));
        }
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() => { if (row.IsLoaded) Refresh(); });
        picker.SelectionChanged += id =>
        {
            if (Enum.TryParse<RecordingMode>(id, out var mode)) selectionError = session.SelectRecordingMode(mode);
            Refresh();
        };
        ViewSubscriptions.Attach(row, () => { session.Changed += OnChanged; Refresh(); },
            () => session.Changed -= OnChanged);
        pickers.Add(picker);
        Refresh();
    }
}
