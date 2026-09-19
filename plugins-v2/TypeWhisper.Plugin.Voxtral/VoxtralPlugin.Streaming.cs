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
    internal Func<string, string, CancellationToken, Task<IStreamingSession>> ConnectStreaming { get; set; } = MistralStreamingSession.ConnectAsync;

    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsRealtime) throw new NotSupportedException("Select a Voxtral Realtime model for live transcription.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { return await ConnectStreaming(Connection.RequireKey(), SelectedModelId!, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("Mistral live connection timed out.", PluginRequestFailureKind.Timeout); }
    }

    private async Task<PluginTranscriptionResult> TranscribeRealtimeAsync(byte[] wavAudio, CancellationToken ct)
    {
        var pcm = ExtractPcm(wavAudio);
        await using var session = await StartStreamingAsync(null, ct).ConfigureAwait(false);
        string? text = null, language = null;
        session.TranscriptReceived += update => { if (update.IsFinal) { text = update.Text; language = update.DetectedLanguage; } };
        await session.SendAudioAsync(pcm, ct).ConfigureAwait(false);
        await session.FinalizeAsync(ct).ConfigureAwait(false);
        return new(text ?? throw ProviderConnection.InvalidResponse(), language, pcm.Length / 32000d, null);
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
