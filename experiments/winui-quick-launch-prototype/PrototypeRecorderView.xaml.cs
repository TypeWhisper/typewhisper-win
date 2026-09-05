using System.Diagnostics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TypeWhisper.WinUIPrototype;

public sealed partial class PrototypeRecorderView : UserControl
{
    private enum SessionState { Ready, Recording, Paused, Complete }
    private SessionState _state;
    private bool _initialized;
    private bool _usedMicrophone;
    private bool _usedSystemAudio;
    private bool AnySource => MicrophoneSource.IsChecked == true || SystemSource.IsChecked == true;
    private string SourceSummary => DescribeSources(MicrophoneSource.IsChecked == true, SystemSource.IsChecked == true);
    private readonly Stopwatch _elapsed = new();
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _presented;
    private bool _rendering;
    private bool _animationsEnabled;
    private long _displayedSecond = -1;
    private Guid _sessionId;
    private DateTimeOffset _sessionStartedAt;
    private string _sessionTitle = string.Empty;
    internal PrototypeHistoryEntry? CompletedEntry { get; private set; }
    internal string SessionTitle
    {
        get => _sessionTitle;
        set
        {
            _sessionTitle = value;
            if (CompletedEntry is not { } completed) return;
            CompletedEntry = completed with
            {
                Content = completed.Content with
                {
                    Title = EffectiveTitle,
                    UpdatedAt = DateTimeOffset.UtcNow > completed.Content.UpdatedAt
                        ? DateTimeOffset.UtcNow : completed.Content.UpdatedAt
                }
            };
            CompletedTitle.Text = EffectiveTitle;
            CompletedEntryChanged?.Invoke(CompletedEntry);
        }
    }
    private string EffectiveTitle => string.IsNullOrWhiteSpace(SessionTitle) ? "Demo session complete" : SessionTitle.Trim();
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event Action<PrototypeHistoryEntry>? CompletedEntryChanged;
    internal event Action<Guid>? OpenInHistoryRequested;

    public PrototypeRecorderView()
    {
        InitializeComponent();
        RecorderBreadcrumbs.SetItems(new("Quick Launch", () =>
        {
            if (DiscardConfirmation.Visibility == Visibility.Visible) GoBack();
            else LauncherRequested?.Invoke(this, EventArgs.Empty);
        }, "Back from recorder"), new("Recorder"));
        MicrophoneSource.IsChecked = true;
        _initialized = true;
        Unloaded += (_, _) => SetRendering(false);
        UpdatePresentation();
    }

    internal void SetPresented(bool presented)
    {
        _presented = presented;
        UpdatePresentation();
    }

    internal void FocusEntry() => PrimaryButton.Focus(FocusState.Programmatic);

    internal void GoBack()
    {
        if (DiscardConfirmation.Visibility == Visibility.Visible)
        {
            DismissDiscard();
            return;
        }
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePresentation()
    {
        var active = _state is SessionState.Recording or SessionState.Paused;
        var complete = _state == SessionState.Complete;
        UpdateSourceToggle(MicrophoneSource, MicrophoneState, MicrophoneIcon);
        UpdateSourceToggle(SystemSource, SystemState, SystemIcon);
        RecorderStatus.Text = _state switch
        {
            SessionState.Recording => AnySource ? "Recording demo" : "All sources muted",
            SessionState.Paused => "Paused",
            SessionState.Complete => "Demo complete",
            _ => "Ready to record"
        };
        RecorderStatus.Foreground = new SolidColorBrush(_state == SessionState.Paused || active && !AnySource
            ? Color.FromArgb(255, 244, 188, 106) : _state == SessionState.Recording
            ? Color.FromArgb(255, 59, 167, 255) : Color.FromArgb(255, 167, 181, 197));
        RecorderDuration.Text = _elapsed.Elapsed.ToString(@"mm\:ss");
        SessionHint.Text = active
            ? AnySource ? $"{SourceSummary} · simulated signal · safe to leave this page"
                : "All sources are off · the session continues silently"
            : AnySource ? "Switch sources on or off, even during a session."
                : "Enable at least one source to start a session.";
        PauseButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        DiscardButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = _state == SessionState.Paused ? "Resume" : "Pause";
        PrimaryButton.Content = active ? "Finish demo" : complete ? "New session" : "Start demo";
        PrimaryButton.IsEnabled = (_state != SessionState.Ready || AnySource)
            && DiscardConfirmation.Visibility != Visibility.Visible;
        SessionPanel.Visibility = complete ? Visibility.Collapsed : Visibility.Visible;
        CompletedPanel.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
        ViewHistoryButton.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
        if (complete)
        {
            CompletedTitle.Text = string.IsNullOrWhiteSpace(SessionTitle) ? "Demo session complete" : SessionTitle.Trim();
            CompletedMetadata.Text = $@"{_elapsed.Elapsed:mm\:ss} · {DescribeSources(_usedMicrophone, _usedSystemAudio)} · no audio file created";
        }
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _displayedSecond = (long)_elapsed.Elapsed.TotalSeconds;
        SetRendering(_presented && _state == SessionState.Recording);
        SignalCanvas.Invalidate();
    }

    private void SetRendering(bool enabled)
    {
        if (_rendering == enabled) return;
        _rendering = enabled;
        if (enabled) CompositionTarget.Rendering += RenderFrame;
        else CompositionTarget.Rendering -= RenderFrame;
    }

    private void RenderFrame(object? sender, object args)
    {
        // Follow WinUI's render cadence rather than a competing dispatcher timer.
        // Only touch text when its displayed value changes; bars need no layout.
        var elapsed = _elapsed.Elapsed;
        var second = (long)elapsed.TotalSeconds;
        if (second != _displayedSecond)
        {
            _displayedSecond = second;
            RecorderDuration.Text = elapsed.ToString(@"mm\:ss");
            var enabled = _uiSettings.AnimationsEnabled;
            if (enabled != _animationsEnabled) SignalCanvas.Invalidate();
            _animationsEnabled = enabled;
        }
        if (_animationsEnabled && AnySource) SignalCanvas.Invalidate();
    }

    private static string DescribeSources(bool microphone, bool systemAudio) => (microphone, systemAudio) switch
    {
        (true, true) => "Microphone + system audio",
        (true, false) => "Microphone",
        (false, true) => "System audio",
        _ => "All sources off"
    };

    private void UpdateSourceToggle(HandCursorToggleButton button, TextBlock label, UIElement icon)
    {
        var enabled = button.IsChecked == true;
        button.IsEnabled = _state != SessionState.Complete;
        button.Background = new SolidColorBrush(enabled ? Color.FromArgb(255, 18, 48, 74) : Color.FromArgb(255, 17, 25, 35));
        button.BorderBrush = new SolidColorBrush(enabled ? Color.FromArgb(255, 47, 131, 189) : Color.FromArgb(255, 43, 64, 84));
        label.Text = enabled ? "On" : "Off";
        label.Foreground = (Brush)Application.Current.Resources[enabled ? "AccentBrush" : "MutedBrush"];
        icon.Opacity = enabled ? 1 : 0.45;
    }

    private void RememberActiveSources()
    {
        if (_state != SessionState.Recording) return;
        _usedMicrophone |= MicrophoneSource.IsChecked == true;
        _usedSystemAudio |= SystemSource.IsChecked == true;
    }

    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        RememberActiveSources();
        UpdatePresentation();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_state == SessionState.Ready && !AnySource) return;
        if (_state is SessionState.Recording or SessionState.Paused)
        {
            _elapsed.Stop();
            _state = SessionState.Complete;
            var completedAt = DateTimeOffset.UtcNow;
            CompletedEntry = PrototypeHistoryEntry.CreateRecorderSample(_sessionId, _sessionStartedAt,
                completedAt >= _sessionStartedAt ? completedAt : _sessionStartedAt,
                EffectiveTitle, _elapsed.Elapsed.TotalSeconds,
                (_usedMicrophone ? PrototypeCaptureInputs.Microphone : PrototypeCaptureInputs.None)
                | (_usedSystemAudio ? PrototypeCaptureInputs.SystemAudio : PrototypeCaptureInputs.None));
            CompletedEntryChanged?.Invoke(CompletedEntry);
        }
        else if (_state == SessionState.Complete)
        {
            _state = SessionState.Ready;
            _elapsed.Reset();
            _usedMicrophone = _usedSystemAudio = false;
            CompletedEntry = null;
        }
        else
        {
            _elapsed.Restart();
            _sessionId = Guid.NewGuid();
            _sessionStartedAt = DateTimeOffset.UtcNow;
            CompletedEntry = null;
            _state = SessionState.Recording;
            RememberActiveSources();
        }
        UpdatePresentation();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_state == SessionState.Recording)
        {
            _state = SessionState.Paused;
            _elapsed.Stop();
        }
        else if (_state == SessionState.Paused)
        {
            _state = SessionState.Recording;
            _elapsed.Start();
            RememberActiveSources();
        }
        UpdatePresentation();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        DiscardConfirmation.Visibility = Visibility.Visible;
        SessionPanel.Opacity = 0.15;
        DiscardButton.IsEnabled = PauseButton.IsEnabled = PrimaryButton.IsEnabled = false;
        KeepSessionButton.Focus(FocusState.Programmatic);
    }

    private void DismissDiscard()
    {
        DiscardConfirmation.Visibility = Visibility.Collapsed;
        SessionPanel.Opacity = 1;
        DiscardButton.IsEnabled = PauseButton.IsEnabled = PrimaryButton.IsEnabled = true;
        PrimaryButton.Focus(FocusState.Programmatic);
    }

    private void KeepSession_Click(object sender, RoutedEventArgs e) => DismissDiscard();
    private void ConfirmDiscard_Click(object sender, RoutedEventArgs e)
    {
        _elapsed.Reset();
        _state = SessionState.Ready;
        _usedMicrophone = _usedSystemAudio = false;
        CompletedEntry = null;
        DismissDiscard();
        UpdatePresentation();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (CompletedEntry is { } entry) OpenInHistoryRequested?.Invoke(entry.RecordId);
    }

    private void SignalCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (sender.Size.Width <= 0) return;
        const int bars = 56;
        var slot = (float)sender.Size.Width / bars;
        var center = (float)sender.Size.Height / 2;
        var phase = _animationsEnabled ? _elapsed.Elapsed.TotalSeconds * 3 : 1;
        var active = _state == SessionState.Recording && AnySource;
        var color = _state == SessionState.Paused ? Color.FromArgb(255, 244, 188, 106)
            : Color.FromArgb(active ? (byte)255 : (byte)90, 59, 167, 255);
        for (var index = 0; index < bars; index++)
        {
            var envelope = Math.Sin(index * Math.PI / (bars - 1));
            var signal = (Math.Sin(index * 0.77 + phase) + Math.Sin(index * 0.23 - phase * 1.4) + 2) / 4;
            var height = active ? (float)(3 + signal * envelope * 42) : 3;
            args.DrawingSession.FillRoundedRectangle(index * slot + slot * 0.3f, center - height / 2,
                Math.Max(2, slot * 0.4f), height, 2, 2, color);
        }
    }
}
