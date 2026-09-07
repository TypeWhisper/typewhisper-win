using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class SystemVoiceSettingsControl : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly TextBlock _status = Label("");
    private readonly HandCursorButton _test = Button("Test voice");
    private readonly HandCursorButton _stop = Button("Stop speaking");
    private bool _testing;
    private bool _stopping;

    internal SystemVoiceSettingsControl(LocalDictationSession session, List<PrototypeChoicePicker> pickers)
    {
        _session = session;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["HairlineBrush"], Margin = new(0, 4, 0, 4) });
        content.Children.Add(new TextBlock { Text = "Spoken feedback", FontSize = 16, Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        content.Children.Add(Label("Read successfully inserted dictation aloud using Windows System Voice. Off by default. Review, failed processing and file jobs are not read automatically."));
        var enabled = PrototypeToggleSwitch.Create(session.AudioPreferences.SpokenFeedbackEnabled);
        AutomationProperties.SetName(enabled, "Spoken feedback");
        var restoring = false;
        enabled.Toggled += async (_, _) =>
        {
            if (restoring) return;
            var previous = session.AudioPreferences.SpokenFeedbackEnabled;
            var error = session.SaveAudioPreferences(session.AudioPreferences with { SpokenFeedbackEnabled = enabled.IsOn });
            if (error is not null)
            {
                restoring = true; enabled.IsOn = previous; restoring = false; _status.Text = error; return;
            }
            _status.Text = "Saved. Applies to future successful dictations.";
            if (!enabled.IsOn) await StopAsync();
        };
        content.Children.Add(enabled);
        content.Children.Add(Label("Provider: Windows System Voice · local synthesis; no cloud requests."));
        IReadOnlyList<SpokenFeedbackVoice> voices;
        try { voices = new WindowsSystemVoiceBackend().GetVoices(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            voices = [];
            content.Children.Add(Label("Installed Windows voices could not be read. Reopen Audio settings after checking Windows speech settings."));
        }
        if (voices.Count == 0) content.Children.Add(Label("No installed Windows voice is available. Install a voice through Windows settings before testing."));
        var options = new List<PrototypeChoice> { new("", "Windows default voice", "Uses the Windows voice; no automatic language switch") };
        options.AddRange(voices.Select(voice => new PrototypeChoice(voice.Id, voice.DisplayName, voice.Language ?? "Installed Windows voice")));
        var savedVoice = session.AudioPreferences.SpokenFeedbackVoiceId ?? "";
        if (!options.Any(option => option.Id == savedVoice))
            options.Add(new(savedVoice, "Saved voice · unavailable", "Choose an installed voice; unavailable voices never fall back silently"));
        var voicePicker = new PrototypeChoicePicker();
        voicePicker.Configure("Windows voice", "speaker", "Spoken feedback voice");
        voicePicker.SetOptions(options, savedVoice);
        voicePicker.SelectionChanged += id =>
        {
            if (restoring) return;
            var error = session.SaveAudioPreferences(session.AudioPreferences with { SpokenFeedbackVoiceId = id });
            if (error is not null)
            {
                restoring = true; voicePicker.SetOptions(options, savedVoice); restoring = false; _status.Text = error;
            }
            else { savedVoice = id; _status.Text = "Voice saved. Applies to the next playback."; }
        };
        content.Children.Add(voicePicker); pickers.Add(voicePicker);
        content.Children.Add(Label("Uses the Audio output selected above. Supports up to 4,000 characters and two minutes of speech. Choose an available voice and output to test playback."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_test); buttons.Children.Add(_stop); content.Children.Add(buttons);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        content.Children.Add(_status); Content = content;
        _test.Click += async (_, _) =>
        {
            if (_testing || _stopping || !session.CanChangeProvider || session.SpokenFeedback.IsBusy) return;
            _testing = true; UpdateButtons(); _status.Text = "Testing Windows voice…";
            try
            {
                var result = await session.TestSpokenFeedbackAsync();
                if (IsLoaded) _status.Text = result.Message ?? (result.Status == SpokenFeedbackStatus.Completed ? "Voice test completed." : "Voice test stopped.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded) _status.Text = "The voice test failed. Check the selected Windows voice and audio output."; }
            finally { _testing = false; if (IsLoaded) UpdateButtons(); }
        };
        _stop.Click += async (_, _) => await StopAsync();
        Loaded += (_, _) => { session.Changed += SessionChanged; UpdateButtons(); };
        Unloaded += async (_, _) =>
        {
            session.Changed -= SessionChanged;
            if (_testing) await StopAsync();
        };
        UpdateButtons();
    }

    private void SessionChanged()
    {
        if (DispatcherQueue.HasThreadAccess) { if (IsLoaded) UpdateButtons(); }
        else DispatcherQueue.TryEnqueue(() => { if (IsLoaded) UpdateButtons(); });
    }

    private async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        if (IsLoaded) { _status.Text = "Stopping spoken feedback…"; UpdateButtons(); }
        try
        {
            await _session.SpokenFeedback.CancelAndDrainAsync();
            if (IsLoaded) _status.Text = "Spoken feedback stopped.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsLoaded) _status.Text = "Spoken feedback could not finish stopping. Wait before starting another test."; }
        finally { _stopping = false; if (IsLoaded) UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        _test.IsEnabled = !_testing && !_stopping && !_session.SpokenFeedback.IsBusy && !_session.SpokenFeedback.IsShutdown && _session.CanChangeProvider;
        _stop.IsEnabled = !_stopping && (_testing || _session.SpokenFeedback.IsBusy);
    }
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
    private static HandCursorButton Button(string text) => new() { Content = text,
        Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
}
