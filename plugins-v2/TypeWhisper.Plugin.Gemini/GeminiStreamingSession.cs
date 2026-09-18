using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Gemini;

internal sealed class GeminiStreamingSession : IStreamingSession
{
    private const string LiveWebSocketUrl =
        "wss://generativelanguage.googleapis.com/ws/" +
        "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly ClientWebSocket _webSocket;
    private readonly GeminiStreamingTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _setupCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private int _disposeStarted;
    private int _finalizeStarted;
    private long _sentAudioBytes;
    private bool _receivedFinalTranscript;
    private bool _receivedActivityEnd;

    private GeminiStreamingSession(
        ClientWebSocket webSocket,
        GeminiStreamingTranscriptCollector collector)
    {
        _webSocket = webSocket;
        _collector = collector;
    }

    /// <inheritdoc />
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    /// <summary>
    /// Opens and configures a Gemini Live transcription session.
    /// </summary>
    public static async Task<GeminiStreamingSession> ConnectAsync(
        string apiKey,
        string modelId,
        IReadOnlyList<string> languageHints,
        IReadOnlyList<string> customVocabulary,
        GeminiTranscriptionMode mode,
        CancellationToken ct,
        Uri? endpoint = null)
    {
        var webSocket = new ClientWebSocket();
        GeminiStreamingSession? session = null;
        try
        {
            await webSocket.ConnectAsync(endpoint ?? BuildWebSocketUri(apiKey), ct);

            session = new GeminiStreamingSession(
                webSocket,
                new GeminiStreamingTranscriptCollector());
            session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
            await session.SendTextAsync(
                CreateSetupPayload(modelId, languageHints, customVocabulary, mode),
                ct);
            await session._setupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await session.SendTextAsync("""{"realtimeInput":{"activityStart":{}}}""", ct);
            return session;
        }
        catch
        {
            if (session is null)
                webSocket.Dispose();
            else
                await session.DisposeAsync();
            throw;
        }
    }

    internal static Uri BuildWebSocketUri(string apiKey) =>
        new($"{LiveWebSocketUrl}?key={Uri.EscapeDataString(apiKey)}");

    internal static string CreateSetupPayload(
        string modelId,
        IReadOnlyList<string> languageHints,
        IReadOnlyList<string> customVocabulary,
        GeminiTranscriptionMode mode)
    {
        var transcription = new Dictionary<string, object?>
        {
            ["languageCodes"] = languageHints,
            ["mode"] = mode == GeminiTranscriptionMode.Smart ? "SMART" : "VERBATIM",
        };
        if (customVocabulary.Count > 0)
            transcription["customVocabulary"] = customVocabulary;

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["setup"] = new Dictionary<string, object?>
            {
                ["model"] = $"models/{NormalizeModelId(modelId)}",
                ["generationConfig"] = new Dictionary<string, object?>
                {
                    ["responseModalities"] = new[] { "TEXT" },
                },
                ["inputAudioTranscription"] = transcription,
                ["realtimeInputConfig"] = new
                {
                    automaticActivityDetection = new { disabled = true }
                },
            }
        });
    }

    internal static string CreateAudioPayload(ReadOnlySpan<byte> pcm16Audio) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["realtimeInput"] = new Dictionary<string, object?>
            {
                ["audio"] = new Dictionary<string, object?>
                {
                    ["data"] = Convert.ToBase64String(pcm16Audio),
                    ["mimeType"] = "audio/pcm;rate=16000",
                }
            }
        });

    /// <inheritdoc />
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (Volatile.Read(ref _finalizeStarted) != 0) throw new InvalidOperationException("Gemini audio input has ended.");
            if (_completed.Task.IsFaulted) await _completed.Task;
            if (_webSocket.State != WebSocketState.Open) throw new IOException("Gemini live connection is closed.");
            await SendTextAsync(CreateAudioPayload(pcm16Audio.Span), ct);
            Interlocked.Add(ref _sentAudioBytes, pcm16Audio.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (_completed.Task.IsFaulted) await _completed.Task;
            if (Interlocked.Exchange(ref _finalizeStarted, 1) == 0)
            {
                if (_webSocket.State != WebSocketState.Open) throw new IOException("Gemini live connection is closed.");
                await SendTextAsync(
                    """{"realtimeInput":{"activityEnd":{}}}""",
                    ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
        await _completed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16_384];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _setupCompleted.TrySetException(new IOException("Gemini closed the live connection before setup completed."));
                        _completed.TrySetException(new IOException("Gemini closed the live connection before transcription completed."));
                        return;
                    }
                    messageBuffer.Write(buffer, 0, result.Count);
                    if (messageBuffer.Length > 4 * 1024 * 1024) throw new IOException("Gemini live message is too large.");
                }
                while (!result.EndOfMessage);

                if (!IsJsonMessageType(result.MessageType))
                    continue;

                var json = Encoding.UTF8.GetString(
                    messageBuffer.GetBuffer(),
                    0,
                    (int)messageBuffer.Length);
                var update = _collector.ApplyEvent(json);

                if (update.SetupCompleted)
                    _setupCompleted.TrySetResult(true);
                if (update.ErrorMessage is { } errorMessage)
                {
                    throw new InvalidOperationException(errorMessage);
                }
                if (update.Transcript is not null)
                    NotifyTranscriptHandlers(TranscriptReceived, update.Transcript);
                // Manual VAD submits exactly one activity per connection. Never reuse an
                // earlier segment: an unsolicited final or boundary invalidates this stream
                // and the host must transcribe the complete recording instead.
                if (update.TranscriptFinalized)
                {
                    if (Volatile.Read(ref _finalizeStarted) == 0 || _receivedFinalTranscript)
                        throw new IOException("Gemini finalized an unexpected transcription segment.");
                    _receivedFinalTranscript = true;
                }
                if (update.ActivityEndedAtSeconds is { } endOffset)
                {
                    if (Volatile.Read(ref _finalizeStarted) == 0 || _receivedActivityEnd ||
                        endOffset < Interlocked.Read(ref _sentAudioBytes) / 32000m)
                        throw new IOException("Gemini acknowledged an unexpected audio boundary.");
                    _receivedActivityEnd = true;
                }
                // The final transcript and its sole activity acknowledgement can arrive in
                // either order. Retain both until this manually submitted activity is done.
                if (_receivedFinalTranscript && _receivedActivityEnd)
                    _completed.TrySetResult(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Debug.WriteLine("Gemini Live receive loop canceled.");
        }
        catch (Exception ex)
        {
            _setupCompleted.TrySetException(ex);
            _completed.TrySetException(ex);
            Debug.WriteLine($"Gemini Live receive error: {ex.Message}");
        }
    }

    internal static bool TryApplyEvent(
        GeminiStreamingTranscriptCollector collector,
        string json,
        out GeminiStreamingUpdate update)
    {
        try
        {
            update = collector.ApplyEvent(json);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Debug.WriteLine($"Gemini Live event parse error: {ex.Message}");
            update = GeminiStreamingUpdate.Empty;
            return false;
        }
    }

    internal static void NotifyTranscriptHandlers(
        Action<StreamingTranscriptEvent>? handlers,
        StreamingTranscriptEvent transcript)
    {
        if (handlers is null)
            return;

        foreach (Action<StreamingTranscriptEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(transcript);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Gemini Live transcript handler error: {ex.Message}");
            }
        }
    }

    internal static bool IsJsonMessageType(WebSocketMessageType messageType) =>
        messageType is WebSocketMessageType.Text or WebSocketMessageType.Binary;

    private static string NormalizeModelId(string modelId) =>
        modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId["models/".Length..]
            : modelId;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _receiveCts.Cancel();

        _completed.TrySetCanceled();
        _webSocket.Abort();

        if (_receiveTask is not null)
            await _receiveTask;

        _receiveCts.Dispose();
        _webSocket.Dispose();
    }
}

internal sealed class GeminiStreamingTranscriptCollector
{
    /// <summary>
    /// Parses one Gemini Live server event.
    /// </summary>
    public GeminiStreamingUpdate ApplyEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryGetProperty(root, "setupComplete", "setup_complete", out _))
            return new GeminiStreamingUpdate(SetupCompleted: true, null, null);

        if (root.TryGetProperty("error", out var error))
        {
            var message = TryGetString(error, "message")
                ?? (error.ValueKind == JsonValueKind.String ? error.GetString() : null)
                ?? "Unknown Gemini Live error";
            return new GeminiStreamingUpdate(false, null, message);
        }

        decimal? activityEnd = null;
        if (TryGetProperty(root, "voiceActivity", "voice_activity", out var activity) &&
            TryGetString(activity, "type") == "ACTIVITY_END" &&
            (TryGetString(activity, "audioOffset") ?? TryGetString(activity, "audio_offset")) is { } offset &&
            offset.EndsWith('s') && decimal.TryParse(offset.AsSpan(0, offset.Length - 1),
                System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            activityEnd = seconds;
        var update = GeminiStreamingUpdate.Empty with { ActivityEndedAtSeconds = activityEnd };
        if (!TryGetProperty(root, "serverContent", "server_content", out var serverContent))
            return update;

        if (TryGetProperty(
                serverContent,
                "inputTranscription",
                "input_transcription",
                out var final)
            && TryGetString(final, "text") is { } finalText)
        {
            return new GeminiStreamingUpdate(
                false,
                string.IsNullOrWhiteSpace(finalText) ? null : new StreamingTranscriptEvent(finalText.Trim(), IsFinal: true),
                null, TranscriptFinalized: true, ActivityEndedAtSeconds: activityEnd);
        }

        if (TryGetProperty(
                serverContent,
                "interimInputTranscription",
                "interim_input_transcription",
                out var interim)
            && TryGetString(interim, "text") is { } interimText
            && !string.IsNullOrWhiteSpace(interimText))
        {
            return new GeminiStreamingUpdate(
                false,
                new StreamingTranscriptEvent(interimText.Trim(), IsFinal: false),
                null, ActivityEndedAtSeconds: activityEnd);
        }

        return update;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string camelCaseName,
        string snakeCaseName,
        out JsonElement value) =>
        element.TryGetProperty(camelCaseName, out value)
        || element.TryGetProperty(snakeCaseName, out value);

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

internal sealed record GeminiStreamingUpdate(
    bool SetupCompleted,
    StreamingTranscriptEvent? Transcript,
    string? ErrorMessage,
    bool TranscriptFinalized = false,
    decimal? ActivityEndedAtSeconds = null)
{
    public static GeminiStreamingUpdate Empty { get; } = new(false, null, null);
}
