using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Meta;

internal sealed class MetaRealtimeStreamingSession : IStreamingSession
{
    internal const int MaximumMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket _webSocket;
    private readonly MetaRealtimeTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _terminalTranscript =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private int _disposeStarted;
    private int _finalizationRequested;
    private bool _disposed;

    internal MetaRealtimeStreamingSession(
        ClientWebSocket webSocket,
        string mode = "PUSH_TO_TALK")
    {
        _webSocket = webSocket;
        _collector = new MetaRealtimeTranscriptCollector(mode);
    }

    /// <inheritdoc />
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal Task TerminalTranscriptTask => _terminalTranscript.Task;

    internal static async Task<MetaRealtimeStreamingSession> ConnectAsync(
        string apiKey,
        string modelId,
        string mode,
        IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords,
        CancellationToken ct, Uri? endpoint = null)
    {
        var webSocket = new ClientWebSocket();
        var connected = false;
        try
        {
            await webSocket.ConnectAsync(endpoint ?? new Uri("wss://api.meta.ai/v1/asr/realtime"), ct);
            var session = new MetaRealtimeStreamingSession(webSocket, mode);
            await session.SendTextAsync(
                CreateHandshakeJson(apiKey, modelId, mode, languageBias, keywords),
                ct);
            var acknowledgement = await ReceiveTextMessageAsync(webSocket, ct);
            ValidateHandshakeAcknowledgement(acknowledgement ?? throw new WebSocketException("Meta closed before acknowledging the session."));
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
        string mode,
        IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords)
    {
        var body = new Dictionary<string, object?>
        {
            ["authorization"] = new Dictionary<string, string>
            {
                ["accessToken"] = $"Bearer {apiKey}",
            },
            ["audioEncoding"] = "PCM_16KHZ",
            ["model"] = modelId,
            ["mode"] = mode,
            ["partialMode"] = "CUMULATIVE",
            ["emitAudioProgress"] = false,
        };
        if (languageBias.Count > 0)
            body["languageBias"] = languageBias;
        if (keywords.Count > 0)
            body["keywords"] = keywords;

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
            if (_terminalTranscript.Task.IsFaulted || _terminalTranscript.Task.IsCanceled)
                await _terminalTranscript.Task;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (FinalizationRequested) return;
            if (_webSocket.State != WebSocketState.Open)
                throw new WebSocketException("The realtime session is no longer open.");
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
            if (_terminalTranscript.Task.IsFaulted || _terminalTranscript.Task.IsCanceled)
                await _terminalTranscript.Task;
            if (_disposed || _webSocket.State != WebSocketState.Open)
                throw new WebSocketException("The realtime session is no longer open.");

            if (!FinalizationRequested)
            {
                RequestFinalization();
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

        // Abort before waiting: a send or close acknowledgement may hold the lock.
        _disposed = true;
        _receiveCts.Cancel();
        _webSocket.Abort();
        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            _terminalTranscript.TrySetException(
                new ObjectDisposedException(nameof(MetaRealtimeStreamingSession)));
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            if (_receiveTask is not null)
                await _receiveTask;
        }
        finally
        {
            _sendLock.Dispose();
            _receiveCts.Dispose();
            _webSocket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextMessageAsync(_webSocket, ct);
                if (json is null)
                {
                    if (!FinalizationRequested || !_collector.HasCompletedTranscript)
                        throw new WebSocketException("Meta closed before completing the transcript.");
                    await _sendLock.WaitAsync(ct);
                    try { await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct); }
                    finally { _sendLock.Release(); }
                    PublishTranscript(new StreamingTranscriptEvent(_collector.CompletedText, IsFinal: true), isTerminal: true);
                    return;
                }
                var update = _collector.Apply(json);
                // PUSH_TO_TALK has an explicit stream-final transcript. Meta can close the
                // transport without a close frame afterwards; that does not invalidate this result.
                if (FinalizationRequested && _collector.HasFinalSingleTurn)
                {
                    PublishTranscript(new StreamingTranscriptEvent(_collector.CompletedText, IsFinal: true), isTerminal: true);
                    return;
                }
                // speechComplete finalizes one turn, not the stream. Keep receiving until clean closure.
                PublishTranscript(update.Transcript is { } snapshot ? snapshot with { IsFinal = false } : null, isTerminal: false);
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
        catch (Exception ex)
        {
            // Subscriber failures must also stop audio input and fail finalization promptly.
            _terminalTranscript.TrySetException(ex);
            Debug.WriteLine($"Meta realtime receiver failed: {ex.GetType().Name}");
        }
        finally
        {
            if (FinalizationRequested && !_terminalTranscript.Task.IsCompleted)
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

    internal void RequestFinalization() =>
        Volatile.Write(ref _finalizationRequested, 1);

    private bool FinalizationRequested => Volatile.Read(ref _finalizationRequested) != 0;

    private static async Task<string?> ReceiveTextMessageAsync(
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
            {
                if (result.CloseStatus == WebSocketCloseStatus.NormalClosure && message.Length == 0)
                    return null;
                throw new WebSocketException("Meta closed the realtime session unexpectedly.");
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("Meta returned an unexpected binary realtime message.");
            if (message.Length + result.Count > MaximumMessageBytes)
                throw new WebSocketException("Meta realtime message exceeds the 1 MiB limit.");
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

internal sealed class MetaRealtimeTranscriptCollector
{
    private sealed class Turn(int id)
    {
        public int Id { get; } = id;
        public string Transcript { get; set; } = "";
        public string? Speaker { get; set; }
        public bool Completed { get; set; }
    }

    private readonly bool _usesDiarization;
    private readonly Dictionary<int, Turn> _turns = [];
    private int? _activeTurnId;
    private string _interim = "";
    private string _finalSingleTurnText = "";
    private bool _hasFinalSingleTurn;

    internal bool HasFinalSingleTurn => _hasFinalSingleTurn;
    internal bool HasCompletedTranscript => _hasFinalSingleTurn ||
        (_usesDiarization && _turns.Values.All(turn => turn.Completed) && string.IsNullOrEmpty(_interim));
    internal string CompletedText => BuildSnapshot();

    internal MetaRealtimeTranscriptCollector(string mode)
    {
        _usesDiarization = mode.Equals("DIARIZATION", StringComparison.OrdinalIgnoreCase);
    }

    internal MetaRealtimeUpdate Apply(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
            return default;

        var type = typeElement.GetString();
        if (type == "error")
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new InvalidOperationException(message ?? "Meta realtime transcription failed.");
        }

        var isFinalEvent = false;
        switch (type)
        {
            case "speechStart":
                if (TryGetInt(root, "turnId", out var startedTurnId))
                {
                    GetOrCreateTurn(startedTurnId);
                    _activeTurnId = startedTurnId;
                    _interim = "";
                }
                break;

            case "speaker":
                if (_activeTurnId is { } speakerTurnId)
                {
                    var turn = GetOrCreateTurn(speakerTurnId);
                    turn.Speaker = root.TryGetProperty("label", out var labelElement)
                        ? labelElement.GetString()
                        : null;
                }
                break;

            case "speechEnd":
                break;

            case "speechComplete":
                if (TryGetInt(root, "turnId", out var completedTurnId))
                {
                    var turn = GetOrCreateTurn(completedTurnId);
                    turn.Transcript = GetTranscript(root);
                    turn.Completed = true;
                    if (_activeTurnId == completedTurnId)
                    {
                        _activeTurnId = null;
                        _interim = "";
                    }
                    isFinalEvent = true;
                }
                break;

            case "transcript":
                var transcript = GetTranscript(root);
                var isFinal = root.TryGetProperty("final", out var finalElement)
                    && finalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && finalElement.GetBoolean();
                if (isFinal && !_usesDiarization)
                {
                    _finalSingleTurnText = transcript;
                    _hasFinalSingleTurn = true;
                    _interim = "";
                    isFinalEvent = true;
                }
                else
                {
                    _interim = transcript;
                    isFinalEvent = isFinal;
                }
                break;

            default:
                return default;
        }

        var snapshot = BuildSnapshot();
        return new MetaRealtimeUpdate(
            string.IsNullOrWhiteSpace(snapshot)
                ? null
                : new StreamingTranscriptEvent(snapshot, isFinalEvent),
            isFinalEvent);
    }

    private string BuildSnapshot()
    {
        if (!_usesDiarization && _hasFinalSingleTurn)
            return _finalSingleTurnText;

        var parts = _turns.Values
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Transcript))
            .OrderBy(turn => turn.Id)
            .Select(turn => _usesDiarization ? FormatTurn(turn) : turn.Transcript)
            .ToList();
        if (parts.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(_interim))
            {
                var interimTurn = _activeTurnId is { } activeTurnId
                    ? GetOrCreateTurn(activeTurnId)
                    : null;
                parts.Add(_usesDiarization
                    ? FormatText(_interim, interimTurn?.Speaker)
                    : _interim);
            }

            return string.Join(_usesDiarization ? "\n" : " ", parts);
        }

        if (!_usesDiarization)
            return _interim;

        if (!string.IsNullOrWhiteSpace(_interim))
        {
            var interimTurn = _activeTurnId is { } activeTurnId
                ? GetOrCreateTurn(activeTurnId)
                : null;
            parts.Add(FormatText(_interim, interimTurn?.Speaker));
        }

        return string.Join("\n", parts);
    }

    private static string FormatTurn(Turn turn) => FormatText(turn.Transcript, turn.Speaker);

    private static string FormatText(string text, string? speaker) =>
        MetaPlugin.NormalizeSpeakerLabel(speaker) is { } label
            ? $"{label}: {text}"
            : text;

    private Turn GetOrCreateTurn(int id)
    {
        if (_turns.TryGetValue(id, out var turn))
            return turn;
        turn = new Turn(id);
        _turns[id] = turn;
        return turn;
    }

    private static string GetTranscript(JsonElement root)
    {
        if (!root.TryGetProperty("transcript", out var transcriptElement) || transcriptElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Meta returned no string transcript in a realtime event.");
        return transcriptElement.GetString()!.Trim();
    }

    private static bool TryGetInt(JsonElement root, string propertyName, out int value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}

internal readonly record struct MetaRealtimeUpdate(
    StreamingTranscriptEvent? Transcript,
    bool IsFinalEvent);
