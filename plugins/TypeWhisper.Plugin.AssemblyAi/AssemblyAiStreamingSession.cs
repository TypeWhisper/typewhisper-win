using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.AssemblyAi;

internal sealed class AssemblyAiStreamingSession : IStreamingSession
{
    private const int MinBytes = 1600; // 50 ms, mono PCM16 at 16 kHz.
    private const int MaxBytes = 32000;
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MemoryStream _buffer = new();
    private readonly TaskCompletionSource _begun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receiver;
    private readonly TimeSpan _completionTimeout;
    // Turn state, the sent audio and Terminate, shared by the receiver and FinalizeAsync.
    private readonly object _gate = new();
    private readonly AssemblyAiSentAudioLevels _sentLevels = new();
    private readonly TaskCompletionSource _covered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _terminateSent;
    private double? _lastWordEnd;
    private bool _finishing;
    private int _disposed;
    private int _lastTurn = -1;
    private bool _lastTurnFinal = true;
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal AssemblyAiStreamingSession(WebSocket socket, TimeSpan? completionTimeout = null)
    {
        _socket = socket;
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static Uri BuildUri(AssemblyAiModel model, string? language, string? prompt)
    {
        var code = AssemblyAiModels.Language(language);
        if (code is not null && !(model.IsPro ? model.Languages : AssemblyAiModels.StreamingLanguages).Contains(code))
            throw new NotSupportedException("Use recorded-audio transcription for this language with Universal-2.");
        var speechModel = model.IsPro ? model.Id : code == "en" ? "universal-streaming-english" : "universal-streaming-multilingual";
        var query = "sample_rate=16000&encoding=pcm_s16le&speech_model=" + speechModel;
        if (!model.IsPro) query += "&format_turns=true";
        if (speechModel != "universal-streaming-english") query += "&language_detection=true";
        if (model.IsPro && code is not null) query += "&language_codes=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { code }));
        var terms = AssemblyAiModels.Terms(prompt, model);
        if (terms.Count > 100 || terms.Any(t => t.Length > 50)) throw new NotSupportedException("Dictionary terms require recorded-audio transcription.");
        if (terms.Count > 0) query += "&keyterms_prompt=" + Uri.EscapeDataString(JsonSerializer.Serialize(terms));
        return new("wss://streaming.assemblyai.com/v3/ws?" + query);
    }

    internal static async Task<IStreamingSession> ConnectAsync(string key, AssemblyAiModel model, string? language, string? prompt, CancellationToken ct)
    {
        var uri = BuildUri(model, language, prompt);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", key);
        AssemblyAiStreamingSession? session = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
            session = new(socket);
            await session.WaitForBeginAsync(deadline.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            throw;
        }
    }

    internal async Task WaitForBeginAsync(CancellationToken ct)
    {
        var winner = await Task.WhenAny(_begun.Task, _receiver).WaitAsync(_completionTimeout, ct).ConfigureAwait(false);
        await winner.ConfigureAwait(false);
        if (!_begun.Task.IsCompletedSuccessfully) throw new IOException("AssemblyAI did not begin the session.");
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (pcm16Audio.Length % 2 != 0) throw new ArgumentException("PCM16 requires complete samples.");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
            if (_finishing) throw new InvalidOperationException("Stream already finalized.");
            while (!pcm16Audio.IsEmpty)
            {
                var count = Math.Min(MaxBytes - (int)_buffer.Length, pcm16Audio.Length);
                _buffer.Write(pcm16Audio.Span[..count]);
                pcm16Audio = pcm16Audio[count..];
                if (_buffer.Length >= MinBytes) await FlushAsync(false, ct).ConfigureAwait(false);
            }
        }
        finally { _sendLock.Release(); }
    }

    private async Task FlushAsync(bool padTail, CancellationToken ct)
    {
        if (_buffer.Length == 0) return;
        var audio = _buffer.ToArray();
        _buffer.SetLength(0);
        if (padTail && audio.Length < MinBytes) Array.Resize(ref audio, MinBytes);
        await _socket.SendAsync(audio.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        lock (_gate) _sentLevels.Append(audio);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
            if (_finishing) throw new InvalidOperationException("Stream already finalized.");
            _finishing = true;
            await FlushAsync(true, ct).ConfigureAwait(false);
            await _socket.SendAsync(TerminateMessage, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            lock (_gate) { _terminateSent = true; SignalIfCovered(); }
            // Termination is ordered after the final Turn; a closed socket is not success. It arrives about
            // half a second after the last turn, so a transcript that already covers the audio finishes first.
            var completed = await Task.WhenAny(_receiver, _covered.Task).WaitAsync(_completionTimeout, ct).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private static ReadOnlyMemory<byte> TerminateMessage => "{\"type\":\"Terminate\"}"u8.ToArray();

    /// <summary>
    /// After Terminate: no turn is open and the audio sent after the last confirmed word is quiet, so it holds no
    /// word whose turn could still be on its way. Callers hold <see cref="_gate"/>.
    /// </summary>
    private void SignalIfCovered()
    {
        if (_terminateSent && _lastTurn >= 0 && _lastTurnFinal && _lastWordEnd is { } end && _sentLevels.IsQuietAfter(end))
            _covered.TrySetResult();
    }

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (_socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0 || _receiver.IsCompleted)
            throw new IOException("AssemblyAI live connection is closed.");
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await ReceiveFramesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new IOException("AssemblyAI returned an invalid live response."); }
    }

    private async Task ReceiveFramesAsync(CancellationToken ct)
    {
        var bytes = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult frame;
            do
            {
                frame = await _socket.ReceiveAsync(new ArraySegment<byte>(bytes), ct).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("AssemblyAI live connection ended before completion.");
                if (message.Length + frame.Count > 1024 * 1024) throw new InvalidDataException("AssemblyAI live response is too large.");
                message.Write(bytes, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Unexpected AssemblyAI live response.");
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (root.TryGetProperty("error", out _) || type is "Error" || type is null)
                throw new IOException("AssemblyAI rejected the live transcription request.");
            if (type == "Begin") { _begun.TrySetResult(); continue; }
            if (type == "Termination")
            {
                if (!_finishing || !_lastTurnFinal) throw new IOException("AssemblyAI ended before the final transcript was confirmed.");
                return;
            }
            if (type != "Turn") continue;
            var order = root.GetProperty("turn_order").GetInt32();
            if (order < 0 || order < _lastTurn) throw new IOException("AssemblyAI returned out-of-order turns.");
            if (order > _lastTurn && !_lastTurnFinal) throw new IOException("AssemblyAI skipped an unconfirmed turn.");
            if (order == _lastTurn && _lastTurnFinal) continue; // Same turn re-delivered, not repeated speech.
            var text = root.GetProperty("transcript").GetString() ?? "";
            var final = root.GetProperty("end_of_turn").GetBoolean() && root.GetProperty("turn_is_formatted").GetBoolean();
            var wordEnd = final && !string.IsNullOrWhiteSpace(text) ? LastWordEnd(root) : null;
            TranscriptReceived?.Invoke(new(text, final) { DetectedLanguage = AssemblyAiPlugin.OptionalString(root, "language_code") });
            // FinalizeAsync may only see the turn once the host has its text, or it could finish without it.
            lock (_gate)
            {
                _lastTurn = order;
                _lastTurnFinal = final;
                // An empty final turn keeps the previous word end, as it adds no words.
                if (final && !string.IsNullOrWhiteSpace(text)) _lastWordEnd = wordEnd;
                if (final) SignalIfCovered();
            }
        }
    }

    /// <summary>The end of a turn's last word in seconds, or null when the turn has no word timings.</summary>
    private static double? LastWordEnd(JsonElement turn)
    {
        if (!turn.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array) return null;
        double? end = null;
        foreach (var word in words.EnumerateArray())
            end = word.ValueKind == JsonValueKind.Object && word.TryGetProperty("end", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0 ? ms / 1000 : null;
        return end;
    }

    /// <summary>AssemblyAI bills a session until it is terminated, so a cancelled one sends Terminate before closing.</summary>
    private async Task TerminateCancelledSessionAsync()
    {
        lock (_gate) if (_terminateSent) return;
        if (_socket.State != WebSocketState.Open || _receiver.IsCompleted) return;
        // A send still in progress keeps the lock; the socket is then aborted without Terminate.
        if (!await _sendLock.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _socket.SendAsync(TerminateMessage, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            lock (_gate) _terminateSent = true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort: DisposeAsync aborts the socket next, and AssemblyAI then ends the session by itself.
        }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await TerminateCancelledSessionAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        _socket.Abort();
        try { await _receiver.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _socket.Dispose();
        _lifetime.Dispose();
        _buffer.Dispose();
        _sendLock.Dispose();
    }
}
