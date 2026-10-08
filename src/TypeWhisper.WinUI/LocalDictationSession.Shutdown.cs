using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly OperationCancellationScope _operationCancellation = new();
    private readonly AsyncShutdownCoordinator _shutdown = new();
    internal bool CanCancelProcessing => !_disposed && !_fileBusy && _phase == DictationPhase.Processing && !_operationCancellation.Token.IsCancellationRequested;

    internal void RequestCancel()
    {
        // Adoption begins a fresh cancellation scope, so a capture still waiting for its model is marked instead.
        if (_earlyCapture) _earlyCancelled = true;
        try { _operationCancellation.Cancel(); }
        catch (AggregateException ex) { AppDiagnostics.WriteFailure("dictation.cancel.callback-failed", ex); }
        _livePreview.Cancel();
        _cloudStream?.Cancel();
        if (!_disposed && !_fileBusy && _phase == DictationPhase.Processing)
            SetStatus(Loc.T("Canceling processing…"), DictationPhase.Processing);
    }

    internal Task ShutdownAsync() => _shutdown.Run(() =>
    {
        _disposed = true;
        _audio.SuspendMicrophonePreroll(true);
        _lastCompletedDictation.Close();
        CtcVocabulary.RequestCancelActivation();
        try { _operationCancellation.Close(); }
        catch (AggregateException ex) { AppDiagnostics.WriteFailure("dictation.shutdown.cancel-failed", ex); }
        _livePreview.Cancel();
        _cloudStream?.Cancel();
        _retentionTimer.Stop();
        StopSilenceMonitoring();
        return DrainAndReleaseAsync(Task.WhenAll(_training?.DisposeAsync().AsTask() ?? Task.CompletedTask, CorrectionLearning.Cancel(), Packages.Updates.ShutdownAsync(), HistoryRetention.CloseAndDrainAsync(), Recovery.ShutdownAsync(),
            DrainSpokenFeedbackAsync(), RegistryModelDownload.ShutdownAsync(), LocalLlmDownload.ShutdownAsync()));
    });

    private async Task DrainAndReleaseAsync(Task retentionDrain)
    {
        await retentionDrain;
        await _gate.WaitAsync();
        var failures = new List<Exception>();
        async Task Release(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { failures.Add(ex); AppDiagnostics.WriteFailure("dictation.shutdown.step-failed", ex); }
        }
        try
        {
            await Release(() => StopRecoveryCaptureAsync(preserve: true));
            await Release(_livePreview.StopAsync);
            await Release(StopCloudStreamAsync);
            // A recording still open at exit has not restored the model its workflow replaced.
            await Release(RestoreWorkflowModelAsync);
            await Release(() => CtcVocabulary.DisposeAsync().AsTask());
            await Release(() => PluginRuntime.DisposeAsync().AsTask());
            await Release(() => _transcriptionPlugin.DisposeAsync().AsTask());
            await Release(() => { _effects.End(); _audio.Dispose(); return Task.CompletedTask; });
            await Release(() => _recoveryAudio.DisposeAsync().AsTask());
            await Release(() => _inserter.Restored);
            await Release(() => { _originalField?.Dispose(); _originalField = null; _inserter.Dispose(); _operationCancellation.Dispose(); return Task.CompletedTask; });
        }
        finally { _gate.Release(); }
        if (failures.Count > 0) throw new AggregateException("Some resources could not be shut down cleanly.", failures);
    }
}
