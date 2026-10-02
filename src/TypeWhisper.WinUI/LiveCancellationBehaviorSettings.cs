using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveCancellationBehaviorSettings
{
    private static readonly string Hint = Loc.T("Double: press Esc twice to cancel. Single: press Esc once. Both show a cancellation banner for 1.5 seconds. Instant: press Esc once without a banner. Applies to recording and processing.");

    // Fills the Advanced page's Recording row.
    internal static void Configure(StackPanel content, List<ChoicePicker> pickers, LocalDictationSession session)
    {
        var row = SettingsRow.Require(content, "CancellationBehavior").Reset(pickers);
        var picker = new ChoicePicker();
        picker.Configure(Loc.T("Cancellation behavior"), "microphone", Loc.T("Cancellation behavior"));
        row.Set(Loc.T("Cancellation behavior"), Hint, picker);
        void Refresh()
        {
            picker.SetOptions([
                new("Double", Loc.T("Double"), Loc.T("Press Esc twice within 3 seconds to cancel.")),
                new("Single", Loc.T("Single"), Loc.T("Press Esc once to cancel.")),
                new("Instant", Loc.T("Instant"), Loc.T("Press Esc once to cancel, without a banner."))
            ], session.EscapeCancelPreferences.Current.ToString());
            row.Status = session.EscapeCancelPreferences.Error ?? "";
        }
        picker.SelectionChanged += id =>
        {
            if (Enum.TryParse<EscapeCancelBehavior>(id, out var behavior)) session.SelectEscapeCancelBehavior(behavior);
            Refresh();
        };
        pickers.Add(picker);
        Refresh();
    }
}
