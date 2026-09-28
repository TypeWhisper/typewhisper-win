using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// One frame is an 8-byte little-endian prefix (header length, payload length), a UTF-8 JSON header
// and an optional binary payload. Audio travels as payload so it never passes through JSON, and the
// protocol uses its own pipe so native libraries writing to stdout or stderr cannot corrupt it.
internal static class TranscriptionWorkerProtocol
{
    internal const int Version = 1;
    private const int MaximumHeaderBytes = 1 << 20;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static async Task WriteAsync(Stream stream, TranscriptionWorkerMessage message, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), payload.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns null at a clean end of stream; a frame cut short throws.</summary>
    internal static async Task<(TranscriptionWorkerMessage Message, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[8];
        var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < prefix.Length) throw new EndOfStreamException("The transcription worker closed the connection mid-frame.");
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        if (headerLength is <= 0 or > MaximumHeaderBytes || payloadLength < 0)
            throw new InvalidDataException("The transcription worker sent an invalid frame.");
        var header = new byte[headerLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var payload = payloadLength == 0 ? [] : new byte[payloadLength];
        if (payloadLength > 0) await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<TranscriptionWorkerMessage>(header, Json)
            ?? throw new InvalidDataException("The transcription worker sent an empty frame.");
        return (message, payload);
    }

    // Plugin exceptions keep their kind and message across the process boundary, because the host
    // shows the message and some callers distinguish unsupported requests from failures.
    internal static TranscriptionWorkerError ToError(Exception exception) => new(exception switch
    {
        OperationCanceledException => "canceled",
        NotSupportedException => "not-supported",
        ArgumentException => "argument",
        FileNotFoundException or DirectoryNotFoundException => "file-not-found",
        InvalidDataException => "invalid-data",
        UnauthorizedAccessException => "unauthorized",
        IOException => "io",
        TimeoutException => "timeout",
        _ => "invalid-operation"
    }, exception.Message);

    internal static Exception ToException(TranscriptionWorkerError error) => error.Kind switch
    {
        "canceled" => new OperationCanceledException(error.Message),
        "not-supported" => new NotSupportedException(error.Message),
        "argument" => new ArgumentException(error.Message),
        "file-not-found" => new FileNotFoundException(error.Message),
        "invalid-data" => new InvalidDataException(error.Message),
        "unauthorized" => new UnauthorizedAccessException(error.Message),
        "io" => new IOException(error.Message),
        "timeout" => new TimeoutException(error.Message),
        _ => new InvalidOperationException(error.Message)
    };

    internal static TranscriptionWorkerResult ToResult(PluginTranscriptionResult result) => new(result.Text, result.DetectedLanguage,
        result.DurationSeconds, result.NoSpeechProbability,
        result.Segments.Select(segment => new TranscriptionWorkerSegment(segment.Text, segment.Start, segment.End)).ToArray(),
        result.TokenTimings.Select(timing => new TranscriptionWorkerTiming(timing.Text, timing.StartSeconds, timing.EndSeconds)).ToArray());

    internal static PluginTranscriptionResult FromResult(TranscriptionWorkerResult result) =>
        new(result.Text, result.DetectedLanguage, result.DurationSeconds, result.NoSpeechProbability)
        {
            Segments = Array.AsReadOnly((result.Segments ?? []).Select(segment => new PluginTranscriptionSegment(segment.Text, segment.Start, segment.End)).ToArray()),
            TokenTimings = Array.AsReadOnly((result.TokenTimings ?? []).Select(timing => new VocabularyTokenTiming(timing.Text, timing.Start, timing.End)).ToArray())
        };
}

internal static class TranscriptionWorkerMessageTypes
{
    internal const string Hello = "hello";
    internal const string Request = "request";
    internal const string Cancel = "cancel";
    internal const string Response = "response";
    internal const string Log = "log";
}

internal static class TranscriptionWorkerCommands
{
    internal const string Load = "load";
    internal const string Transcribe = "transcribe";
    internal const string Shutdown = "shutdown";
}

internal static class TranscriptionWorkerAudioFormats
{
    internal const string Pcm = "pcm-f32le";
    internal const string Wav = "wav";
}

/// <summary>The single envelope of every frame; unused fields stay null.</summary>
internal sealed record TranscriptionWorkerMessage
{
    public string Type { get; init; } = "";
    public long Id { get; init; }
    public int ProtocolVersion { get; init; }
    public int ProcessId { get; init; }
    public string? Command { get; init; }
    public string? ModelId { get; init; }
    public string? Language { get; init; }
    public string[]? LanguageHints { get; init; }
    public bool Translate { get; init; }
    public string? Prompt { get; init; }
    public string? AudioFormat { get; init; }
    public TranscriptionWorkerResult? Result { get; init; }
    public TranscriptionWorkerError? Error { get; init; }
    public TranscriptionWorkerState? State { get; init; }
    public PluginLogLevel? LogLevel { get; init; }
    public string? LogMessage { get; init; }
}

internal sealed record TranscriptionWorkerError(string Kind, string Message);

/// <summary>The worker engine's own view after each command, so the host can report what actually ran.</summary>
internal sealed record TranscriptionWorkerState(string? SelectedModelId, string? LoadedModelId,
    TranscriptionAccelerationPreference Preference, TranscriptionAccelerationStatus Status);

internal sealed record TranscriptionWorkerResult(string Text, string? DetectedLanguage, double DurationSeconds,
    float? NoSpeechProbability, TranscriptionWorkerSegment[]? Segments, TranscriptionWorkerTiming[]? TokenTimings);

internal sealed record TranscriptionWorkerSegment(string Text, double Start, double End);

internal sealed record TranscriptionWorkerTiming(string Text, double Start, double End);
