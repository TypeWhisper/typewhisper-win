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
        var title = new TextBlock { Text = Loc.T("Advanced"), FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        content.Children.Add(title);
        var sections = new StackPanel { Spacing = 24 };
        content.Children.Add(sections);

        var recording = Section(sections, Loc.T("Recording"),
            Row("ModelAutoUnloadSeconds"), Row("TranscribeShortQuietClipsAggressively"), Row("CancellationBehavior"),
            WhisperModeRow(session));
        LiveModelMemorySettings.Configure(recording, pickers, session);
        LiveShortClipSettings.Configure(recording, session);
        LiveCancellationBehaviorSettings.Configure(recording, pickers, session);

        Section(sections, Loc.T("Spoken feedback"), Tagged("SpokenFeedbackEnabled", new SystemVoiceSettingsControl(session, pickers)));
        Section(sections, Loc.T("API server"), new HttpApiSettingsView(api));
        Section(sections, Loc.T("Command line tool"), new CliSettingsView());
        Section(sections, Loc.T("Integrations"), new RaycastIntegrationView());
        Section(sections, Loc.T("Support diagnostics"), new DiagnosticsSettingsView());
    }

    // Rows are filled by their live settings; the card separates them with hairlines.
    private static StackPanel Section(StackPanel sections, string heading, params UIElement[] rows)
    {
        var section = new StackPanel { Spacing = 10 };
        var label = new TextBlock { Text = heading, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(label, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        section.Children.Add(label);
        var body = new StackPanel { Spacing = 16 };
        foreach (var row in rows)
        {
            if (body.Children.Count > 0)
                body.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["HairlineBrush"] });
            body.Children.Add(row);
        }
        section.Children.Add(new Border
        {
            Child = body, Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1)
        });
        sections.Children.Add(section);
        return body;
    }

    private static StackPanel Row(string key) => new() { Spacing = 8, Tag = key };

    private static StackPanel Tagged(string key, UIElement child)
    {
        var row = Row(key);
        row.Children.Add(child);
        return row;
    }

    // Whisper mode is the macOS microphone boost; it is saved with the other audio preferences.
    private static StackPanel WhisperModeRow(LocalDictationSession session)
    {
        var row = Row("WhisperModeEnabled");
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = Loc.T("Automatically raises quiet microphone input before transcription. Helps with low-gain microphones, but very noisy rooms may sound louder too.");
        var label = SettingsHelp.Label(Loc.T("Whisper mode"), hint);
        label.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        var toggle = AppToggleSwitch.Create(session.AudioPreferences.WhisperModeEnabled);
        AutomationProperties.SetName(toggle, Loc.T("Whisper mode")); AutomationProperties.SetHelpText(toggle, hint);
        Grid.SetColumn(toggle, 1); grid.Children.Add(toggle);
        row.Children.Add(grid);
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = session.AudioPreferencesError ?? Loc.T("Saved for the next dictation.") };
        row.Children.Add(status);
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            var error = session.SaveAudioPreferences(session.AudioPreferences with { WhisperModeEnabled = toggle.IsOn });
            status.Text = error ?? Loc.T("Saved for the next dictation.");
            if (error is not null) { restoring = true; toggle.IsOn = !toggle.IsOn; restoring = false; }
        };
        return row;
    }
}
