using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly OperationCancellationScope _operationCancellation = new();
    private readonly AsyncShutdownCoordinator _shutdown = new();
    internal bool CanCancelProcessing => !_disposed && !_fileBusy && _phase == DictationPhase.Processing && !_operationCancellation.Token.IsCancellationRequested;

    internal void RequestCancel()
    {
        try { _operationCancellation.Cancel(); }
        catch (AggregateException ex) { System.Diagnostics.Trace.TraceError("Operation cancellation callback failed: {0}", ex); }
        _livePreview.Cancel();
        if (!_disposed && !_fileBusy && _phase == DictationPhase.Processing)
            SetStatus("Canceling processing…", DictationPhase.Processing);
    }

    internal Task ShutdownAsync() => _shutdown.Run(() =>
    {
        _disposed = true;
        try { _operationCancellation.Close(); }
        catch (AggregateException ex) { System.Diagnostics.Trace.TraceError("Shutdown cancellation callback failed: {0}", ex); }
        _livePreview.Cancel();
        _retentionTimer.Stop();
        StopSilenceMonitoring();
        return DrainAndReleaseAsync();
    });

    private async Task DrainAndReleaseAsync()
    {
        await _gate.WaitAsync();
        var failures = new List<Exception>();
        async Task Release(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { failures.Add(ex); System.Diagnostics.Trace.TraceError("Shutdown step failed: {0}", ex); }
        }
        try
        {
            await Release(async () => { if (_audio.IsRecording) await _audio.StopRecordingAsync(); });
            await Release(_livePreview.StopAsync);
            await Release(() => CtcVocabulary.DisposeAsync().AsTask());
            await Release(() => Groq.DisposeAsync().AsTask());
            await Release(() => PluginRuntime.DisposeAsync().AsTask());
            await Release(() => _transcriptionPlugin.DisposeAsync().AsTask());
            await Release(() => { _effects.End(); _audio.Dispose(); return Task.CompletedTask; });
            await Release(() => { _inserter.Dispose(); _operationCancellation.Dispose(); return Task.CompletedTask; });
        }
        finally { _gate.Release(); }
        if (failures.Count > 0) throw new AggregateException("Some resources could not be shut down cleanly.", failures);
    }
}
