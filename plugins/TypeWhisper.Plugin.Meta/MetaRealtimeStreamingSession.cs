using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Meta;

internal sealed class MetaRealtimeStreamingSession : IStreamingSession
{
    private readonly ClientWebSocket _webSocket;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveTask;
    private bool _ended;
    private bool _disposed;

    private MetaRealtimeStreamingSession(ClientWebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    /// <inheritdoc />
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal static async Task<MetaRealtimeStreamingSession> ConnectAsync(
        string apiKey,
        string modelId,
        IReadOnlyList<string> languageBias,
        CancellationToken ct)
    {
        var webSocket = new ClientWebSocket();
        try
        {
            await webSocket.ConnectAsync(new Uri("wss://api.meta.ai/v1/asr/realtime"), ct);
            var session = new MetaRealtimeStreamingSession(webSocket);
            await session.SendTextAsync(CreateHandshakeJson(apiKey, modelId, languageBias), ct);
            var acknowledgement = await ReceiveTextMessageAsync(webSocket, ct);
            ValidateHandshakeAcknowledgement(acknowledgement);
            session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
            return session;
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }
    }

    internal static string CreateHandshakeJson(
        string apiKey,
        string modelId,
        IReadOnlyList<string> languageBias)
    {
        var body = new Dictionary<string, object?>
        {
            ["authorization"] = new Dictionary<string, string>
            {
                ["accessToken"] = $"Bearer {apiKey}",
            },
            ["audioEncoding"] = "PCM_16KHZ",
            ["model"] = modelId,
            ["mode"] = "PUSH_TO_TALK",
            ["partialMode"] = "CUMULATIVE",
            ["emitAudioProgress"] = false,
        };
        if (languageBias.Count > 0)
            body["languageBias"] = languageBias;

        return JsonSerializer.Serialize(body);
    }

    internal static StreamingTranscriptEvent? ParseTranscriptEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
            return null;

        var type = typeElement.GetString();
        if (type == "error")
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new InvalidOperationException(message ?? "Meta realtime transcription failed.");
        }

        if (type != "transcript"
            || !root.TryGetProperty("transcript", out var transcriptElement))
        {
            return null;
        }

        var transcript = transcriptElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
            return null;
        var isFinal = root.TryGetProperty("final", out var finalElement)
            && finalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && finalElement.GetBoolean();
        return new StreamingTranscriptEvent(transcript, isFinal);
    }

    /// <inheritdoc />
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_disposed || _ended || pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ended || _webSocket.State != WebSocketState.Open)
                return;
            await _webSocket.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed || _ended)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _ended || _webSocket.State != WebSocketState.Open)
                return;
            _ended = true;
            await SendTextAsync("""{"type":"endStream"}""", ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _receiveCts.Cancel();
        _webSocket.Abort();
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
                // The receive is expected to stop when the session is disposed.
            }
        }

        _sendLock.Dispose();
        _receiveCts.Dispose();
        _webSocket.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextMessageAsync(_webSocket, ct);
                var transcriptEvent = ParseTranscriptEvent(json);
                if (transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Meta realtime WebSocket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Meta realtime transcription error: {ex.Message}");
        }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveTextMessageAsync(
        ClientWebSocket webSocket,
        CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Meta closed the realtime session.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("Meta returned an unexpected binary realtime message.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    private static void ValidateHandshakeAcknowledgement(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("sessionId", out var sessionId)
            && sessionId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(sessionId.GetString()))
        {
            return;
        }

        var error = root.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
        throw new InvalidOperationException(error ?? "Meta rejected the realtime handshake.");
    }
}
