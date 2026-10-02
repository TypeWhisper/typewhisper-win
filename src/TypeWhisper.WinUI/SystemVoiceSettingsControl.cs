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
    private readonly HandCursorButton _test = Button(Loc.T("Test voice"));
    private readonly HandCursorButton _stop = Button(Loc.T("Stop speaking"));
    private bool _testing;
    private bool _stopping;

    internal SystemVoiceSettingsControl(LocalDictationSession session, List<ChoicePicker> pickers)
    {
        _session = session;
        var content = new SettingsRows();
        var enabled = AppToggleSwitch.Create(session.AudioPreferences.SpokenFeedbackEnabled);
        AutomationProperties.SetName(enabled, Loc.T("Spoken feedback"));
        var feedback = new SettingsRow("SpokenFeedbackEnabled").Set(Loc.T("Spoken feedback"), "",
            Loc.T("Read successfully inserted dictation aloud using the selected voice. Off by default. Review, failed processing and file jobs are not read automatically. Windows voices run locally. Selecting a cloud voice sends the text to that provider and may incur API charges. Uses the selected audio output. Supports up to 4,000 characters and two minutes of speech."), enabled);
        content.Children.Add(feedback);
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
            _status.Text = "";
            if (!enabled.IsOn) await StopAsync();
        };
        IReadOnlyList<SpokenFeedbackVoice> voices;
        try { voices = session.GetSpokenFeedbackVoices(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            voices = [];
            feedback.Below(Label(Loc.T("Installed Windows voices could not be read. Reopen Audio settings after checking Windows speech settings.")));
        }
        if (voices.Count == 0) feedback.Below(Label(Loc.T("No installed Windows voice is available. Install a voice through Windows settings before testing.")));
        var options = new List<Choice> { new("", Loc.T("Windows default voice"), Loc.T("Uses the Windows voice; no automatic language switch")) };
        options.AddRange(voices.Select(voice => new Choice(voice.Id, voice.DisplayName, voice.Language ?? (voice.Id.StartsWith("plugin:", StringComparison.Ordinal) ? (voice.IsLocal ? Loc.T("Local provider voice") : Loc.T("Cloud provider voice")) : Loc.T("Installed Windows voice")))));
        var savedVoice = session.AudioPreferences.SpokenFeedbackVoiceId ?? "";
        if (!options.Any(option => option.Id == savedVoice))
            options.Add(new(savedVoice, Loc.T("Saved voice · unavailable"), Loc.T("Choose an available voice; unavailable voices never fall back silently")));
        var voicePicker = new ChoicePicker();
        voicePicker.Configure(Loc.T("Voice"), "speaker", Loc.T("Spoken feedback voice"));
        voicePicker.SetOptions(options, savedVoice);
        voicePicker.SelectionChanged += id =>
        {
            if (restoring) return;
            var error = session.SaveAudioPreferences(session.AudioPreferences with { SpokenFeedbackVoiceId = id });
            if (error is not null)
            {
                restoring = true; voicePicker.SetOptions(options, savedVoice); restoring = false; _status.Text = error;
            }
            else { savedVoice = id; _status.Text = ""; }
        };
        pickers.Add(voicePicker);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_test); buttons.Children.Add(_stop);
        var voice = new SettingsRow("SpokenFeedbackVoiceId").Set(Loc.T("Voice"), control: voicePicker);
        voice.Below(buttons).Below(_status);
        content.Children.Add(voice);
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Content = content;
        _test.Click += async (_, _) =>
        {
            if (_testing || _stopping || !session.CanChangeProvider || session.SpokenFeedback.IsBusy) return;
            _testing = true; UpdateButtons(); _status.Text = Loc.T("Testing voice…");
            try
            {
                var result = await session.TestSpokenFeedbackAsync();
                if (IsLoaded) _status.Text = result.Message ?? (result.Status == SpokenFeedbackStatus.Completed ? Loc.T("Voice test completed.") : Loc.T("Voice test stopped."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded) _status.Text = Loc.T("The voice test failed. Check the selected voice and audio output."); }
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
        if (IsLoaded) { _status.Text = Loc.T("Stopping spoken feedback…"); UpdateButtons(); }
        try
        {
            await _session.SpokenFeedback.CancelAndDrainAsync();
            if (IsLoaded) _status.Text = Loc.T("Spoken feedback stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsLoaded) _status.Text = Loc.T("Spoken feedback could not finish stopping. Wait before starting another test."); }
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
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
