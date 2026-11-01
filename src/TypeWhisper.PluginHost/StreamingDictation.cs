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
    private int _previewOnly;
    private long _acceptedSamples;
    /// <summary>Unambiguous language reported by a finalized segment.</summary>
    public string? DetectedLanguage { get; private set; }

    /// <summary>
    /// True once the provider accepted the stream only as a preview for the dictionary prompt. The stream then
    /// never supplies the final text: <see cref="FinishAsync"/> returns null without reporting a failure.
    /// </summary>
    public bool IsPreviewOnly => Volatile.Read(ref _previewOnly) != 0;

    /// <summary>
    /// The callback must retain the provider for the entire streaming operation. <paramref name="promptAllowed"/> is
    /// asked under the lease, right before the stream opens: false drops the dictionary prompt for this stream.
    /// </summary>
    public StreamingDictation(
        Func<Func<ITranscriptionEnginePlugin, CancellationToken, Task<string>>, CancellationToken, Task<string>> use,
        IReadOnlyList<string> languages, Action<string> publish, Action failed, CancellationToken ct, IReadOnlyList<string>? dictionaryTerms = null,
        Func<bool>? promptAllowed = null)
    {
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _result = RunAsync(use, languages.ToArray(), publish, failed, dictionaryTerms?.ToArray(), promptAllowed);
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
        IReadOnlyList<string> languages, Action<string> publish, Action failed, IReadOnlyList<string>? dictionaryTerms, Func<bool>? promptAllowed)
    {
        try
        {
            var streamed = await use(async (engine, ct) =>
            {
                var prompt = promptAllowed?.Invoke() == false ? null : LanguageHintTranscription.CreateDictionaryPrompt(engine, dictionaryTerms);
                if (!engine.SupportsStreaming || !engine.SupportsStreamingCompletion) throw new NotSupportedException("Live transcription is unavailable.");
                if (!engine.SupportsStreamingForPrompt(prompt))
                {
                    // A preview stream runs without the prompt; the final text comes from the complete recording.
                    if (!engine.SupportsStreamingPreviewForPrompt(prompt)) throw new NotSupportedException("Live transcription is unavailable.");
                    prompt = null;
                    Volatile.Write(ref _previewOnly, 1);
                }
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
                        // A stopped preview has nothing left to show; skip the remaining backlog.
                        if (IsPreviewOnly && Volatile.Read(ref _closed) != 0) return "";
                        using var send = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        send.CancelAfter(TimeSpan.FromSeconds(10));
                        await session.SendAudioAsync(pcm, send.Token).ConfigureAwait(false);
                    }
                    if (IsPreviewOnly) return "";
                    using var finish = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    finish.CancelAfter(TimeSpan.FromSeconds(15));
                    await session.FinalizeAsync(finish.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return string.Join(" ", confirmed);
                }
                finally { session.TranscriptReceived -= OnTranscript; }
            }, _cancel.Token).ConfigureAwait(false);
            // A preview never supplies the final text.
            return IsPreviewOnly ? null : streamed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_cancel.IsCancellationRequested || Volatile.Read(ref _inputFailed) != 0) failed();
            return null;
        }
    }

    /// <summary>
    /// Drains every accepted chunk and the final provider response. Null requires full-recording fallback,
    /// which is always the case for a preview-only stream.
    /// </summary>
    public async Task<string?> FinishAsync(int? expectedSamples = null)
    {
        Interlocked.Exchange(ref _closed, 1);
        if (IsPreviewOnly)
        {
            // The final text comes from the complete recording. Cancellation closes the session at once;
            // waiting for it releases the provider lease before the host starts that decode.
            Cancel();
            await _result.ConfigureAwait(false);
            return null;
        }
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
