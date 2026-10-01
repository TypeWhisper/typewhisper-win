using System.Diagnostics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class RecorderView : UserControl
{
    private RecorderController? _recorder;
    private readonly HandCursorButton _audioSourceHelp = SettingsHelp.Button(Loc.T("Audio source"), Loc.T("Choose a source, then start your session. Audio stays on this device. Up to 60 minutes per recording."));
    private string? _audioSourceHelpText;
    private RecorderCaptureAdapter? _capture;
    private TimeSpan ActiveDuration => (_recorder?.Duration ?? TimeSpan.Zero)
        + (_recorder?.State == RecorderState.Recording ? _capture?.SegmentElapsed ?? TimeSpan.Zero : TimeSpan.Zero);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _initialized;
    private bool _presented;
    private bool _automaticStop;
    private string _recordingTitle = "";
    private bool _recordingDeleted;
    private RecorderPreferencesStore? _recorderPreferences;
    private RecorderPreferences _preferencesAtStart = new();
    private bool _updatingRecorderPreferences;
    internal string SessionTitle { get; set; } = "";
    internal event Action<string>? TranscribeRequested;
    internal bool NeedsSaveRetry => _recorder?.State == RecorderState.SaveFailed || _recorder?.State is (RecorderState.Recording or RecorderState.Paused) && _recorder.Error is not null;

    public RecorderView()
    {
        InitializeComponent();
        RecorderTitle.Text = Loc.T("Recorder");
        RecordingName.Header = Loc.T("Recording name (optional)");
        RecordingName.PlaceholderText = Loc.T("Untitled recording");
        AutomationProperties.SetName(RecordingName, Loc.T("Recording name"));
        AutomationProperties.SetName(MicrophoneSource, Loc.T("Recorder source microphone"));
        MicrophoneLabel.Text = Loc.T("Microphone");
        AutomationProperties.SetName(SystemSource, Loc.T("Recorder source system audio"));
        SystemLabel.Text = Loc.T("System audio");
        LibraryStatus.Text = Loc.T("Saved recordings");
        LibraryRefreshButton.Content = Loc.T("Refresh");
        LibraryStopPlaybackButton.Content = Loc.T("Stop playback");
        LibraryQueueButton.Content = Loc.T("Transcribe · T");
        LibraryFolderButton.Content = Loc.T("Folder · F");
        LibraryDeleteButton.Content = Loc.T("Delete · Del");
        AutomationProperties.SetName(PauseButton, Loc.T("Pause or resume this recording"));
        AutomationProperties.SetName(PrimaryButton, Loc.T("Recorder primary action"));
        EntryActionMenu.Attach(this, () => EntryActionMenu.FromButtons(ContextActionsFooter));
        var sourceLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sourceLabel.Children.Add(new TextBlock { Text = Loc.T("Audio source"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        sourceLabel.Children.Add(_audioSourceHelp);
        AudioSourceHelp.Child = sourceLabel;
        RecorderTabs.SetItems([new("record", Loc.T("Record")), new("recordings", Loc.T("Recordings"))], "record");
        RecorderTabs.SelectionChanged += id => ShowLibrary(id == "recordings");
        MicrophoneSource.IsChecked = true;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += async (_, _) =>
        {
            Refresh();
            if (_recorder?.State == RecorderState.Recording && !_recorder.Busy && ActiveDuration >= TimeSpan.FromHours(1) && !_automaticStop)
            {
                _automaticStop = true; _timer.Stop();
                try { await _recorder.StopAtLimitAsync(ActiveDuration); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Automatic recorder save failed: {0}", ex); }
                Refresh();
            }
        };
        _initialized = true;
        Refresh();
    }
    internal void Connect(LocalDictationSession session)
    {
        if (_recorder is not null) return;
        _recorderPreferences = session.RecorderPreferences;
        _recorderPreferences.Changed += OnRecorderPreferencesChanged;
        _capture = session.CreateRecorderCapture();
        _recorder = new(session.ReserveRecorder, (microphone, system) => _capture.StartAsync(microphone, system, _preferencesAtStart.OutputDeviceId), _capture.StopAsync,
            samples => RecorderWavStore.SaveAsync(WinUIProfile.DataPath("recordings"), samples, _recordingTitle));
        _recorder.Changed += Refresh;
        Refresh();
    }
    // Settings render the saved recorder defaults below the session controls, as on macOS.
    internal StackPanel DefaultsPanel => RecorderDefaults;
    internal void SetPresented(bool presented) { _presented = presented; if (!presented) StopAudioPlayback(); Refresh(); if (presented) { if (_libraryOpen) BeginLibraryRefresh(); } }
    internal void FocusEntry() { if (_libraryOpen) LibraryEntries.Focus(FocusState.Programmatic); else PrimaryButton.Focus(FocusState.Programmatic); }
    internal bool IsRecording { get; private set; }
    internal bool CanToggleRecording { get; private set; }
    internal event Action? RecordingStateChanged;

    // The tray's primary action, as in the macOS menu bar: starts with the saved sources or stops and saves.
    internal async Task<bool> ToggleRecordingAsync()
    {
        if (_recorder is null || _recorder.Busy || !CanToggleRecording) return false;
        try
        {
            if (_recorder.State is RecorderState.Recording or RecorderState.Paused) await StopAsync();
            else await StartRecordingAsync(_recorderPreferences?.Current ?? new RecorderPreferences());
            return _recorder.Error is null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Recorder operation failed: {0}", ex); return false; }
        finally { Refresh(); }
    }
    internal async Task ShutdownAsync()
    {
        if (_recorder is null) return;
        await _recorder.ShutdownAsync();
        await ShutdownLibraryAsync();
        if (_recorderPreferences is not null) _recorderPreferences.Changed -= OnRecorderPreferencesChanged;
        _timer.Stop(); _capture?.Dispose();
    }
    private void Refresh()
    {
        if (!_initialized) return;
        UpdateApiRecorderSession();
        var state = _recorder?.State ?? RecorderState.Ready;
        var busy = _recorder?.Busy == true;
        var active = state is RecorderState.Recording or RecorderState.Paused;
        if (!busy && !active && state != RecorderState.SaveFailed && _recorderPreferences is not null)
        {
            _updatingRecorderPreferences = true;
            MicrophoneSource.IsChecked = _recorderPreferences.Current.MicrophoneEnabled;
            SystemSource.IsChecked = _recorderPreferences.Current.SystemAudioEnabled;
            _updatingRecorderPreferences = false;
        }
        var saved = state == RecorderState.Saved && _recorder?.Error is null;
        if (saved && _librarySavedPath != _recorder?.FilePath)
        {
            _librarySavedPath = _recorder?.FilePath;
            _selectRecordingPath = _librarySavedPath;
            ShowLibrary(true);
        }
        RecorderStatus.Text = _recorder?.Error ?? (state == RecorderState.Ready && _recordingDeleted ? Loc.T("Recording deleted") : state switch
        {
            RecorderState.Recording => Loc.T("Recording"), RecorderState.Paused => Loc.T("Paused · sources stopped"), RecorderState.Saving => Loc.T("Saving recording…"),
            RecorderState.SaveFailed => Loc.T("Could not save. Audio is retained for retry."), RecorderState.Saved => Loc.T("Recording saved"), _ => Loc.T("Ready to record")
        });
        RecorderDuration.Text = (active ? ActiveDuration : _recorder?.Duration ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss");
        var selectedPreferences = active || busy || state == RecorderState.SaveFailed ? _preferencesAtStart : _recorderPreferences?.Current;
        var sourceHelp = selectedPreferences?.OutputDeviceId is null
            ? Loc.T("Choose a source, then start your session. Audio stays on this device. Up to 60 minutes per recording · default system output.")
            : Loc.T("Choose a source, then start your session. Audio stays on this device. Up to 60 minutes per recording · selected system output (Recorder settings).");
        if (_audioSourceHelpText != sourceHelp)
        {
            _audioSourceHelpText = sourceHelp;
            SettingsHelp.Update(_audioSourceHelp, sourceHelp);
        }
        SessionHint.Text = _recorderPreferences?.Error ?? _capture?.Warning ?? "";
        SessionHint.Visibility = string.IsNullOrEmpty(SessionHint.Text) ? Visibility.Collapsed : Visibility.Visible;
        RecordingName.IsEnabled = !busy && !active && state != RecorderState.SaveFailed;
        MicrophoneSource.IsEnabled = SystemSource.IsEnabled = !busy && !active && state != RecorderState.SaveFailed;
        MicrophoneState.Text = MicrophoneSource.IsChecked == true ? Loc.T("On") : Loc.T("Off");
        SystemState.Text = SystemSource.IsChecked == true ? Loc.T("On") : Loc.T("Off");
        PrimaryButton.Content = active ? Loc.T("Stop and save · Enter") : state == RecorderState.SaveFailed ? Loc.T("Retry save · Enter") : Loc.T("Start recording · Enter");
        PrimaryButton.IsEnabled = _recorder is not null && !busy && (active || state == RecorderState.SaveFailed || MicrophoneSource.IsChecked == true || SystemSource.IsChecked == true);
        PrimaryButton.Visibility = !_libraryOpen || active || state == RecorderState.SaveFailed ? Visibility.Visible : Visibility.Collapsed;
        LibraryRecordingStatus.Visibility = _libraryOpen && (active || busy || state == RecorderState.SaveFailed) ? Visibility.Visible : Visibility.Collapsed;
        LibraryRecordingStatus.Text = $"{RecorderStatus.Text} · {RecorderDuration.Text}";
        PauseButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = state == RecorderState.Paused ? Loc.T("Resume") : Loc.T("Pause");
        PauseButton.IsEnabled = active && !busy && !_automaticStop;
        SessionPanel.Visibility = Visibility.Visible;
        if (saved) { RecorderStatus.Text = Loc.T("Ready for a new recording"); RecorderDuration.Text = "00:00:00"; }
        RefreshLibraryActions();
        if (_presented) SignalCanvas.Invalidate();
        var toggle = (active, _recorder is not null && !busy && (active || state != RecorderState.SaveFailed
            && (MicrophoneSource.IsChecked == true || SystemSource.IsChecked == true)));
        if (toggle != (IsRecording, CanToggleRecording))
        {
            (IsRecording, CanToggleRecording) = toggle;
            RecordingStateChanged?.Invoke();
        }
    }
    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null) return;
        if (_libraryOpen && _recorder.State is not (RecorderState.Recording or RecorderState.Paused or RecorderState.SaveFailed)) return;
        try
        {
            if (_recorder.State is RecorderState.Recording or RecorderState.Paused) { await StopAsync(); return; }
            if (_recorder.State == RecorderState.SaveFailed) await _recorder.RetrySaveAsync();
            else
            {
                await StartRecordingAsync(_recorderPreferences?.Current ?? new RecorderPreferences());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Recorder operation failed: {0}", ex); }
        Refresh();
    }
    private async Task StartRecordingAsync(RecorderPreferences preferences)
    {
        if (_recorder is null) throw new InvalidOperationException("The recorder is unavailable.");
        // A new UI recording must not overwrite the previous API session's polling result.
        if (_apiRecorderSession is { Started: true }) _apiRecorderSession = null;
        _recordingTitle = RecorderWavStore.NormalizeTitle(SessionTitle);
        _recordingDeleted = false;
        _preferencesAtStart = preferences;
        _capture?.BeginSession();
        await _recorder.StartAsync(preferences.MicrophoneEnabled, preferences.SystemAudioEnabled);
        _updatingRecorderPreferences = true;
        MicrophoneSource.IsChecked = preferences.MicrophoneEnabled;
        SystemSource.IsChecked = preferences.SystemAudioEnabled;
        _updatingRecorderPreferences = false;
        _automaticStop = false; _timer.Start();
    }
    private async Task StopAsync()
    {
        if (_recorder is null) return;
        _timer.Stop();
        try { await _recorder.StopAndSaveAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Recorder saving failed: {0}", ex); }
        Refresh();
    }
    private void RecordingName_Changed(object sender, TextChangedEventArgs e) => SessionTitle = RecordingName.Text;
    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingRecorderPreferences) return;
        if (_initialized && _recorderPreferences is not null && _recorder?.Busy != true && _recorder?.State is not (RecorderState.Recording or RecorderState.Paused or RecorderState.SaveFailed))
            _recorderPreferences.Save(_recorderPreferences.Current with
            { MicrophoneEnabled = MicrophoneSource.IsChecked == true, SystemAudioEnabled = SystemSource.IsChecked == true });
        Refresh();
    }
    private void OnRecorderPreferencesChanged()
    {
        if (DispatcherQueue.HasThreadAccess) Refresh();
        else DispatcherQueue.TryEnqueue(Refresh);
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null || _recorder.Busy) return;
        try
        {
            if (_recorder.State == RecorderState.Recording) await _recorder.PauseAsync();
            else if (_recorder.State == RecorderState.Paused) await _recorder.ResumeAsync();
            if (_recorder.State == RecorderState.Paused && _recorder.Duration >= TimeSpan.FromHours(1))
            { _automaticStop = true; await StopAsync(); }
            else if (_recorder.State == RecorderState.Recording) _timer.Start();
            else _timer.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Trace.TraceError("Recorder pause or resume failed: {0}", ex); }
        finally { Refresh(); }
    }
    private void SignalCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var level = _recorder?.State == RecorderState.Recording ? _capture?.Level ?? 0 : 0;
        args.DrawingSession.FillRoundedRectangle(0, 20, Math.Max(0, (float)sender.ActualWidth) * level, 10, 3, 3,
            global::Windows.UI.Color.FromArgb(255, 10, 132, 255));
    }
}
