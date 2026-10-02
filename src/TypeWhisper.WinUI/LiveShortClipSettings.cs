using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

internal static class LiveShortClipSettings
{
    // Fills the Advanced page's Recording row.
    internal static void Configure(StackPanel content, LocalDictationSession session)
    {
        var row = SettingsRow.Require(content, "TranscribeShortQuietClipsAggressively");
        var store = session.TextPreferences;
        var toggle = AppToggleSwitch.Create(store.Current.TranscribeShortQuietClipsAggressively);
        AutomationProperties.SetName(toggle, Loc.T("Recognize short, quiet clips"));
        row.Set(Loc.T("Recognize short, quiet clips"), Loc.T("Try to recognize very brief or quiet speech, even when detection is uncertain."),
            Loc.T("Enable to transcribe very quiet audio; silence may produce unwanted text. Clips shorter than 40 ms are always skipped. Changes apply to the next recording."), toggle);
        row.Status = store.Error ?? "";
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            store.Save(store.Current with { TranscribeShortQuietClipsAggressively = toggle.IsOn });
            restoring = true;
            toggle.IsOn = store.Current.TranscribeShortQuietClipsAggressively;
            restoring = false;
            row.Status = store.Error ?? "";
        };
    }
}
