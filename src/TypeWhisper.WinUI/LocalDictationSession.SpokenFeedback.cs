using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly PluginSpokenFeedbackBackend _speechBackend;
    internal SpokenFeedbackController SpokenFeedback { get; }
    internal IReadOnlyList<SpokenFeedbackVoice> GetSpokenFeedbackVoices() => _speechBackend.GetVoices();
    private DictationAudioPreferences _spokenFeedbackAtStart = new();
    private Task _spokenFeedbackActivity = Task.CompletedTask;
    internal Action? StopHistoryPlayback { get; set; }
    private SpokenFeedbackRequest? _historySpeechRequest;

    internal Task StopHistoryReadbackAsync() => _historySpeechRequest is { } request
        ? SpokenFeedback.CancelAndDrainAsync(request) : Task.CompletedTask;

    internal Task<SpokenFeedbackResult> ReadHistoryAsync(string text, string? language)
    {
        if (_disposed || !CanChangeProvider || Models.Busy || SpokenFeedback.IsBusy || !_gate.Wait(0))
            return Task.FromResult(new SpokenFeedbackResult(SpokenFeedbackStatus.Rejected,
                "Finish the current recording, playback or model operation before reading this transcript."));
        try
        {
            _historySpeechRequest = new(text, language, AudioPreferences.SpokenFeedbackVoiceId, AudioPreferences.OutputDeviceId);
            var playback = RunSpokenFeedbackAsync(_historySpeechRequest, reportFailure: false);
            _spokenFeedbackActivity = playback;
            return playback;
        }
        finally { _gate.Release(); }
    }

    internal Task<SpokenFeedbackResult> ToggleReadLastDictationAsync()
    {
        if (_disposed || (!SpokenFeedback.IsBusy && (!CanChangeProvider || Models.Busy)) || !_gate.Wait(0))
            return Task.FromResult(new SpokenFeedbackResult(SpokenFeedbackStatus.Rejected,
                "Finish the current recording or model operation before reading the last dictation."));
        try
        {
            var playback = ReadLastDictationCoreAsync();
            _spokenFeedbackActivity = playback;
            return playback;
        }
        finally { _gate.Release(); }
    }

    private async Task<SpokenFeedbackResult> ReadLastDictationCoreAsync()
    {
        StopHistoryPlayback?.Invoke();
        var playback = LastDictationReadback.ToggleAsync(SpokenFeedback, LastCompletedDictation,
            AudioPreferences.SpokenFeedbackVoiceId, AudioPreferences.OutputDeviceId);
        Changed?.Invoke();
        var result = await playback;
        if (!_disposed) Changed?.Invoke();
        return result;
    }

    internal Task<SpokenFeedbackResult> TestSpokenFeedbackAsync()
    {
        if (!CanChangeProvider || Models.Busy || SpokenFeedback.IsBusy || !_gate.Wait(0))
            return Task.FromResult(new SpokenFeedbackResult(SpokenFeedbackStatus.Rejected,
                "Finish the current recording or model operation before testing spoken feedback."));
        try
        {
            var playback = RunSpokenFeedbackAsync(new("This is a test of TypeWhisper spoken feedback.", "en",
                AudioPreferences.SpokenFeedbackVoiceId, AudioPreferences.OutputDeviceId), reportFailure: false);
            _spokenFeedbackActivity = playback;
            return playback;
        }
        finally { _gate.Release(); }
    }

    private void ReadCompletedDictation(TranscriptionRecord record, DictationOutputResult outcome, bool processingSucceeded)
    {
        if (_disposed || !SpokenFeedbackPolicy.ShouldSpeakAutomatically(
            _spokenFeedbackAtStart.SpokenFeedbackEnabled && AudioPreferences.SpokenFeedbackEnabled,
            processingSucceeded && record.Status == TranscriptionRecordStatus.Succeeded, !outcome.Failed, outcome.NeedsReview)) return;
        _spokenFeedbackActivity = RunSpokenFeedbackAsync(new(record.FinalText, record.Language,
            _spokenFeedbackAtStart.SpokenFeedbackVoiceId, _spokenFeedbackAtStart.OutputDeviceId), reportFailure: true);
    }

    private async Task<SpokenFeedbackResult> RunSpokenFeedbackAsync(SpokenFeedbackRequest request, bool reportFailure)
    {
        StopHistoryPlayback?.Invoke();
        var playback = SpokenFeedback.SpeakAsync(request);
        Changed?.Invoke();
        var result = await playback;
        if (!_disposed)
        {
            if (reportFailure && (result.Status is SpokenFeedbackStatus.Failed or SpokenFeedbackStatus.Rejected) &&
                _phase == DictationPhase.Completed)
                SetStatus(Status + " · " + result.Message, DictationPhase.Completed);
            else Changed?.Invoke();
        }
        return result;
    }

    private Task DrainSpokenFeedbackAsync() => Task.WhenAll(SpokenFeedback.ShutdownAsync(), _spokenFeedbackActivity);
}
