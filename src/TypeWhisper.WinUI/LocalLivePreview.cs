namespace TypeWhisper.WinUI;

// Uses the legacy local polling approach with a shorter preview interval. The host supplies its existing engine;
// no second model instance and no overlapping preview decodes are created.
internal sealed class LocalLivePreview(TimeSpan? interval = null) : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1500);
    private CancellationTokenSource? _cancellation;
    private Task _pending = Task.CompletedTask;
    internal void Start(Func<float[]?> snapshot, Func<float[], Task<string>> decode, Action<string> publish, Action<string> failed)
    {
        if (!_pending.IsCompleted) throw new InvalidOperationException("Drain the previous preview before starting another recording.");
        Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _pending = RunAsync(cancellation, snapshot, decode, publish, failed);
    }

    private async Task RunAsync(CancellationTokenSource cancellation, Func<float[]?> snapshot,
        Func<float[], Task<string>> decode, Action<string> publish, Action<string> failed)
    {
        var token = cancellation.Token;
        try
        {
            while (true)
            {
                await Task.Delay(interval ?? DefaultInterval, token);
                var samples = snapshot();
                if (samples is null || samples.Length < 8000) continue;
                var text = await decode(samples);
                if (token.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(text)) publish(text);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!token.IsCancellationRequested) failed(ex.Message); }
        finally { cancellation.Dispose(); }
    }

    internal void Cancel()
    {
        var previous = _cancellation;
        _cancellation = null;
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }
    internal async Task StopAsync() { Cancel(); await _pending; }
    public void Dispose() => Cancel();
}
