using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;

namespace TypeWhisper.WinUI;

internal sealed class LiveAudioSettings(LocalDictationSession session)
{
    private readonly TextBlock _status = Label(session.AudioPreferencesError ?? "Changes are saved for your next dictation.", true);

    internal void Render(StackPanel content, List<PrototypeChoicePicker> pickers)
    {
        content.Children.Clear();
        content.Children.Add(new TextBlock { Text = "Audio", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        content.Children.Add(Label("Audio preferences are saved in this development profile and used by dictation.", true));
        var microphones = new MicrophonePriorityEditor(session);
        content.Children.Add(microphones);
        pickers.Add(microphones.AddPicker);
        var preferences = session.AudioPreferences;
        var devices = OutputDevices();
        void Save(DictationAudioPreferences next) => _status.Text = session.SaveAudioPreferences(next) ?? "Saved · applies to the next dictation.";

        AddPicker("Audio output", "One output for feedback sounds and volume reduction. Spoken feedback will use it once connected.", devices,
            preferences.OutputDeviceId ?? "", id => Save(session.AudioPreferences with { OutputDeviceId = id }));
        AddToggle("Sound feedback", "Play a short sound when recording starts or stops.", preferences.SoundFeedbackEnabled,
            value => Save(session.AudioPreferences with { SoundFeedbackEnabled = value }));
        AddToggle("Whisper mode", "Boost quiet speech automatically.", preferences.WhisperModeEnabled,
            value => Save(session.AudioPreferences with { WhisperModeEnabled = value }));
        AddToggle("Lower audio while recording", "Reduce the selected output's volume, then restore it after recording. This includes TypeWhisper sounds on that output.", preferences.AudioDuckingEnabled,
            value => Save(session.AudioPreferences with { AudioDuckingEnabled = value }));
        var levels = new[] { 0, 10, 20, 30, 50, 75, 100 }.Select(level => new PrototypeChoice(level.ToString(), level == 0 ? "Muted" : $"{level}%", "Of the current output volume")).ToArray();
        AddPicker("Recording volume", "0% silences the output. Your own volume changes during recording are preserved.", levels,
            ((int)Math.Round(preferences.AudioDuckingLevel * 100)).ToString(), id => Save(session.AudioPreferences with { AudioDuckingLevel = int.Parse(id) / 100f }));
        AddToggle("Pause media during recording", "Send the media Play/Pause key at start and stop, as in the previous app. Use while media is playing; paused media may start.", preferences.PauseMediaDuringRecording,
            value => Save(session.AudioPreferences with { PauseMediaDuringRecording = value }));
        AddToggle("Spoken feedback", "Not connected yet · requires the speech provider integration.", false, _ => { }, false);
        AddToggle("Stop after silence", "Finish and transcribe after a quiet pause, including silence at the start. Waits while shortcut modifiers are held. Background noise may delay stopping.", preferences.SilenceAutoStopEnabled,
            value => Save(session.AudioPreferences with { SilenceAutoStopEnabled = value }));
        var timeouts = new[] { 3, 5, 10, 15, 30 }.Append(preferences.SilenceAutoStopSeconds).Distinct().Order()
            .Select(seconds => new PrototypeChoice(seconds.ToString(), $"{seconds} seconds", "Continuous silence before finishing")).ToArray();
        AddPicker("Silence timeout", "Used when Stop after silence is enabled. Changes apply to the next recording.", timeouts,
            preferences.SilenceAutoStopSeconds.ToString(), id => Save(session.AudioPreferences with { SilenceAutoStopSeconds = int.Parse(id) }));
        content.Children.Add(_status);

        void Separator() => content.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["HairlineBrush"], Margin = new Thickness(0, 4, 0, 4) });
        void AddToggle(string title, string hint, bool value, Action<bool> changed, bool enabled = true)
        {
            Separator();
            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(Label(title)); copy.Children.Add(Label(hint, true)); row.Children.Add(copy);
            var toggle = PrototypeToggleSwitch.Create(value);
            toggle.IsEnabled = enabled;
            AutomationProperties.SetName(toggle, title); AutomationProperties.SetHelpText(toggle, hint);
            var restoring = false;
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                changed(toggle.IsOn);
                if (session.AudioPreferencesError is not null) { restoring = true; toggle.IsOn = !toggle.IsOn; restoring = false; }
            };
            Grid.SetColumn(toggle, 1); row.Children.Add(toggle); content.Children.Add(row);
        }
        void AddPicker(string title, string hint, IReadOnlyList<PrototypeChoice> options, string selected, Action<string> changed)
        {
            Separator();
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(Label(title));
            var picker = new PrototypeChoicePicker();
            picker.Configure(title, "speaker", title);
            var choices = options.Any(option => option.Id == selected) ? options : options.Concat([new PrototypeChoice(selected, "Saved device · unavailable", "Reconnect the device or select another output")]).ToArray();
            picker.SetOptions(choices, selected);
            var saved = selected;
            picker.SelectionChanged += id =>
            {
                changed(id);
                if (session.AudioPreferencesError is not null) picker.SetOptions(choices, saved);
                else saved = id;
            };
            row.Children.Add(picker); row.Children.Add(Label(hint, true));
            content.Children.Add(row); pickers.Add(picker);
        }
    }

    private static IReadOnlyList<PrototypeChoice> OutputDevices()
    {
        var choices = new List<PrototypeChoice> { new("", "System default", "Windows default output") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device) choices.Add(new(device.ID, device.FriendlyName, "Audio output"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Debug.WriteLine("Output enumeration failed: " + ex.Message); }
        return choices;
    }

    private static TextBlock Label(string text, bool muted = false) => new()
    {
        Text = text, FontSize = muted ? 12 : 14, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };
}
