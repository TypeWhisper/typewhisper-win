using TypeWhisper.Core.Services;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly DictationRecoveryAudioStore _recoveryAudio = new(WinUIProfile.DataPath("dictation-recovery"));
    private DictationRecoveryPreferences _recoveryAtStart = new();
    internal DictationRecoveryPreferencesStore RecoveryPreferences { get; } = new(WinUIProfile.DataPath("recovery.json"));
    internal DictationRecoveryController Recovery { get; }
    private string? _reportedRecoveryError;

    private void ReportRecoveryStorageError()
    {
        if (_disposed || _recoveryAudio.LastError is not { } error || error == _reportedRecoveryError) return;
        _reportedRecoveryError = error;
        SetStatus(Status + " · " + error);
    }

    internal async Task<string?> SaveRecoveryPreferencesAsync(DictationRecoveryPreferences next)
    {
        if (_disposed || !CanChangeProvider || Recovery.Busy || !await _gate.WaitAsync(0))
            return "Finish dictation or recovery before changing recovery preferences.";
        try
        {
            if (_disposed) return "The app is shutting down.";
            if (!RecoveryPreferences.Save(next)) return RecoveryPreferences.Error;
            // Disabling stops future capture but must not delete existing recovery audio.
            await _recoveryAudio.SetRetentionAsync(next.Enabled ? next.RetentionDays : 0);
            return _recoveryAudio.LastError;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return "The recovery choice was saved, but audio cleanup could not finish. Existing audio may remain."; }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    private async Task FinishRecoveryLeaseAsync(RecoveryRecordingLease? lease, bool preserve)
    {
        if (lease is null) return;
        try
        {
            if (preserve && _recoveryAtStart.CanPreserveWith(RecoveryPreferences.Current))
            {
                if (await lease.PreserveAsync() is null && !_disposed)
                    SetStatus(Status + " · Recovery audio could not be preserved.");
            }
            else await lease.DiscardAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_disposed) SetStatus(Status + " · Recovery audio could not be updated; review saved recovery audio before retrying."); }
    }

    private async Task StopRecoveryCaptureAsync(bool preserve)
    {
        if (!_audio.IsRecording) return;
        var captured = await _audio.StopRecordingWithRecoveryAsync();
        await FinishRecoveryLeaseAsync(captured.RecoveryLease, preserve);
    }

    // Pure manual retry: no FileQueue acceptance, lexicon usage, History, workflow,
    // text processor, clipboard, or paste action. The controller owns result acceptance.
    private async Task<string> DecodeRecoveryAudioAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanTranscribeFile || !await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Select a ready model and finish the current operation first.");
        _fileBusy = true;
        try
        {
            ct = _operationCancellation.Begin(ct);
            Changed?.Invoke();
            var language = Language;
            var translate = TranscriptionTaskPreferences.Current == TypeWhisper.Core.Interfaces.TranscriptionTask.Translate;
            if (translate && !SupportsTranslation) throw new NotSupportedException("Select a translation-capable model first.");
            var registry = UsesRegistryProvider;
            var selection = RegistrySelectionId(_providerId);
            var hints = TextPreferences.Current.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries);
            await _livePreview.StopAsync();
            ct.ThrowIfCancellationRequested();
            var samples = await MediaFoundationFileDecoder.LoadAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            var decoded = registry
                ? await PluginRuntime.UseTranscriptionAsync(selection, (engine, token) =>
                    LanguageHintTranscription.DecodeAsync(engine, samples,
                        () => CloudTranscriptionPlugin.EncodeWav(samples, engine.ProviderId == "groq" ? 25_000_000 : int.MaxValue),
                        language == "auto" ? null : language, hints, translate, token), ct)
                : await _transcriptionPlugin.DecodeResultAsync(samples, language == "auto" ? null : language, translate, ct);
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrWhiteSpace(decoded.Text) || FinalSpeechPolicy.ShouldReject(decoded.Text,
                decoded.NoSpeechProbability, false, TextPreferences.Current.TranscribeShortQuietClipsAggressively))
                throw new InvalidOperationException("No speech was recognized in the saved audio.");
            return decoded.Text;
        }
        finally { _fileBusy = false; _gate.Release(); Changed?.Invoke(); }
    }
}
