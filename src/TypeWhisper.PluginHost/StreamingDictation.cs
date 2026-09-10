using System.Buffers.Binary;
using System.Threading.Channels;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>Feeds a retained provider ordered PCM chunks and publishes confirmed text plus its current interim segment.</summary>
public sealed class StreamingDictation : IAsyncDisposable
{
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cancel;
    private readonly Task<string?> _result;
    private int _closed;
    private int _inputFailed;
    private long _acceptedSamples;
    /// <summary>Unambiguous language reported by a finalized segment.</summary>
    public string? DetectedLanguage { get; private set; }

    /// <summary>The callback must retain the provider for the entire streaming operation.</summary>
    public StreamingDictation(
        Func<Func<ITranscriptionEnginePlugin, CancellationToken, Task<string>>, CancellationToken, Task<string>> use,
        IReadOnlyList<string> languages, Action<string> publish, Action failed, CancellationToken ct, IReadOnlyList<string>? dictionaryTerms = null)
    {
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _result = RunAsync(use, languages.ToArray(), publish, failed, dictionaryTerms?.ToArray());
    }

    /// <summary>Copies capture samples without blocking the audio thread. An overflow invalidates the entire stream.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        if (Volatile.Read(ref _closed) != 0 || _result.IsCompleted) return;
        if (samples.Length > 16000 * 5) { FailInput(); return; }
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i])) { FailInput(); return; }
            var value = (short)Math.Clamp((int)Math.Round(Math.Clamp(samples[i], -1, 1) * 32768), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), value);
        }
        if (!_audio.Writer.TryWrite(pcm)) FailInput();
        else Interlocked.Add(ref _acceptedSamples, samples.Length);
    }

    private void FailInput()
    {
        Interlocked.Exchange(ref _inputFailed, 1);
        Cancel();
    }

    private async Task<string?> RunAsync(
        Func<Func<ITranscriptionEnginePlugin, CancellationToken, Task<string>>, CancellationToken, Task<string>> use,
        IReadOnlyList<string> languages, Action<string> publish, Action failed, IReadOnlyList<string>? dictionaryTerms)
    {
        try
        {
            return await use(async (engine, ct) =>
            {
                var prompt = LanguageHintTranscription.CreateDictionaryPrompt(engine, dictionaryTerms);
                if (!engine.SupportsStreaming || !engine.SupportsStreamingCompletion || !engine.SupportsStreamingForPrompt(prompt)) throw new NotSupportedException("Live transcription is unavailable.");
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await using var session = await engine.StartStreamingWithLanguageHintsAndPromptAsync(languages, prompt, connect.Token).ConfigureAwait(false);
                connect.CancelAfter(Timeout.InfiniteTimeSpan);
                var confirmed = new List<string>();
                void OnTranscript(StreamingTranscriptEvent update)
                {
                    if (ct.IsCancellationRequested) return;
                    if (update.IsFinal && !string.IsNullOrWhiteSpace(update.Text)) confirmed.Add(update.Text.Trim());
                    if (update.IsFinal && update.DetectedLanguage is not null) DetectedLanguage = update.DetectedLanguage;
                    var text = string.Join(" ", update.IsFinal || string.IsNullOrWhiteSpace(update.Text)
                        ? confirmed : confirmed.Append(update.Text.Trim()));
                    publish(text);
                }
                session.TranscriptReceived += OnTranscript;
                try
                {
                    await foreach (var pcm in _audio.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        using var send = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        send.CancelAfter(TimeSpan.FromSeconds(10));
                        await session.SendAudioAsync(pcm, send.Token).ConfigureAwait(false);
                    }
                    using var finish = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    finish.CancelAfter(TimeSpan.FromSeconds(15));
                    await session.FinalizeAsync(finish.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return string.Join(" ", confirmed);
                }
                finally { session.TranscriptReceived -= OnTranscript; }
            }, _cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_cancel.IsCancellationRequested || Volatile.Read(ref _inputFailed) != 0) failed();
            return null;
        }
    }

    /// <summary>Drains every accepted chunk and the final provider response. Null requires full-recording fallback.</summary>
    public async Task<string?> FinishAsync(int? expectedSamples = null)
    {
        Interlocked.Exchange(ref _closed, 1);
        if (expectedSamples is { } expected && expected != Interlocked.Read(ref _acceptedSamples))
            FailInput();
        else _audio.Writer.TryComplete();
        try { return await _result.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Bound the whole stop operation, including a slow backlog, not just individual sends.
            FailInput();
            return await _result.ConfigureAwait(false);
        }
    }

    /// <summary>Stops sending audio and suppresses subsequent preview publication.</summary>
    public void Cancel() { Interlocked.Exchange(ref _closed, 1); _cancel.Cancel(); }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _result.ConfigureAwait(false);
        _cancel.Dispose();
    }
}
