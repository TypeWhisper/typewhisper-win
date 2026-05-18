using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.SmallestAi;

internal sealed class SmallestAiStreamingSession : IStreamingSession
{
    private readonly ClientWebSocket _ws;
    private readonly SmallestAiTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _lastResponseReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private bool _disposed;

    private SmallestAiStreamingSession(ClientWebSocket ws, SmallestAiTranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<SmallestAiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct)
    {
        var ws = CreateConfiguredWebSocket(apiKey);
        await ws.ConnectAsync(BuildStreamingUri(language, wordTimestamps: true), ct);

        var session = new SmallestAiStreamingSession(ws, new SmallestAiTranscriptCollector());
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public static Uri BuildStreamingUri(string? language, bool wordTimestamps)
    {
        var query = new List<string>
        {
            "encoding=linear16",
            "sample_rate=16000"
        };

        var normalizedLanguage = SmallestAiPlugin.NormalizeLanguage(language);
        if (normalizedLanguage is not null)
            query.Insert(0, $"language={Uri.EscapeDataString(normalizedLanguage)}");

        if (wordTimestamps)
            query.Add("word_timestamps=true");

        return new Uri("wss://api.smallest.ai/waves/v1/pulse/get_text?" + string.Join("&", query));
    }

    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(string apiKey) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}"
        };

    private static ClientWebSocket CreateConfiguredWebSocket(string apiKey)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateStreamingHeaders(apiKey))
            ws.Options.SetRequestHeader(header.Key, header.Value);
        return ws;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_disposed || _ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open)
                return;

            await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
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
            if (_ws.State != WebSocketState.Open)
                return;

            await SendTextAsync("""{"type":"close_stream"}""", ct);
        }
        finally
        {
            _sendLock.Release();
        }

        await _lastResponseReceived.Task.WaitAsync(ct);
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
                    {
                        _lastResponseReceived.TrySetResult();
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

                if (_collector.IsLastReceived)
                    _lastResponseReceived.TrySetResult();
            }
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse receive loop canceled: {ex.Message}");
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse WebSocket error: {ex.Message}");
            _lastResponseReceived.TrySetException(ex);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse stream error: {ex.Message}");
            _lastResponseReceived.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _receiveCts.Cancel();
        _lastResponseReceived.TrySetResult();

        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch (OperationCanceledException ex)
                {
                    Debug.WriteLine($"Smallest AI Pulse WebSocket close canceled: {ex.Message}");
                }
                catch (WebSocketException ex)
                {
                    Debug.WriteLine($"Smallest AI Pulse WebSocket close error: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine($"Smallest AI Pulse WebSocket close skipped: {ex.Message}");
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
                Debug.WriteLine($"Smallest AI Pulse receive loop canceled during dispose: {ex.Message}");
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"Smallest AI Pulse receive loop closed during dispose: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Smallest AI Pulse receive loop parse error during dispose: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Smallest AI Pulse receive loop stopped during dispose: {ex.Message}");
            }
        }

        _sendLock.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}

internal sealed class SmallestAiTranscriptCollector
{
    public string? DetectedLanguage { get; private set; }
    public bool IsLastReceived { get; private set; }

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (IsError(root))
            throw new InvalidOperationException(SmallestAiPlugin.ExtractApiError(root));

        var type = GetString(root, "type");
        if (!string.IsNullOrWhiteSpace(type)
            && !type.Equals("transcription", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var transcript = GetString(root, "transcript")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        var isFinal = GetBool(root, "is_final");
        var isLast = GetBool(root, "is_last");
        IsLastReceived = IsLastReceived || isLast;

        if ((isFinal || isLast)
            && (GetString(root, "language") ?? GetFirstString(root, "languages")) is { } language
            && !string.IsNullOrWhiteSpace(language))
        {
            DetectedLanguage = language;
        }

        return new StreamingTranscriptEvent(transcript, isFinal || isLast);
    }

    private static bool IsError(JsonElement root)
    {
        if (GetString(root, "type") is { } type
            && type.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (GetString(root, "status") is { } status
            && status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return root.TryGetProperty("error", out var error)
            && error.ValueKind is JsonValueKind.Object or JsonValueKind.String;
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? GetFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
