using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed class OpenAiRealtimeStreamingSession : IStreamingSession
{
    internal const string ModelId = "gpt-realtime-whisper";
    internal const int SourceSampleRate = 16_000;
    internal const int TargetSampleRate = 24_000;

    private readonly ClientWebSocket _ws;
    private readonly OpenAiRealtimeTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveTask;
    private bool _disposed;

    private OpenAiRealtimeStreamingSession(ClientWebSocket ws, OpenAiRealtimeTranscriptCollector collector)
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
    public static async Task<OpenAiRealtimeStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        string? prompt,
        CancellationToken ct)
    {
        var ws = CreateConfiguredWebSocket(apiKey);
        await ws.ConnectAsync(BuildRealtimeUri(), ct);

        var collector = new OpenAiRealtimeTranscriptCollector();
        var session = new OpenAiRealtimeStreamingSession(ws, collector);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        await session.SendTextAsync(CreateSessionUpdatePayload(language, prompt), ct);
        return session;
    }

    /// <summary>
    /// Transcribes wav asynchronously.
    /// </summary>
    public static async Task<PluginTranscriptionResult> TranscribeWavAsync(
        string apiKey,
        byte[] wavAudio,
        string? language,
        string? prompt,
        CancellationToken ct)
    {
        await using var session = await ConnectAsync(apiKey, language, prompt, ct);
        var pcm = ExtractPcm16Data(wavAudio);
        const int chunkBytes = SourceSampleRate * sizeof(short) / 5; // 200ms
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, pcm.Length - offset);
            await session.SendAudioAsync(pcm.AsMemory(offset, length), ct);
        }

        await session.FinalizeAsync(ct);
        await session.WaitForCompletedTranscriptAsync(TimeSpan.FromSeconds(10), ct);
        return new PluginTranscriptionResult(session._collector.CurrentText, language, 0, NoSpeechProbability: null);
    }

    internal static Uri BuildRealtimeUri() =>
        new("wss://api.openai.com/v1/realtime?intent=transcription");

    internal static IReadOnlyDictionary<string, string> CreateRealtimeHeaders(string apiKey) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}"
        };

    internal static ClientWebSocket CreateConfiguredWebSocket(string apiKey)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateRealtimeHeaders(apiKey))
            ws.Options.SetRequestHeader(header.Key, header.Value);
        return ws;
    }

    internal static string CreateSessionUpdatePayload(string? language, string? prompt)
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = ModelId
        };

        if (!string.IsNullOrWhiteSpace(language))
            transcription["language"] = language;

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
        if (_disposed || _ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ws.State != WebSocketState.Open)
                return;

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
        if (_disposed || _ws.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ws.State != WebSocketState.Open)
                return;
            await SendTextAsync("""{"type":"input_audio_buffer.commit"}""", ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

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
                        return;
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                if (_collector.ApplyEvent(json, out var transcriptEvent) && transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"OpenAI realtime WebSocket error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"OpenAI realtime parse error: {ex.Message}");
        }
    }

    private async Task WaitForCompletedTranscriptAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            if (_collector.HasCompletedTranscript)
                return;
            await Task.Delay(50, linked.Token);
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

    private static byte[] ExtractPcm16Data(byte[] wavAudio)
    {
        if (wavAudio.Length <= 44)
            return [];

        for (var offset = 12; offset + 8 <= wavAudio.Length; )
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            var dataStart = offset + 8;
            if (chunkId == "data" && dataStart + chunkSize <= wavAudio.Length)
                return wavAudio[dataStart..(dataStart + chunkSize)];
            offset = dataStart + chunkSize;
        }

        return wavAudio[44..];
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

        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }
        }

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

internal sealed class OpenAiRealtimeTranscriptCollector
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
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N");
                var delta = GetString(root, "delta") ?? "";
                _deltaTexts[itemId] = _deltaTexts.TryGetValue(itemId, out var current)
                    ? current + delta
                    : delta;
                transcriptEvent = new StreamingTranscriptEvent(CurrentText, false);
                return !string.IsNullOrWhiteSpace(transcriptEvent.Text);
            }
            case "conversation.item.input_audio_transcription.completed":
            {
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N");
                var transcript = (GetString(root, "transcript") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(transcript))
                    return false;
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
