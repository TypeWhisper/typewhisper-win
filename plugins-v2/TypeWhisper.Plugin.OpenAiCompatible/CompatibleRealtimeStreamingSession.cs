using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

internal sealed class CompatibleRealtimeStreamingSession : IStreamingSession
{
    internal const string LegacyModelId = "gpt-realtime-whisper";
    internal const string LiveModelId = "gpt-live-transcribe";
    internal const string LiveDelay = "low";
    internal const int SourceSampleRate = 16_000;
    internal const int TargetSampleRate = 24_000;

    private readonly WebSocket _ws;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _committedItemId;
    private readonly HashSet<string> _completedItems = [];
    private int _finalizing;
    private readonly CompatibleRealtimeTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveTask;
    private volatile bool _disposed;

    internal CompatibleRealtimeStreamingSession(WebSocket ws, CompatibleRealtimeTranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    /// <summary>
    /// Raised when transcript received.
    /// </summary>
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    /// <summary>
    /// Connects the streaming session before audio is sent.
    /// </summary>
    public static async Task<CompatibleRealtimeStreamingSession> ConnectAsync(
        Uri endpoint,
        string apiKey,
        string modelId,
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct, IReadOnlyList<string>? keywords = null, string delay = "")
    {
        var ws = new ClientWebSocket(); foreach (var header in OpenAiCompatiblePlugin.AuthenticationHeaders(endpoint, apiKey)) ws.Options.SetRequestHeader(header.Key, header.Value);
        CompatibleRealtimeStreamingSession? session = null;
        try
        {
            await ws.ConnectAsync(endpoint, ct);
            session = new(ws, new());
            await session.StartAsync(modelId, languageHints, prompt, ct, keywords, delay);
            return session;
        }
        catch { if (session is not null) await session.DisposeAsync(); else ws.Dispose(); throw; }
    }

    /// <summary>
    /// Transcribes wav asynchronously.
    /// </summary>
    public static async Task<PluginTranscriptionResult> TranscribeWavAsync(
        Uri endpoint,
        string apiKey,
        string modelId,
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct, IReadOnlyList<string>? keywords = null, string delay = "")
    {
        await using var session = await ConnectAsync(endpoint, apiKey, modelId, languageHints, prompt, ct, keywords, delay);
        var pcm = ExtractPcm16Data(wavAudio);
        const int chunkBytes = SourceSampleRate * sizeof(short) / 5; // 200ms
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, pcm.Length - offset);
            await session.SendAudioAsync(pcm.AsMemory(offset, length), ct);
        }

        await session.FinalizeAsync(ct);
        var fallbackLanguage = IsLiveModel(modelId)
            ? null
            : languageHints.FirstOrDefault();
        return new PluginTranscriptionResult(
            session._collector.CurrentText,
            fallbackLanguage,
            0,
            NoSpeechProbability: null);
    }

    internal async Task StartAsync(string modelId, IReadOnlyList<string> hints, string? prompt, CancellationToken ct,
        IReadOnlyList<string>? keywords = null, string delay = "")
    {
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
        await SendTextAsync(CreateSessionUpdatePayload(modelId, hints, prompt, keywords, delay), ct);
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException) { throw new IOException("OpenAI did not acknowledge the live session settings."); }
    }

    internal static string CreateSessionUpdatePayload(
        string modelId,
        IReadOnlyList<string> languageHints,
        string? prompt, IReadOnlyList<string>? keywords = null, string delay = "")
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = modelId
        };

        if (IsLiveModel(modelId))
        {
            if (languageHints.Count > 0)
                transcription["languages"] = languageHints;
            if (!string.IsNullOrWhiteSpace(prompt))
                transcription["prompt"] = prompt;
            if (!string.IsNullOrEmpty(delay)) transcription["delay"] = delay;
            if (keywords is { Count: > 0 }) transcription["keywords"] = keywords;
        }
        else if (languageHints.FirstOrDefault() is { } language)
        {
            transcription["language"] = language;
        }

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = new Dictionary<string, object?>
            {
                ["type"] = "transcription",
                ["audio"] = new Dictionary<string, object?>
                {
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["format"] = new Dictionary<string, object?>
                        {
                            ["type"] = "audio/pcm",
                            ["rate"] = TargetSampleRate,
                        },
                        ["transcription"] = transcription,
                        ["turn_detection"] = null,
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    internal static bool IsLiveModel(string modelId) => !modelId.Contains("whisper", StringComparison.OrdinalIgnoreCase);

    internal static string CreateAudioAppendPayload(ReadOnlySpan<byte> pcm16Audio)
    {
        var resampled = Resample16kPcmTo24k(pcm16Audio);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(resampled),
        });
    }

    /// <summary>
    /// Sends a PCM audio chunk to the active streaming session.
    /// </summary>
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed || _ws.State != WebSocketState.Open || Volatile.Read(ref _finalizing) != 0)
            throw new IOException("The OpenAI live session is no longer accepting audio.");
        if (pcm16Audio.Length == 0) return;
        if (pcm16Audio.Length % 2 != 0) throw new InvalidDataException("Expected 16-bit PCM audio.");

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ws.State != WebSocketState.Open)
                throw new IOException("The OpenAI live connection was interrupted.");

            await SendTextAsync(CreateAudioAppendPayload(pcm16Audio.Span), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Finalizes the stream and returns the provider transcript.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _finalizing, 1) == 0)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                if (_disposed || _ws.State != WebSocketState.Open) throw new IOException("The OpenAI live connection was interrupted.");
                await SendTextAsync("""{"type":"input_audio_buffer.commit"}""", ct);
            }
            finally { _sendLock.Release(); }
        }
        try { await _final.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { throw new IOException("OpenAI did not confirm the final transcript."); }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("OpenAI closed the live connection before completion.");
                    if (messageBuffer.Length + result.Count > 1024 * 1024) throw new IOException("OpenAI sent an oversized live message.");
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var changed = _collector.ApplyEvent(json, out var transcriptEvent);
                if (_collector.Error is not null) throw new IOException("OpenAI rejected the live transcription request.");
                if (_collector.IsSessionReady) _ready.TrySetResult();
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                if (root.TryGetProperty("item_id", out var itemValue) && itemValue.GetString() is { } itemId)
                {
                    if (type == "input_audio_buffer.committed") _committedItemId = itemId;
                    if (type == "conversation.item.input_audio_transcription.completed") _completedItems.Add(itemId);
                }
                if (changed && transcriptEvent is not null) TranscriptReceived?.Invoke(transcriptEvent);
                if (_committedItemId is not null && _completedItems.Contains(_committedItemId)) _final.TrySetResult();
            }
            ct.ThrowIfCancellationRequested();
            throw new IOException("OpenAI closed the live connection before completion.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { _ready.TrySetCanceled(ct); _final.TrySetCanceled(ct); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = new IOException("OpenAI live transcription was interrupted or returned an invalid response.");
            _ready.TrySetException(failure); _final.TrySetException(failure);
        }
    }

    internal static byte[] Resample16kPcmTo24k(ReadOnlySpan<byte> pcm16Audio)
    {
        var sourceSampleCount = pcm16Audio.Length / sizeof(short);
        if (sourceSampleCount == 0)
            return [];

        var targetSampleCount = Math.Max(1, (int)Math.Round(sourceSampleCount * (double)TargetSampleRate / SourceSampleRate));
        var output = new byte[targetSampleCount * sizeof(short)];

        for (var targetIndex = 0; targetIndex < targetSampleCount; targetIndex++)
        {
            var sourcePosition = targetIndex * (double)SourceSampleRate / TargetSampleRate;
            var lowerIndex = Math.Min((int)Math.Floor(sourcePosition), sourceSampleCount - 1);
            var upperIndex = Math.Min(lowerIndex + 1, sourceSampleCount - 1);
            var fraction = sourcePosition - lowerIndex;
            var lower = ReadSample(pcm16Audio, lowerIndex);
            var upper = ReadSample(pcm16Audio, upperIndex);
            var sample = (short)Math.Clamp(
                (int)Math.Round(lower + ((upper - lower) * fraction)),
                short.MinValue,
                short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(targetIndex * sizeof(short)), sample);
        }

        return output;
    }

    private static short ReadSample(ReadOnlySpan<byte> pcm16Audio, int sampleIndex) =>
        BinaryPrimitives.ReadInt16LittleEndian(pcm16Audio.Slice(sampleIndex * sizeof(short), sizeof(short)));

    internal static byte[] ExtractPcm16Data(byte[] wavAudio)
    {
        if (wavAudio.Length < 44 || !wavAudio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wavAudio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a PCM WAV recording.");
        var validFormat = false;
        for (var offset = 12; offset <= wavAudio.Length - 8;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            var dataStart = offset + 8;
            if (size < 0 || size > wavAudio.Length - dataStart) throw new InvalidDataException("Invalid WAV chunk size.");
            var chunk = wavAudio.AsSpan(offset, 4);
            if (chunk.SequenceEqual("fmt "u8))
                validFormat = size >= 16 && BinaryPrimitives.ReadInt16LittleEndian(wavAudio.AsSpan(dataStart, 2)) == 1
                    && BinaryPrimitives.ReadInt16LittleEndian(wavAudio.AsSpan(dataStart + 2, 2)) == 1
                    && BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(dataStart + 4, 4)) == SourceSampleRate
                    && BinaryPrimitives.ReadInt16LittleEndian(wavAudio.AsSpan(dataStart + 14, 2)) == 16;
            if (chunk.SequenceEqual("data"u8))
            {
                if (!validFormat || size == 0 || size % 2 != 0) throw new InvalidDataException("Expected mono 16 kHz, 16-bit PCM audio.");
                return wavAudio[dataStart..(dataStart + size)];
            }
            offset = checked(dataStart + size + (size & 1));
        }
        throw new InvalidDataException("WAV recording contains no PCM data.");
    }

    /// <summary>
    /// Releases asynchronous resources owned by this session.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _receiveCts.Cancel();

        _ws.Abort();

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch { }
        }

        _sendLock.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}

internal sealed class CompatibleRealtimeTranscriptCollector
{
    private readonly List<string> _completedOrder = [];
    private readonly Dictionary<string, string> _completedTexts = [];
    private readonly Dictionary<string, string> _deltaTexts = [];

    /// <summary>
    /// Gets the current text.
    /// </summary>
    public string CurrentText
    {
        get
        {
            var parts = _completedOrder
                .Where(_completedTexts.ContainsKey)
                .Select(id => _completedTexts[id])
                .ToList();
            parts.AddRange(_deltaTexts
                .Where(pair => !_completedTexts.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
            return string.Join(" ", parts).Trim();
        }
    }

    /// <summary>
    /// Gets whether has completed transcript.
    /// </summary>
    public bool HasCompletedTranscript => _completedOrder.Count > 0;
    /// <summary>
    /// Gets or sets the is session ready value.
    /// </summary>
    public bool IsSessionReady { get; private set; }
    /// <summary>
    /// Gets or sets the error value.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Applies an event update to the current state.
    /// </summary>
    public bool ApplyEvent(string json, out StreamingTranscriptEvent? transcriptEvent)
    {
        transcriptEvent = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            return false;

        var type = typeEl.GetString();
        switch (type)
        {
            case "conversation.item.input_audio_transcription.delta":
            {
                var itemId = GetString(root, "item_id") ?? "__unidentified_item__";
                var delta = GetString(root, "delta") ?? "";
                _deltaTexts[itemId] = _deltaTexts.TryGetValue(itemId, out var current)
                    ? current + delta
                    : delta;
                transcriptEvent = new StreamingTranscriptEvent(CurrentText, false);
                return !string.IsNullOrWhiteSpace(transcriptEvent.Text);
            }
            case "conversation.item.input_audio_transcription.completed":
            {
                var itemId = GetString(root, "item_id") ?? "__unidentified_item__";
                var transcript = (GetString(root, "transcript") ?? "").Trim();
                if (!_completedTexts.ContainsKey(itemId))
                    _completedOrder.Add(itemId);
                _completedTexts[itemId] = transcript;
                _deltaTexts.Remove(itemId);
                transcriptEvent = new StreamingTranscriptEvent(CurrentText, true);
                return true;
            }
            case "session.updated":
            case "transcription_session.updated":
                IsSessionReady = true;
                return false;
            case "conversation.item.input_audio_transcription.failed":
            case "error":
                Error = ExtractErrorMessage(root) ?? "OpenAI realtime transcription failed";
                return false;
            default:
                return false;
        }
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (GetString(error, "message") is { } message)
                    return message;
                if (GetString(error, "type") is { } type)
                    return type;
            }
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return GetString(root, "message");
    }
}

