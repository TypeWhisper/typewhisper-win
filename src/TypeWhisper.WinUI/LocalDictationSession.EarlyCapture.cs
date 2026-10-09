using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Capture that began while the model was still loading. It runs outside the session gate until the
    // load releases it; the regular start then adopts the recording instead of opening the microphone.
    private bool _earlyCapture;
    private IntPtr _earlyTarget;
    private bool _earlyCancelled;
    // Samples captured when the user finished speaking before the model was ready. Later audio is dropped.
    private int? _earlyStopSamples;

    // The window changed while the model loaded. The speech is kept, but nothing is inserted or sent to a workflow action.
    private bool _reviewAfterTargetChange;

    private bool CanCaptureWhileModelLoads => !_disposed && _phase == DictationPhase.LoadingModel && !_earlyCapture
        && !_audio.IsRecording && !_fileBusy && !_recorderReserved && !_workflowReserved;

    // A loading model must not cost the first words: the microphone opens now and the audio waits for the model.
    private bool BeginEarlyCapture()
    {
        if (!CanCaptureWhileModelLoads) return false;
        var preferences = AudioPreferences;
        try
        {
            _earlyTarget = NativeMethods.GetForegroundWindow();
            _earlyStopSamples = null;
            _earlyCancelled = false;
            _audio.WhisperModeEnabled = preferences.WhisperModeEnabled;
            _recoveryAtStart = RecoveryPreferences.Current;
            // Whether the provider streams is only known once it has loaded.
            _streamAudio.Begin();
            AppDiagnostics.BeginDictation();
            AppDiagnostics.Write("dictation.capture.early");
            _audio.StartRecording(enableRecovery: _recoveryAtStart.Enabled && _recoveryAtStart.IsValid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("dictation.capture.failed", ex); }
        if (!_audio.IsRecording)
        {
            _streamAudio.Reset();
            AppDiagnostics.EndDictation();
            _showModelLoadingForDictation = true;
            Changed?.Invoke();
            return false;
        }
        _earlyCapture = true;
        BeginRecordingFeedback(preferences);
        Changed?.Invoke();
        return true;
    }

    private async Task AdoptEarlyCaptureAsync(AutomaticWorkflowSnapshot? workflow)
    {
        try
        {
            await _gate.WaitAsync();
            await SetRecordingAsync(true, workflow, adoptEarlyCapture: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { AppDiagnostics.WriteFailure("dictation.capture.adopt-failed", ex); }
    }

    // An adopted capture already holds speech, and a long load (such as a first CUDA download) leaves time to
    // switch windows. Its transcript goes to review instead of being discarded. Other starts still stop, since
    // nothing has been said yet and the shortcut can simply be pressed again.
    private bool KeepForReviewAfterTargetChange(bool adoptedEarlyCapture)
    {
        if (!adoptedEarlyCapture) return false;
        if (!_reviewAfterTargetChange) AppDiagnostics.Write("dictation.target-changed.review");
        _reviewAfterTargetChange = true;
        return true;
    }

    private void BeginRecordingFeedback(DictationAudioPreferences preferences)
    {
        _sounds.IsEnabled = preferences.SoundFeedbackEnabled;
        _sounds.OutputDeviceId = preferences.OutputDeviceId;
        _ducking.OutputDeviceId = preferences.OutputDeviceId;
        _effects.Begin(preferences);
        _sounds.PlayStartSound();
    }
}
