using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

internal sealed class XaiStreamingSession : IStreamingSession
{
    private readonly ClientWebSocket _ws;
    private readonly XaiTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveTask;
    private bool _disposed;

    private XaiStreamingSession(ClientWebSocket ws, XaiTranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<XaiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct)
    {
        var ws = CreateConfiguredWebSocket(apiKey);
        await ws.ConnectAsync(BuildStreamingUri(language, interimResults: true), ct);

        var collector = new XaiTranscriptCollector();
        var session = new XaiStreamingSession(ws, collector);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public static Uri BuildStreamingUri(string? language, bool interimResults)
    {
        var query = new List<string>
        {
            "sample_rate=16000",
            "encoding=pcm",
            $"interim_results={(interimResults ? "true" : "false")}",
        };

        if (!string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }

        return new Uri("wss://api.x.ai/v1/stt?" + string.Join("&", query));
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
            if (_disposed || _ws.State != WebSocketState.Open)
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
            if (_disposed || _ws.State != WebSocketState.Open)
                return;

            await SendTextAsync("""{"type":"audio.done"}""", ct);
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
                var transcriptEvent = _collector.ApplyEvent(json);
                if (transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"xAI STT WebSocket error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"xAI STT parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"xAI STT stream error: {ex.Message}");
        }
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

internal sealed class XaiTranscriptCollector
{
    private readonly List<string> _finals = [];
    private string _interim = "";
    private string? _doneText;
    private string? _detectedLanguage;
    private double _duration;

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Invalid xAI STT event.");
        }

        return typeEl.GetString() switch
        {
            "transcript.created" => null,
            "transcript.partial" => ApplyPartialEvent(root),
            "transcript.done" => ApplyDoneEvent(root),
            "error" => throw new InvalidOperationException(ExtractErrorMessage(root) ?? "Unknown xAI STT error"),
            _ => null
        };
    }

    public PluginTranscriptionResult FinalResult(string? fallbackLanguage)
    {
        var text = !string.IsNullOrWhiteSpace(_doneText)
            ? _doneText!
            : string.Join(" ", _finals).Trim();

        return new PluginTranscriptionResult(text, _detectedLanguage ?? fallbackLanguage ?? "", _duration);
    }

    private StreamingTranscriptEvent ApplyPartialEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        var isFinal = GetBool(root, "is_final");
        var speechFinal = GetBool(root, "speech_final");
        RememberMetadata(root);

        if (isFinal)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                var joined = string.Join(" ", _finals);
                if (speechFinal && _finals.Count > 0 && text.StartsWith(joined, StringComparison.Ordinal))
                    _finals.Clear();

                if (_finals.LastOrDefault() != text)
                    _finals.Add(text);
            }
            _interim = "";
            return new StreamingTranscriptEvent(CurrentText(), IsFinal: true);
        }

        _interim = text;
        return new StreamingTranscriptEvent(CurrentText(), IsFinal: false);
    }

    private StreamingTranscriptEvent ApplyDoneEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(text))
        {
            _doneText = text;
            _interim = "";
        }
        RememberMetadata(root);
        return new StreamingTranscriptEvent(CurrentText(), IsFinal: true);
    }

    private string CurrentText()
    {
        if (!string.IsNullOrWhiteSpace(_doneText))
            return _doneText!;

        var parts = new List<string>(_finals);
        if (!string.IsNullOrWhiteSpace(_interim))
            parts.Add(_interim);
        return string.Join(" ", parts).Trim();
    }

    private void RememberMetadata(JsonElement root)
    {
        if (GetString(root, "language") is { } language && !string.IsNullOrWhiteSpace(language))
            _detectedLanguage = language;

        if (root.TryGetProperty("duration", out var durationEl)
            && durationEl.ValueKind == JsonValueKind.Number
            && durationEl.TryGetDouble(out var duration))
        {
            _duration = duration;
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

    private static string? ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && GetString(error, "message") is { } objectMessage)
                return objectMessage;
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return GetString(root, "message");
    }
}
