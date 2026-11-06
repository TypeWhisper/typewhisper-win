using System.Buffers.Binary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{
    /// <inheritdoc />
    public bool SupportsStreaming => IsRealtime;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => IsRealtime;
    // On the same dictations, Realtime 3 misheard more words and finished about 0.6 s later than an upload to Mini
    // Transcribe 3 (tested 2026-11-06). With a file model available, the live session therefore only shows preview text
    // and the host takes the final text from the complete recording, which TranscribeAsync uploads to that model.
    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt) =>
        SupportsStreaming && BatchModel is null
        && (SelectedTranscription?.ContextBias == true || PluginDictionaryTerms.ParsePrompt(prompt).Count == 0);
    /// <inheritdoc />
    public bool SupportsStreamingPreviewForPrompt(string? prompt) => SupportsStreaming && !SupportsStreamingForPrompt(prompt);
    internal Func<string, MistralSessionOptions, TimeSpan, CancellationToken, Task<IStreamingSession>> ConnectStreaming { get; set; } = MistralStreamingSession.ConnectAsync;

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingCoreAsync(language, null, TimeSpan.FromSeconds(15), ct);

    // Implemented implicitly: an explicit interface implementation would reference a member older SDKs lack and fail to load there.
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct) =>
        StartStreamingCoreAsync(languageHints.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)), prompt, TimeSpan.FromSeconds(15), ct);

    private async Task<IStreamingSession> StartStreamingCoreAsync(string? language, string? prompt, TimeSpan completionTimeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (SelectedTranscription is not { Realtime: true } model) throw new NotSupportedException("Select a Voxtral Realtime model for live transcription.");
        // Realtime 3 takes the language and dictionary in session.update; Realtime 2 rejects both, so they are only sent when supported.
        var options = new MistralSessionOptions(model.Id, LanguageFor(model, language), ContextBias(model, prompt));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { return await ConnectStreaming(Connection.RequireKey(), options, completionTimeout, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("Mistral live connection timed out.", PluginRequestFailureKind.Timeout); }
    }

    // Used only when the catalog offers no file transcription model for complete recordings.
    private async Task<PluginTranscriptionResult> TranscribeRealtimeAsync(byte[] wavAudio, string? language, string? prompt, CancellationToken ct)
    {
        var pcm = ExtractPcm(wavAudio);
        // A complete recording is uploaded faster than realtime. Allow its duration plus
        // the usual live tail budget; the host's live deadline does not wrap this fallback.
        var duration = pcm.Length / 32000d;
        await using var session = await StartStreamingCoreAsync(language, prompt, TimeSpan.FromSeconds(duration + 15), ct).ConfigureAwait(false);
        string? text = null, detected = null;
        session.TranscriptReceived += update => { if (update.IsFinal) { text = update.Text; detected = update.DetectedLanguage; } };
        await session.SendAudioAsync(pcm, ct).ConfigureAwait(false);
        await session.FinalizeAsync(ct).ConfigureAwait(false);
        return new(text ?? throw ProviderConnection.InvalidResponse(), detected, duration, null);
    }

    internal static ReadOnlyMemory<byte> ExtractPcm(byte[] wav)
    {
        if (wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a PCM WAV recording.");
        var validFormat = false;
        for (var offset = 12; offset <= wav.Length - 8;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            var start = offset + 8;
            if (size < 0 || size > wav.Length - start) throw new InvalidDataException("Invalid WAV chunk size.");
            var chunk = wav.AsSpan(offset, 4);
            if (chunk.SequenceEqual("fmt "u8))
                validFormat = size >= 16 && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(start, 2)) == 1
                    && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(start + 2, 2)) == 1
                    && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(start + 4, 4)) == 16000
                    && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(start + 14, 2)) == 16;
            if (chunk.SequenceEqual("data"u8))
            {
                if (!validFormat || size == 0 || size % 2 != 0) throw new InvalidDataException("Expected mono 16 kHz, 16-bit PCM audio.");
                return wav.AsMemory(start, size);
            }
            offset = checked(start + size + (size & 1));
        }
        throw new InvalidDataException("WAV recording contains no PCM data.");
    }
}
