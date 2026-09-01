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
    private readonly TaskCompletionSource<bool> _terminalTranscript =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private int _disposeStarted;
    private bool _ended;
    private bool _disposed;

    internal MetaRealtimeStreamingSession(ClientWebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    /// <inheritdoc />
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal Task TerminalTranscriptTask => _terminalTranscript.Task;

    internal static async Task<MetaRealtimeStreamingSession> ConnectAsync(
        string apiKey,
        string modelId,
        IReadOnlyList<string> languageBias,
        CancellationToken ct)
    {
        var webSocket = new ClientWebSocket();
        var connected = false;
        try
        {
            await webSocket.ConnectAsync(new Uri("wss://api.meta.ai/v1/asr/realtime"), ct);
            var session = new MetaRealtimeStreamingSession(webSocket);
            await session.SendTextAsync(CreateHandshakeJson(apiKey, modelId, languageBias), ct);
            var acknowledgement = await ReceiveTextMessageAsync(webSocket, ct);
            ValidateHandshakeAcknowledgement(acknowledgement);
            session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
            connected = true;
            return session;
        }
        finally
        {
            if (!connected)
                webSocket.Dispose();
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

    internal static StreamingTranscriptEvent? ParseTranscriptEvent(string json) =>
        ParseTranscriptEvent(json, out _);

    internal static StreamingTranscriptEvent? ParseTranscriptEvent(string json, out bool isTerminal)
    {
        isTerminal = false;
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

        var isFinal = root.TryGetProperty("final", out var finalElement)
            && finalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && finalElement.GetBoolean();
        isTerminal = isFinal;
        var transcript = transcriptElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
            return null;
        return new StreamingTranscriptEvent(transcript, isFinal);
    }

    /// <inheritdoc />
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (pcm16Audio.Length == 0)
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
        var waitForTerminalTranscript = false;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_disposed || _webSocket.State != WebSocketState.Open)
                return;

            if (!_ended)
            {
                _ended = true;
                try
                {
                    await SendTextAsync("""{"type":"endStream"}""", ct);
                }
                catch (OperationCanceledException ex)
                {
                    _terminalTranscript.TrySetCanceled(ex.CancellationToken);
                    throw;
                }
                catch (WebSocketException ex)
                {
                    _terminalTranscript.TrySetException(ex);
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    _terminalTranscript.TrySetException(ex);
                    throw;
                }
            }

            waitForTerminalTranscript = true;
        }
        finally
        {
            _sendLock.Release();
        }

        if (waitForTerminalTranscript)
            await _terminalTranscript.Task.WaitAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            _disposed = true;
            _terminalTranscript.TrySetException(
                new ObjectDisposedException(nameof(MetaRealtimeStreamingSession)));
            _receiveCts.Cancel();
            _webSocket.Abort();
        }
        finally
        {
            _sendLock.Release();
        }

        if (_receiveTask is not null)
            await _receiveTask;

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
                var transcriptEvent = ParseTranscriptEvent(json, out var isTerminal);
                PublishTranscript(transcriptEvent, isTerminal);
            }
        }
        catch (OperationCanceledException ex)
        {
            if (!_disposed)
                _terminalTranscript.TrySetException(ex);
        }
        catch (WebSocketException ex)
        {
            _terminalTranscript.TrySetException(ex);
            Debug.WriteLine($"Meta realtime WebSocket error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _terminalTranscript.TrySetException(ex);
            Debug.WriteLine($"Meta realtime parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _terminalTranscript.TrySetException(ex);
            Debug.WriteLine($"Meta realtime transcription error: {ex.Message}");
        }
        finally
        {
            if (_ended && !_terminalTranscript.Task.IsCompleted)
            {
                _terminalTranscript.TrySetException(
                    new WebSocketException("Meta realtime session ended before the final transcript."));
            }
        }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    internal void PublishTranscript(StreamingTranscriptEvent? transcriptEvent, bool isTerminal)
    {
        if (transcriptEvent is not null)
            TranscriptReceived?.Invoke(transcriptEvent);
        if (isTerminal)
            _terminalTranscript.TrySetResult(true);
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
