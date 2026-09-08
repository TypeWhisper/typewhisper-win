using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.WinUI;

internal static class LiveRecorderSettings
{
    internal static void Configure(string category, StackPanel parent, List<ChoicePicker> pickers,
        RecorderPreferencesStore preferences, Func<IReadOnlyList<SystemAudioOutputDevice>> getDevices)
    {
        if (category != "Recorder") return;
        parent.Children.Clear();
        // The catalog parent is reused between categories; this child owns exactly this binding's events.
        var content = new StackPanel { Spacing = 12 };
        parent.Children.Add(content);
        pickers.Clear();
        content.Children.Add(SettingsHelp.Label("Recorder", "Source choices are saved for your next recording. Changes never switch sources during an active recording.", 24));
        var microphone = AppToggleSwitch.Create(preferences.Current.MicrophoneEnabled);
        var system = AppToggleSwitch.Create(preferences.Current.SystemAudioEnabled);
        AddToggle("Microphone on by default", microphone);
        AddToggle("System audio on by default", system);
        content.Children.Add(SettingsHelp.Label("System audio device", "The microphone uses your Audio settings priority list. This output selection controls which system audio is recorded, independently of feedback sounds."));
        var device = new ChoicePicker();
        device.Configure("System audio device", "speaker", "Recorder system audio device");
        content.Children.Add(device); pickers.Add(device);
        var refreshDevices = new HandCursorButton { Content = "Refresh devices", HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        content.Children.Add(refreshDevices);
        content.Children.Add(SettingsHelp.Label("Audio format: WAV · 16 kHz mono", "Recordings are saved locally. Choose Transcribe on a saved recording to process it."));
        content.Children.Add(Label("Tracks: Mixed · microphone ducking off", 14));
        var status = Label("");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        content.Children.Add(status);
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
                "Saved device · unavailable", "Reconnect it or choose another device. No automatic fallback.")]).ToArray();
            device.SetOptions(options, id);
            status.Text = preferences.Error ?? deviceError ?? (!available
                ? "The saved output is unavailable. Reconnect it or choose another output before recording system audio."
                : "Saved · applies to the next recording.");
            refreshing = false;
        }
        void RefreshDevices()
        {
            var available = new List<Choice> { new("", "System default", "Windows default audio output") };
            deviceError = null;
            try
            {
                available.AddRange(getDevices().Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .DistinctBy(item => item.Id).Select(item => new Choice(item.Id!, item.Name, "System audio capture source")));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { deviceError = "Audio devices could not be listed. Your saved selection is unchanged."; }
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

        void AddToggle(string title, ToggleSwitch toggle)
        {
            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Label(title, 14);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            AutomationProperties.SetName(toggle, title);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            content.Children.Add(row);
        }
    }

    private static TextBlock Label(string text, double size = 12) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
}
