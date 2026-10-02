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
    private readonly TextBlock _status = Copy(Loc.T("Speak into your microphone to check its input."), true);
    private readonly TextBlock _device = Copy("", true);
    private readonly TextBlock _explanation = Copy("", true);
    private readonly ProgressBar _level = new() { Minimum = 0, Maximum = 100, Height = 6, IsTabStop = false };
    private readonly DispatcherQueueTimer _timer;
    private bool _ownsTest;

    internal MicrophoneTestControl(LocalDictationSession session)
    {
        _session = session;
        Spacing = 8;
        Padding = new Thickness(0, 12, 0, 12);
        _button = new HandCursorButton
        {
            Content = Loc.T("Test microphone"), VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"]
        };
        _button.Click += (_, _) => ToggleTest();
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(SettingsHelp.Label(Loc.T("Microphone test"), Loc.T("Checks the microphone selected by your priority list. No audio is saved or sent. Stops automatically after 15 seconds.")));
        copy.Children.Add(_status);
        header.Children.Add(copy);
        Grid.SetColumn(_button, 1); header.Children.Add(_button);
        AutomationProperties.SetName(_level, Loc.T("Microphone input level"));
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _level.Visibility = Visibility.Collapsed;
        Children.Add(header);
        Children.Add(_device);
        Children.Add(_level);
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
        _level.Visibility = Visibility.Visible;
        _button.Content = Loc.T("Stop test");
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
            _status.Text = Loc.T("Could not stop the microphone test: {0}", ex.Message);
            _button.Content = Loc.T("Test microphone");
            _level.Value = 0;
            return;
        }
        if (_session.MicrophoneTest is { } test) Render(test);
        _button.Content = Loc.T("Test microphone");
        _level.Value = 0;
        _level.Visibility = Visibility.Collapsed;
    }

    private void Render(MicrophoneTestSnapshot test)
    {
        SetCopy(_device, test.DeviceName);
        _level.Value = test.Peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(test.Peak) + 60) / 60 * 100, 0, 100);
        var (status, explanation) = test.State switch
        {
            MicrophoneTestState.WaitingForPackets => (Loc.T("Waiting for microphone input…"), Loc.T("Speak into the microphone.")),
            MicrophoneTestState.NoPackets => (Loc.T("No audio received"), Loc.T("Check the microphone connection and Windows microphone access.")),
            MicrophoneTestState.WindowsSilent => (Loc.T("Windows reports silence"), Loc.T("Check the microphone mute switch and Windows input settings.")),
            MicrophoneTestState.ZeroSamples => (Loc.T("Microphone sends only silence"), Loc.T("Check mute controls, input volume and the audio driver.")),
            MicrophoneTestState.Signal => (Loc.T("Audio signal received"), Loc.T("Sound is reaching TypeWhisper.")),
            _ => (Loc.T("Microphone test failed"), test.Error ?? MicrophoneFailure.Generic)
        };
        _status.Text = test.Running ? status : Loc.T("Test stopped · {0}", status);
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
