using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Reson8;

internal sealed class Reson8StreamingSession : IStreamingSession
{
    /// <summary>256 ms of audio, like the macOS plugin; Reson8 closes the connection on frames above 64 KiB.</summary>
    internal const int MaximumFrameBytes = 8_192;

    private readonly ClientWebSocket _ws;
    private readonly Reson8TranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _flushConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private bool _disposed;
    private long _sentAudioBytes;
    private readonly TimeSpan? _finalizationTimeout;

    private Reson8StreamingSession(ClientWebSocket ws, Reson8TranscriptCollector collector, TimeSpan? finalizationTimeout)
    {
        _ws = ws;
        _collector = collector;
        _finalizationTimeout = finalizationTimeout;
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
        string authHeader,
        Uri endpoint,
        string? requestedLanguage,
        CancellationToken ct, TimeSpan? connectionTimeout = null, TimeSpan? finalizationTimeout = null)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateStreamingHeaders(apiKey, authHeader))
            ws.Options.SetRequestHeader(header.Key, header.Value);
        // Reson8 names the reason for a rejected connection, such as an unknown custom model, in X-Error-Message.
        ws.Options.CollectHttpResponseDetails = true;

        using var timeout = new CancellationTokenSource(connectionTimeout ?? TimeSpan.FromSeconds(15));
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await ws.ConnectAsync(endpoint, connection.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { ws.Dispose(); throw new TimeoutException("The Reson8 streaming connection timed out.", ex); }
        catch (WebSocketException ex) when (RejectionOf(ws, ex) is { } rejection)
        { ws.Dispose(); throw rejection; }
        catch { ws.Dispose(); throw; }

        var session = new Reson8StreamingSession(ws, new Reson8TranscriptCollector(requestedLanguage), finalizationTimeout);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    /// <summary>
    /// The realtime endpoint of a server with the query the plugin built.
    /// </summary>
    public static Uri BuildRealtimeUri(string baseUrl, Reson8Query query)
    {
        var normalizedBase = baseUrl.Trim().TrimEnd('/');
        var baseUri = new Uri(normalizedBase);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/v1/speech-to-text/realtime",
            Query = query.ToString()
        };
        return builder.Uri;
    }

    /// <summary>A rejected upgrade with Reson8's status and reason, or null for other connection failures.</summary>
    internal static PluginRequestException? RejectionOf(ClientWebSocket ws, WebSocketException exception)
    {
        var status = (int)ws.HttpStatusCode;
        if (status is < 400 or > 599)
            return null;
        var reason = ws.HttpResponseHeaders is { } headers
            && headers.TryGetValue("X-Error-Message", out var values) && values.FirstOrDefault() is { Length: > 0 } value
                ? value.Length > 300 ? value[..300] : value
                : "no reason given";
        var kind = status switch
        {
            401 => PluginRequestFailureKind.Authentication,
            402 or 403 => PluginRequestFailureKind.Permission,
            429 => PluginRequestFailureKind.RateLimit,
            >= 500 => PluginRequestFailureKind.ServerError,
            _ => PluginRequestFailureKind.InvalidRequest
        };
        return new PluginRequestException($"Reson8 rejected the live connection ({status}): {reason}", kind, status, innerException: exception);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_flushConfirmed.Task.IsFaulted) await _flushConfirmed.Task;
        if (pcm16Audio.Length == 0) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_flushConfirmed.Task.IsFaulted) await _flushConfirmed.Task;
            if (_ws.State != WebSocketState.Open) throw new WebSocketException("The streaming connection is no longer open.");
            // Reson8 closes the connection on frames above 64 KiB; the host may hand over seconds of buffered audio at once.
            for (var remaining = pcm16Audio; !remaining.IsEmpty;)
            {
                var frame = remaining[..Math.Min(MaximumFrameBytes, remaining.Length)];
                await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                Interlocked.Add(ref _sentAudioBytes, frame.Length);
                remaining = remaining[frame.Length..];
            }
            if (_flushConfirmed.Task.IsFaulted) await _flushConfirmed.Task;
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
        // Prerecorded audio may be sent faster than realtime. Give the provider
        // its audio duration plus grace; live callers can impose a shorter token.
        using var timeout = new CancellationTokenSource(_finalizationTimeout ??
            TimeSpan.FromSeconds(15 + Interlocked.Read(ref _sentAudioBytes) / 32000d));
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var finishToken = completion.Token;
        try
        {
            if (_disposed || _ws.State != WebSocketState.Open)
            { await _flushConfirmed.Task.WaitAsync(finishToken); return; }

            var json = $$"""{"type":"flush_request","id":"{{Guid.NewGuid()}}"}""";
            await _sendLock.WaitAsync(finishToken);
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    var payload = Encoding.UTF8.GetBytes(json);
                    await _ws.SendAsync(payload, WebSocketMessageType.Text, true, finishToken);
                }
            }
            finally { _sendLock.Release(); }
            await _flushConfirmed.Task.WaitAsync(finishToken);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The Reson8 streaming completion timed out.", ex);
        }
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
                        _flushConfirmed.TrySetException(new WebSocketException("The provider closed before stream completion."));
                        return;
                    }

                    if (messageBuffer.Length + result.Count > 4 * 1024 * 1024) throw new InvalidOperationException("Streaming message exceeds the size limit.");
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
            _flushConfirmed.TrySetException(ex);
            Debug.WriteLine($"Reson8 receive loop canceled: {ex.Message}");
        }
        catch (WebSocketException ex)
        {
            _flushConfirmed.TrySetException(ex);
            Debug.WriteLine($"Reson8 WebSocket error: {ex.Message}");
            _flushConfirmed.TrySetException(ex);
        }
        catch (JsonException ex)
        {
            _flushConfirmed.TrySetException(ex);
            Debug.WriteLine($"Reson8 parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _flushConfirmed.TrySetException(ex);
            Debug.WriteLine($"Reson8 stream error: {ex.Message}");
            _flushConfirmed.TrySetException(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _flushConfirmed.TrySetException(ex);
            Debug.WriteLine($"Reson8 receive callback failed: {ex.GetType().Name}");
        }
        finally
        {
            _flushConfirmed.TrySetException(new IOException("The receive loop ended before stream completion."));
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
                try { _ws.Abort(); }
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

internal sealed class Reson8TranscriptCollector(string? requestedLanguage = null)
{
    private readonly List<string> _finals = [];
    private readonly List<string> _languages = [];
    private string _interim = "";

    /// <summary>The language most final transcripts were detected as.</summary>
    public string? DetectedLanguage =>
        _languages.GroupBy(language => language, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();

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
        string? language = null;
        if (isFinal)
        {
            _finals.Add(text);
            _interim = "";
            language = Reson8Transcript.DetectedLanguage(GetString(root, "language"), requestedLanguage);
            if (language is not null)
                _languages.Add(language);
        }
        else
        {
            _interim = text;
        }

        return new StreamingTranscriptEvent(text, isFinal) { DetectedLanguage = language };
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
            if (!string.IsNullOrWhiteSpace(evt.DetectedLanguage))
                _languages.Add(evt.DetectedLanguage);
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
