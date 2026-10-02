using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;

namespace TypeWhisper.WinUI;

internal sealed class LiveAudioSettings(LocalDictationSession session)
{
    internal void Render(StackPanel content, List<ChoicePicker> pickers)
    {
        content.Children.Clear();
        content.Children.Add(SettingsCard.PageTitle(Loc.T("Audio"), Loc.T("Audio preferences are saved automatically and used by dictation.")));
        var input = new SettingsCard();
        var microphones = new MicrophonePriorityEditor(session);
        input.Add(microphones);
        input.Add(new MicrophoneTestControl(session));
        content.Children.Add(input);
        pickers.Add(microphones.AddPicker);
        var output = new SettingsCard();
        content.Children.Add(output);
        var preferences = session.AudioPreferences;
        var devices = OutputDevices();

        AddPicker(Loc.T("Audio output"), Loc.T("One output for feedback sounds, spoken feedback and volume reduction."), devices,
            preferences.OutputDeviceId ?? "", id => session.AudioPreferences with { OutputDeviceId = id });
        AddToggle(Loc.T("Sound feedback"), Loc.T("Play a short sound when recording starts or stops."), preferences.SoundFeedbackEnabled,
            value => session.AudioPreferences with { SoundFeedbackEnabled = value });
        AddToggle(Loc.T("Lower audio while recording"), Loc.T("Reduce the selected output's volume, then restore it after recording. This includes TypeWhisper sounds on that output."), preferences.AudioDuckingEnabled,
            value => session.AudioPreferences with { AudioDuckingEnabled = value });
        var levels = new[] { 0, 10, 20, 30, 50, 75, 100 }.Select(level => new Choice(level.ToString(), level == 0 ? Loc.T("Muted") : $"{level}%", Loc.T("Of the current output volume"))).ToArray();
        AddPicker(Loc.T("Recording volume"), Loc.T("0% silences the output. Your own volume changes during recording are preserved."), levels,
            ((int)Math.Round(preferences.AudioDuckingLevel * 100)).ToString(), id => session.AudioPreferences with { AudioDuckingLevel = int.Parse(id) / 100f });
        AddToggle(Loc.T("Pause media during recording"), Loc.T("Send the media Play/Pause key at start and stop, as in the previous app. Use while media is playing; paused media may start."), preferences.PauseMediaDuringRecording,
            value => session.AudioPreferences with { PauseMediaDuringRecording = value });
        AddToggle(Loc.T("Stop after silence"), Loc.T("Finish and transcribe after a quiet pause, including silence at the start. Waits while shortcut modifiers are held. Background noise may delay stopping."), preferences.SilenceAutoStopEnabled,
            value => session.AudioPreferences with { SilenceAutoStopEnabled = value });
        var timeouts = new[] { 3, 5, 10, 15, 30 }.Append(preferences.SilenceAutoStopSeconds).Distinct().Order()
            .Select(seconds => new Choice(seconds.ToString(), Loc.T("{0} seconds", seconds), Loc.T("Continuous silence before finishing"))).ToArray();
        AddPicker(Loc.T("Silence timeout"), Loc.T("Used when Stop after silence is enabled. Changes apply to the next recording."), timeouts,
            preferences.SilenceAutoStopSeconds.ToString(), id => session.AudioPreferences with { SilenceAutoStopSeconds = int.Parse(id) });

        void AddToggle(string title, string hint, bool value, Func<bool, DictationAudioPreferences> next)
        {
            var toggle = AppToggleSwitch.Create(value);
            AutomationProperties.SetName(toggle, title); AutomationProperties.SetHelpText(toggle, hint);
            var row = new SettingsRow().Set(title, hint, toggle);
            row.Status = session.AudioPreferencesError ?? "";
            var restoring = false;
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                var error = session.SaveAudioPreferences(next(toggle.IsOn));
                row.Status = error ?? "";
                if (error is not null) { restoring = true; toggle.IsOn = !toggle.IsOn; restoring = false; }
            };
            output.Children.Add(row);
        }
        void AddPicker(string title, string hint, IReadOnlyList<Choice> options, string selected, Func<string, DictationAudioPreferences> next)
        {
            var picker = new ChoicePicker();
            picker.Configure(title, "speaker", title);
            var choices = options.Any(option => option.Id == selected) ? options : options.Concat([new Choice(selected, Loc.T("Saved device · unavailable"), Loc.T("Reconnect the device or select another output"))]).ToArray();
            picker.SetOptions(choices, selected);
            var row = new SettingsRow().Set(title, hint, picker);
            var saved = selected;
            picker.SelectionChanged += id =>
            {
                var error = session.SaveAudioPreferences(next(id));
                row.Status = error ?? "";
                if (error is not null) picker.SetOptions(choices, saved);
                else saved = id;
            };
            output.Children.Add(row); pickers.Add(picker);
        }
    }

    private static IReadOnlyList<Choice> OutputDevices()
    {
        var choices = new List<Choice> { new("", Loc.T("System default"), Loc.T("Windows default output")) };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device) choices.Add(new(device.ID, device.FriendlyName, Loc.T("Audio output")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Debug.WriteLine("Output enumeration failed: " + ex.Message); }
        return choices;
    }
}
