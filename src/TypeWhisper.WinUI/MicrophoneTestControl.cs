using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed class MicrophoneTestControl : StackPanel
{
    private readonly LocalDictationSession _session;
    private readonly HandCursorButton _button;
    private readonly TextBlock _status = Copy("Speak into your microphone to check its input.");
    private readonly TextBlock _device = Copy("", true);
    private readonly TextBlock _explanation = Copy("", true);
    private readonly ProgressBar _level = new() { Minimum = 0, Maximum = 100, Height = 6, IsTabStop = false };
    private readonly DispatcherQueueTimer _timer;
    private bool _ownsTest;

    internal MicrophoneTestControl(LocalDictationSession session)
    {
        _session = session;
        Spacing = 8;
        Margin = new Thickness(0, 8, 0, 8);
        Children.Add(SettingsHelp.Label("Microphone test", "Checks the microphone selected by your priority list. No audio is saved or sent. Stops automatically after 15 seconds."));
        _button = new HandCursorButton
        {
            Content = "Test microphone", HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"]
        };
        _button.Click += (_, _) => ToggleTest();
        AutomationProperties.SetName(_level, "Microphone input level");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_button);
        Children.Add(_device);
        Children.Add(_level);
        Children.Add(_status);
        Children.Add(_explanation);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Update();
        Unloaded += (_, _) => StopTest();
    }

    private void ToggleTest()
    {
        if (_ownsTest) { StopTest(); return; }
        var error = _session.StartMicrophoneTest();
        if (error is not null)
        {
            _status.Text = error;
            SetCopy(_device, "");
            SetCopy(_explanation, "");
            _level.Value = 0;
            return;
        }
        _ownsTest = true;
        _button.Content = "Stop test";
        _timer.Start();
        Update();
    }

    private void Update()
    {
        if (!_ownsTest || _session.MicrophoneTest is not { } test) return;
        if (!test.Running || test.Duration >= TimeSpan.FromSeconds(15))
        {
            StopTest();
            return;
        }
        Render(test);
    }

    private void StopTest()
    {
        _timer.Stop();
        if (!_ownsTest) return;
        _ownsTest = false;
        try { _session.StopMicrophoneTest(); }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            _status.Text = "Could not stop the microphone test: " + ex.Message;
            _button.Content = "Test microphone";
            _level.Value = 0;
            return;
        }
        if (_session.MicrophoneTest is { } test) Render(test);
        _button.Content = "Test microphone";
        _level.Value = 0;
    }

    private void Render(MicrophoneTestSnapshot test)
    {
        SetCopy(_device, test.DeviceName);
        _level.Value = test.Peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(test.Peak) + 60) / 60 * 100, 0, 100);
        var (status, explanation) = test.State switch
        {
            MicrophoneTestState.WaitingForPackets => ("Waiting for microphone input…", "Speak into the microphone."),
            MicrophoneTestState.NoPackets => ("No audio received", "Check the microphone connection and Windows microphone access."),
            MicrophoneTestState.WindowsSilent => ("Windows reports silence", "Check the microphone mute switch and Windows input settings."),
            MicrophoneTestState.ZeroSamples => ("Microphone sends only silence", "Check mute controls, input volume and the audio driver."),
            MicrophoneTestState.Signal => ("Audio signal received", "Sound is reaching TypeWhisper."),
            _ => ("Microphone test failed", test.Error ?? MicrophoneFailure.Generic)
        };
        _status.Text = test.Running ? status : "Test stopped · " + status;
        SetCopy(_explanation, explanation);
    }

    private static void SetCopy(TextBlock copy, string text)
    {
        copy.Text = text;
        copy.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock Copy(string text, bool muted = false) => new()
    {
        Text = text, FontSize = muted ? 12 : 13, TextWrapping = TextWrapping.Wrap,
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };
}
