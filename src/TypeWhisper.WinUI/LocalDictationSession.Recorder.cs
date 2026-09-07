namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private bool _recorderReserved;
    internal RecorderCaptureAdapter CreateRecorderCapture() => new(_audio, _fileDispatcher);
    internal IDisposable ReserveRecorder()
    {
        if (!CanChangeProvider || Models.Busy || !_gate.Wait(0))
            throw new InvalidOperationException("Finish dictation, file transcription or model setup before starting the recorder.");
        _recorderReserved = true;
        Changed?.Invoke();
        return new RecorderReservation(this);
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
