using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.ElevenLabs;

internal sealed class ElevenLabsStreamingSession : IStreamingSession
{
    internal const int MinimumBufferedChunkBytes = 3200; // 100ms at 16kHz, 16-bit mono

    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MemoryStream _audioBuffer = new();
    private Task? _receiveTask;
    private bool _disposed;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<ElevenLabsStreamingSession> ConnectAsync(
        string apiKey,
        string realtimeModelId,
        string? language,
        CancellationToken ct)
    {
        var session = new ElevenLabsStreamingSession();
        session._ws.Options.SetRequestHeader("xi-api-key", apiKey);
        await session._ws.ConnectAsync(BuildRealtimeUri(realtimeModelId, language), ct);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_disposed || _ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ws.State != WebSocketState.Open)
                return;

            _audioBuffer.Write(pcm16Audio.Span);
            if (_audioBuffer.Length < MinimumBufferedChunkBytes)
                return;

            var chunk = _audioBuffer.ToArray();
            _audioBuffer.SetLength(0);
            await SendAudioPayloadAsync(chunk, commit: false, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed || _ws.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ws.State != WebSocketState.Open)
                return;

            if (_audioBuffer.Length == 0)
                return;

            var chunk = _audioBuffer.ToArray();
            _audioBuffer.SetLength(0);
            await SendAudioPayloadAsync(chunk, commit: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal static Uri BuildRealtimeUri(string realtimeModelId, string? language)
    {
        var query = new List<string>
        {
            $"model_id={Uri.EscapeDataString(realtimeModelId)}",
            "audio_format=pcm_16000",
            "commit_strategy=vad",
            "include_timestamps=true",
            "include_language_detection=true",
        };

        if (!string.IsNullOrWhiteSpace(language))
            query.Add($"language_code={Uri.EscapeDataString(language)}");

        return new Uri("wss://api.elevenlabs.io/v1/speech-to-text/realtime?" + string.Join("&", query));
    }

    internal static string BuildAudioChunkPayload(byte[] pcm16Audio, bool commit) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["message_type"] = "input_audio_chunk",
            ["audio_base_64"] = Convert.ToBase64String(pcm16Audio),
            ["sample_rate"] = 16000,
            ["commit"] = commit,
        });

    internal static bool TryParseTranscriptEvent(
        string json,
        out StreamingTranscriptEvent? transcriptEvent,
        out string? error)
    {
        transcriptEvent = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message_type", out var messageTypeEl))
                return false;

            var messageType = messageTypeEl.GetString();
            if (string.IsNullOrWhiteSpace(messageType) || messageType == "session_started")
                return false;

            if (messageType.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                error = ExtractErrorMessage(root) ?? json;
                return false;
            }

            if (messageType is "partial_transcript")
            {
                var text = GetText(root);
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                transcriptEvent = new StreamingTranscriptEvent(text, IsFinal: false);
                return true;
            }

            if (messageType is "committed_transcript" or "committed_transcript_with_timestamps")
            {
                var text = GetText(root);
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                transcriptEvent = new StreamingTranscriptEvent(text, IsFinal: true);
                return true;
            }
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }

        return false;
    }

    private async Task SendAudioPayloadAsync(byte[] chunk, bool commit, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(BuildAudioChunkPayload(chunk, commit));
        await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
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
                if (TryParseTranscriptEvent(json, out var transcriptEvent, out var error))
                {
                    TranscriptReceived?.Invoke(transcriptEvent!);
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    Debug.WriteLine($"ElevenLabs realtime error: {error}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"ElevenLabs realtime WebSocket error: {ex.Message}");
        }
    }

    private static string GetText(JsonElement root) =>
        root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";

    private static string? ExtractErrorMessage(JsonElement root)
    {
        foreach (var propertyName in new[] { "error", "message", "details" })
        {
            if (root.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString();
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _receiveCts.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* best effort */ }
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch { /* expected */ }
        }

        _audioBuffer.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}
