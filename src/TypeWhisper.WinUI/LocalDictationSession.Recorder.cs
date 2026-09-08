namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal TypeWhisper.Presentation.RecorderPreferencesStore RecorderPreferences { get; } = new(WinUIProfile.DataPath("recorder.json"));
    internal IReadOnlyList<TypeWhisper.Windows.Services.SystemAudioOutputDevice> GetRecorderOutputDevices()
    {
        using var systemAudio = new TypeWhisper.Windows.Services.SystemAudioCaptureService();
        return systemAudio.GetAvailableOutputDevices();
    }
    private bool _recorderReserved;
    internal RecorderCaptureAdapter CreateRecorderCapture() => new(_audio, _fileDispatcher);
    internal IDisposable ReserveRecorder()
    {
        if (SpokenFeedback.IsBusy)
            throw new InvalidOperationException("Stop spoken feedback in Audio settings before starting the recorder.");
        if (!CanChangeProvider || Models.Busy || !_gate.Wait(0))
            throw new InvalidOperationException("Finish dictation, file transcription or model setup before starting the recorder.");
        _recorderReserved = true;
        try { StopHistoryPlayback?.Invoke(); Changed?.Invoke(); return new RecorderReservation(this); }
        catch { _recorderReserved = false; _gate.Release(); throw; }
    }
    private sealed class RecorderReservation(LocalDictationSession session) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            session._recorderReserved = false;
            session._gate.Release();
            session.Changed?.Invoke();
        }
    }
}
