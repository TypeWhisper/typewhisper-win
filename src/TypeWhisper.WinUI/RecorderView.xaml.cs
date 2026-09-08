using System.Diagnostics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class RecorderView : UserControl
{
    private RecorderController? _recorder;
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
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event Action<string>? TranscribeRequested;
    internal bool NeedsSaveRetry => _recorder?.State == RecorderState.SaveFailed || _recorder?.State is (RecorderState.Recording or RecorderState.Paused) && _recorder.Error is not null;

    public RecorderView()
    {
        InitializeComponent();
        RecorderTabs.SetItems([new("record", "Record"), new("recordings", "Recordings")], "record");
        RecorderTabs.SelectionChanged += id => ShowLibrary(id == "recordings");
        RecorderBreadcrumbs.SetItems(new("Quick Launch", () => LauncherRequested?.Invoke(this, EventArgs.Empty), "Back from recorder"), new("Recorder"));
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
    internal void SetPresented(bool presented) { _presented = presented; if (!presented) StopAudioPlayback(); Refresh(); if (presented) { if (_libraryOpen) BeginLibraryRefresh(); } }
    internal void FocusEntry() { if (_libraryOpen) LibraryEntries.Focus(FocusState.Programmatic); else PrimaryButton.Focus(FocusState.Programmatic); }
    internal void GoBack()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
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
        RecorderStatus.Text = _recorder?.Error ?? (state == RecorderState.Ready && _recordingDeleted ? "Recording deleted" : state switch
        {
            RecorderState.Recording => "Recording", RecorderState.Paused => "Paused · sources stopped", RecorderState.Saving => "Saving recording…",
            RecorderState.SaveFailed => "Could not save. Audio is retained for retry.", RecorderState.Saved => "Recording saved", _ => "Ready to record"
        });
        RecorderDuration.Text = (active ? ActiveDuration : _recorder?.Duration ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss");
        var selectedPreferences = active || busy || state == RecorderState.SaveFailed ? _preferencesAtStart : _recorderPreferences?.Current;
        var outputHint = selectedPreferences?.OutputDeviceId is null ? "default system output" : "selected system output (Recorder settings)";
        SessionHint.Text = _recorderPreferences?.Error ?? _capture?.Warning ?? $"Audio stays on this device. Up to 60 minutes per recording · {outputHint}.";
        RecordingName.IsEnabled = !busy && !active && state != RecorderState.SaveFailed;
        MicrophoneSource.IsEnabled = SystemSource.IsEnabled = !busy && !active && state != RecorderState.SaveFailed;
        MicrophoneState.Text = MicrophoneSource.IsChecked == true ? "On" : "Off";
        SystemState.Text = SystemSource.IsChecked == true ? "On" : "Off";
        PrimaryButton.Content = active ? "Stop and save \u00b7 Enter" : state == RecorderState.SaveFailed ? "Retry save \u00b7 Enter" : "Start recording \u00b7 Enter";
        PrimaryButton.IsEnabled = _recorder is not null && !busy && (active || state == RecorderState.SaveFailed || MicrophoneSource.IsChecked == true || SystemSource.IsChecked == true);
        PrimaryButton.Visibility = !_libraryOpen || active || state == RecorderState.SaveFailed ? Visibility.Visible : Visibility.Collapsed;
        LibraryRecordingStatus.Visibility = _libraryOpen && (active || busy || state == RecorderState.SaveFailed) ? Visibility.Visible : Visibility.Collapsed;
        LibraryRecordingStatus.Text = $"{RecorderStatus.Text} · {RecorderDuration.Text}";
        PauseButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = state == RecorderState.Paused ? "Resume" : "Pause";
        PauseButton.IsEnabled = active && !busy && !_automaticStop;
        DiscardButton.Visibility = DiscardConfirmation.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Visible;
        if (saved) { RecorderStatus.Text = "Ready for a new recording"; RecorderDuration.Text = "00:00:00"; }
        ViewHistoryButton.Visibility = OpenFolderButton.Visibility = Visibility.Collapsed;
        RefreshLibraryActions();
        if (_presented) SignalCanvas.Invalidate();
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
                _recordingTitle = RecorderWavStore.NormalizeTitle(SessionTitle);
                _recordingDeleted = false;
                _preferencesAtStart = _recorderPreferences?.Current ?? new RecorderPreferences();
                _capture?.BeginSession();
                await _recorder.StartAsync(_preferencesAtStart.MicrophoneEnabled, _preferencesAtStart.SystemAudioEnabled);
                _automaticStop = false; _timer.Start();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Recorder operation failed: {0}", ex); }
        Refresh();
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
    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    { if (_recorder?.FilePath is { } path) RequestTranscribe(path); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder?.FilePath is not { } path) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { RecorderStatus.Text = "Could not open the folder: " + ex.Message; }
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
    private void Discard_Click(object sender, RoutedEventArgs e) { }
    private void KeepSession_Click(object sender, RoutedEventArgs e) { }
    private void ConfirmDiscard_Click(object sender, RoutedEventArgs e) { }
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void SignalCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var level = _recorder?.State == RecorderState.Recording ? _capture?.Level ?? 0 : 0;
        args.DrawingSession.FillRoundedRectangle(0, 20, Math.Max(0, (float)sender.ActualWidth) * level, 10, 3, 3,
            global::Windows.UI.Color.FromArgb(255, 59, 167, 255));
    }
}
