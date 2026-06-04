using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Reson8;

internal sealed class Reson8StreamingSession : IStreamingSession
{
    private readonly ClientWebSocket _ws;
    private readonly Reson8TranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _flushConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private bool _disposed;

    private Reson8StreamingSession(ClientWebSocket ws, Reson8TranscriptCollector collector)
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
    public static async Task<Reson8StreamingSession> ConnectAsync(
        string apiKey,
        string baseUrl,
        string authHeader,
        string? modelId,
        string? language,
        CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateStreamingHeaders(apiKey, authHeader))
            ws.Options.SetRequestHeader(header.Key, header.Value);

        await ws.ConnectAsync(BuildRealtimeUri(baseUrl, modelId, language), ct);

        var session = new Reson8StreamingSession(ws, new Reson8TranscriptCollector());
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    /// <summary>
    /// Builds realtime uri.
    /// </summary>
    public static Uri BuildRealtimeUri(string baseUrl, string? modelId, string? language)
    {
        var normalizedBase = baseUrl.Trim().TrimEnd('/');
        var baseUri = new Uri(normalizedBase);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = "/v1/speech-to-text/realtime"
        };

        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1",
            "include_interim=true"
        };

        if (!string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            query.Add($"language={Uri.EscapeDataString(language.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(modelId, Reson8Plugin.DefaultModelId, StringComparison.Ordinal))
        {
            query.Add($"custom_model_id={Uri.EscapeDataString(modelId.Trim())}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    /// <summary>
    /// Creates streaming headers.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(string apiKey, string authHeader) =>
        new Dictionary<string, string>
        {
            [string.IsNullOrWhiteSpace(authHeader) ? Reson8Plugin.DefaultAuthHeader : authHeader.Trim()] =
                Reson8Plugin.AuthHeaderValue(apiKey, authHeader)
        };

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
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
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

        var json = $$"""{"type":"flush_request","id":"{{Guid.NewGuid()}}"}""";
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                var payload = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }

        await _flushConfirmed.Task.WaitAsync(ct);
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
                    {
                        _flushConfirmed.TrySetResult();
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var transcriptEvent = _collector.ApplyEvent(json);
                if (transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);

                if (_collector.IsFlushConfirmed)
                    _flushConfirmed.TrySetResult();
            }
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"Reson8 receive loop canceled: {ex.Message}");
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Reson8 WebSocket error: {ex.Message}");
            _flushConfirmed.TrySetException(ex);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Reson8 parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Reson8 stream error: {ex.Message}");
            _flushConfirmed.TrySetException(ex);
        }
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
        _flushConfirmed.TrySetResult();

        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch (OperationCanceledException ex)
                {
                    Debug.WriteLine($"Reson8 WebSocket close canceled: {ex.Message}");
                }
                catch (WebSocketException ex)
                {
                    Debug.WriteLine($"Reson8 WebSocket close error: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine($"Reson8 WebSocket close skipped: {ex.Message}");
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException ex)
            {
                Debug.WriteLine($"Reson8 receive loop canceled during dispose: {ex.Message}");
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"Reson8 receive loop closed during dispose: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Reson8 receive loop parse error during dispose: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Reson8 receive loop stopped during dispose: {ex.Message}");
            }
        }

        _sendLock.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}

internal sealed class Reson8TranscriptCollector
{
    private readonly List<string> _finals = [];
    private string _interim = "";

    /// <summary>
    /// Gets or sets the is flush confirmed value.
    /// </summary>
    public bool IsFlushConfirmed { get; private set; }
    /// <summary>
    /// Performs final text.
    /// </summary>
    public string FinalText => string.Join(" ", _finals);

    /// <summary>
    /// Applies an event update to the current state.
    /// </summary>
    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = GetString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;

        if (type.Equals("flush_confirmation", StringComparison.OrdinalIgnoreCase))
        {
            IsFlushConfirmed = true;
            return null;
        }

        if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Reson8Plugin.ExtractApiError(json));

        if (!type.Equals("transcript", StringComparison.OrdinalIgnoreCase))
            return null;

        var text = GetString(root, "text")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var isFinal = GetBool(root, "is_final");
        if (isFinal)
        {
            _finals.Add(text);
            _interim = "";
        }
        else
        {
            _interim = text;
        }

        return new StreamingTranscriptEvent(text, isFinal);
    }

    /// <summary>
    /// Applies an event update to the current state.
    /// </summary>
    public string ApplyEvent(StreamingTranscriptEvent evt)
    {
        if (evt.IsFinal)
        {
            var trimmed = evt.Text.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                _finals.Add(trimmed);
            _interim = "";
        }
        else
        {
            _interim = evt.Text.Trim();
        }

        return CurrentText;
    }

    private string CurrentText
    {
        get
        {
            var parts = _finals.ToList();
            if (!string.IsNullOrWhiteSpace(_interim))
                parts.Add(_interim);
            return string.Join(" ", parts);
        }
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
