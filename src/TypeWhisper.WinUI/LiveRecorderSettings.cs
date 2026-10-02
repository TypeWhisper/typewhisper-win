using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal static class LiveRecorderSettings
{
    internal static void Configure(string category, StackPanel parent, List<ChoicePicker> pickers,
        RecorderPreferencesStore preferences, Func<IReadOnlyList<SystemAudioOutputDevice>> getDevices)
    {
        if (category != "Recorder") return;
        parent.Children.Clear();
        // The catalog parent is reused between categories; this child owns exactly this binding's events.
        var content = new StackPanel { Spacing = 20 };
        parent.Children.Add(content);
        pickers.Clear();
        content.Children.Add(SettingsCard.PageTitle(Loc.T("Recorder"), Loc.T("Source choices are saved for your next recording. Changes never switch sources during an active recording.")));
        var card = new SettingsCard();
        content.Children.Add(card);
        var microphone = AppToggleSwitch.Create(preferences.Current.MicrophoneEnabled);
        var system = AppToggleSwitch.Create(preferences.Current.SystemAudioEnabled);
        AddToggle(Loc.T("Microphone on by default"), Loc.T("Start new recorder sessions with your microphone enabled."), microphone);
        AddToggle(Loc.T("System audio on by default"), Loc.T("Start new recorder sessions with sound from your computer enabled."), system);
        var device = new ChoicePicker();
        device.Configure(Loc.T("System audio device"), "speaker", Loc.T("Recorder system audio device"));
        device.Width = 220; device.UseRowHeight();
        pickers.Add(device);
        var refreshDevices = new HandCursorButton { Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 }, Width = 36, Height = 36,
            Padding = new Thickness(8), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(refreshDevices, Loc.T("Refresh devices")); ToolTipService.SetToolTip(refreshDevices, Loc.T("Refresh devices"));
        var deviceControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        deviceControls.Children.Add(device); deviceControls.Children.Add(refreshDevices);
        var deviceRow = new SettingsRow("RecorderSystemAudioDeviceId").Set(Loc.T("System audio device"),
            Loc.T("The microphone uses your Audio settings priority list. This output selection controls which system audio is recorded, independently of feedback sounds."), deviceControls);
        card.Children.Add(deviceRow);
        card.Children.Add(new SettingsRow().Set(Loc.T("Audio format: WAV · 16 kHz mono"), Loc.T("Tracks: Mixed · microphone ducking off"),
            Loc.T("Recordings are saved locally. Choose Transcribe on a saved recording to process it.")));
        var refreshing = false;
        var subscribed = false;
        IReadOnlyList<Choice> choices = [];
        string? deviceError = null;

        void RefreshSelection()
        {
            refreshing = true;
            var current = preferences.Current;
            microphone.IsOn = current.MicrophoneEnabled;
            system.IsOn = current.SystemAudioEnabled;
            var id = current.OutputDeviceId ?? "";
            var available = choices.Any(choice => choice.Id == id);
            var options = available ? choices : choices.Concat([new Choice(id,
                Loc.T("Saved device · unavailable"), Loc.T("Reconnect it or choose another device. No automatic fallback."))]).ToArray();
            device.SetOptions(options, id);
            deviceRow.Status = preferences.Error ?? deviceError ?? (!available
                ? Loc.T("The saved output is unavailable. Reconnect it or choose another output before recording system audio.")
                : "");
            refreshing = false;
        }
        void RefreshDevices()
        {
            var available = new List<Choice> { new("", Loc.T("System default"), Loc.T("Windows default audio output")) };
            deviceError = null;
            try
            {
                available.AddRange(getDevices().Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .DistinctBy(item => item.Id).Select(item => new Choice(item.Id!, item.Name, Loc.T("System audio capture source"))));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { deviceError = Loc.T("Audio devices could not be listed. Your saved selection is unchanged."); }
            choices = available;
            RefreshSelection();
        }
        void OnPreferencesChanged()
        {
            if (content.DispatcherQueue.HasThreadAccess) RefreshSelection();
            else content.DispatcherQueue.TryEnqueue(() => { if (content.IsLoaded) RefreshSelection(); });
        }
        microphone.Toggled += (_, _) => { if (!refreshing) { preferences.Save(preferences.Current with { MicrophoneEnabled = microphone.IsOn }); RefreshSelection(); } };
        system.Toggled += (_, _) => { if (!refreshing) { preferences.Save(preferences.Current with { SystemAudioEnabled = system.IsOn }); RefreshSelection(); } };
        device.SelectionChanged += id => { if (!refreshing) { preferences.Save(preferences.Current with { OutputDeviceId = id }); RefreshSelection(); } };
        refreshDevices.Click += (_, _) => RefreshDevices();
        void Attach()
        {
            if (!subscribed) { preferences.Changed += OnPreferencesChanged; subscribed = true; }
            RefreshDevices();
        }
        content.Loaded += (_, _) => Attach();
        content.Unloaded += (_, _) => { preferences.Changed -= OnPreferencesChanged; subscribed = false; };
        if (content.IsLoaded) Attach();
        RefreshDevices();

        void AddToggle(string title, string description, ToggleSwitch toggle)
        {
            AutomationProperties.SetName(toggle, title);
            card.Children.Add(new SettingsRow().Set(title, description, toggle));
        }
    }
}
