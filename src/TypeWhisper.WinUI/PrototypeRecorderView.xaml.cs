using System.Diagnostics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeRecorderView : UserControl
{
    private RecorderController? _recorder;
    private RecorderCaptureAdapter? _capture;
    private readonly Stopwatch _elapsed = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _initialized;
    private bool _presented;
    private bool _automaticStop;
    private string _recordingTitle = "";
    private bool _recordingDeleted;
    internal string SessionTitle { get; set; } = "";
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event Action<string>? TranscribeRequested;
    internal bool NeedsSaveRetry => _recorder?.State == RecorderState.SaveFailed || _recorder?.State == RecorderState.Recording && _recorder.Error is not null;

    public PrototypeRecorderView()
    {
        InitializeComponent();
        RecorderBreadcrumbs.SetItems(new("Quick Launch", () => LauncherRequested?.Invoke(this, EventArgs.Empty), "Back from recorder"), new("Recorder"));
        MicrophoneSource.IsChecked = true;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += async (_, _) =>
        {
            Refresh();
            if (_recorder?.State == RecorderState.Recording && _elapsed.Elapsed >= TimeSpan.FromHours(1) && !_automaticStop)
            {
                _automaticStop = true; _elapsed.Stop(); _timer.Stop();
                try { await _recorder.StopAtLimitAsync(_elapsed.Elapsed); }
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
        _capture = session.CreateRecorderCapture();
        _recorder = new(session.ReserveRecorder, _capture.StartAsync, _capture.StopAsync,
            samples => RecorderWavStore.SaveAsync(WinUIProfile.DataPath("recordings"), samples, _recordingTitle));
        _recorder.Changed += Refresh;
        Refresh();
    }
    internal void SetPresented(bool presented) { _presented = presented; Refresh(); if (presented && _libraryOpen) BeginLibraryRefresh(); }
    internal void FocusEntry() => (_libraryOpen ? LibraryRefreshButton : PrimaryButton).Focus(FocusState.Programmatic);
    internal void GoBack()
    {
        if (_libraryOpen) { Library_Click(this, new RoutedEventArgs()); return; }
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
    internal async Task ShutdownAsync()
    {
        if (_recorder is null) return;
        await _recorder.ShutdownAsync();
        await ShutdownLibraryAsync();
        _timer.Stop(); _capture?.Dispose();
    }
    private void Refresh()
    {
        if (!_initialized) return;
        var state = _recorder?.State ?? RecorderState.Ready;
        var busy = _recorder?.Busy == true;
        var active = state == RecorderState.Recording;
        var saved = state == RecorderState.Saved && _recorder?.Error is null;
        if (saved && _librarySavedPath != _recorder?.FilePath)
        {
            _librarySavedPath = _recorder?.FilePath;
            if (_libraryOpen) BeginLibraryRefresh();
        }
        RecorderStatus.Text = _recorder?.Error ?? (state == RecorderState.Ready && _recordingDeleted ? "Recording deleted" : state switch
        {
            RecorderState.Recording => "Recording", RecorderState.Saving => "Saving recording…",
            RecorderState.SaveFailed => "Could not save. Audio is retained for retry.", RecorderState.Saved => "Recording saved", _ => "Ready to record"
        });
        RecorderDuration.Text = (active ? _elapsed.Elapsed : _recorder?.Duration ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss");
        SessionHint.Text = _capture?.Warning ?? "WAV · default system output · maximum 60 minutes, then automatic stop and save. Pause and source changes during recording are unavailable.";
        MicrophoneSource.IsEnabled = SystemSource.IsEnabled = !busy && !active && state != RecorderState.SaveFailed;
        MicrophoneState.Text = MicrophoneSource.IsChecked == true ? "On" : "Off";
        SystemState.Text = SystemSource.IsChecked == true ? "On" : "Off";
        PrimaryButton.Content = active ? "Stop and save" : state == RecorderState.SaveFailed ? "Retry save" : "Start recording";
        PrimaryButton.IsEnabled = _recorder is not null && !busy && (active || state == RecorderState.SaveFailed || MicrophoneSource.IsChecked == true || SystemSource.IsChecked == true);
        PrimaryButton.Visibility = !_libraryOpen || active || state == RecorderState.SaveFailed ? Visibility.Visible : Visibility.Collapsed;
        LibraryRecordingStatus.Visibility = _libraryOpen && (active || busy || state == RecorderState.SaveFailed) ? Visibility.Visible : Visibility.Collapsed;
        LibraryRecordingStatus.Text = $"{RecorderStatus.Text} · {RecorderDuration.Text}";
        PauseButton.Visibility = DiscardButton.Visibility = DiscardConfirmation.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        CompletedPanel.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        CompletedTitle.Text = _recordingTitle.Length == 0 ? "Recording saved" : _recordingTitle;
        CompletedMetadata.Text = $"{_recorder?.Duration.ToString(@"hh\:mm\:ss")} · {_recorder?.FilePath}";
        ViewHistoryButton.Content = "Transcribe file…";
        ViewHistoryButton.Visibility = OpenFolderButton.Visibility = saved && !_libraryOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_presented) SignalCanvas.Invalidate();
    }
    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null) return;
        if (_libraryOpen && _recorder.State is not (RecorderState.Recording or RecorderState.SaveFailed)) return;
        try
        {
            if (_recorder.State == RecorderState.Recording) { await StopAsync(); return; }
            if (_recorder.State == RecorderState.SaveFailed) await _recorder.RetrySaveAsync();
            else
            {
                _recordingTitle = RecorderWavStore.NormalizeTitle(SessionTitle);
                _recordingDeleted = false;
                await _recorder.StartAsync(MicrophoneSource.IsChecked == true, SystemSource.IsChecked == true);
                _elapsed.Restart(); _automaticStop = false; _timer.Start();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Recorder operation failed: {0}", ex); }
        Refresh();
    }
    private async Task StopAsync()
    {
        if (_recorder is null) return;
        _elapsed.Stop(); _timer.Stop();
        try { await _recorder.StopAndSaveAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("Recorder saving failed: {0}", ex); }
        Refresh();
    }
    private void Source_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    { if (_recorder?.FilePath is { } path) RequestTranscribe(path); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder?.FilePath is not { } path) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { RecorderStatus.Text = "Could not open the folder: " + ex.Message; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { }
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
