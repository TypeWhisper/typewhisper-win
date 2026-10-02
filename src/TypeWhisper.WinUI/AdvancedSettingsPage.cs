using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Follows the macOS Advanced page: titled sections, each in one card, in the macOS order where Windows has them.
// Row tags match the settings search keys so a search result scrolls to its row.
internal static class AdvancedSettingsPage
{
    internal static void Render(StackPanel content, List<ChoicePicker> pickers, LocalDictationSession session, WinUIHttpApi api)
    {
        content.Children.Clear(); pickers.Clear();
        content.Children.Add(SettingsCard.PageTitle(Loc.T("Advanced")));
        var recording = Section(content, Loc.T("Recording"),
            Row("ModelAutoUnloadSeconds"), Row("TranscribeShortQuietClipsAggressively"), Row("CancellationBehavior"),
            WhisperModeRow(session));
        LiveModelMemorySettings.Configure(recording, pickers, session);
        LiveShortClipSettings.Configure(recording, session);
        LiveCancellationBehaviorSettings.Configure(recording, pickers, session);

        Section(content, Loc.T("Spoken feedback"), new SystemVoiceSettingsControl(session, pickers));
        Section(content, Loc.T("API server"), new HttpApiSettingsView(api));
        Section(content, Loc.T("Command line tool"), new CliSettingsView());
        Section(content, Loc.T("Integrations"), new RaycastIntegrationView());
        Section(content, Loc.T("Support diagnostics"), new DiagnosticsSettingsView());
    }

    // Rows are filled by their live settings.
    private static SettingsCard Section(StackPanel page, string heading, params UIElement[] rows)
    {
        var card = new SettingsCard(heading);
        foreach (var row in rows) card.Add(row);
        page.Children.Add(card);
        return card;
    }

    private static SettingsRow Row(string key) => new(key);

    // Whisper mode is the macOS microphone boost; it is saved with the other audio preferences.
    private static SettingsRow WhisperModeRow(LocalDictationSession session)
    {
        var row = Row("WhisperModeEnabled");
        var toggle = AppToggleSwitch.Create(session.AudioPreferences.WhisperModeEnabled);
        var help = Loc.T("Automatically raises quiet microphone input before transcription. Helps with low-gain microphones, but very noisy rooms may sound louder too.");
        AutomationProperties.SetName(toggle, Loc.T("Whisper mode")); AutomationProperties.SetHelpText(toggle, help);
        row.Set(Loc.T("Whisper mode"), Loc.T("Boost quiet speech automatically."), help, toggle);
        row.Status = session.AudioPreferencesError ?? "";
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            var error = session.SaveAudioPreferences(session.AudioPreferences with { WhisperModeEnabled = toggle.IsOn });
            row.Status = error ?? "";
            if (error is not null) { restoring = true; toggle.IsOn = !toggle.IsOn; restoring = false; }
        };
        return row;
    }
}
