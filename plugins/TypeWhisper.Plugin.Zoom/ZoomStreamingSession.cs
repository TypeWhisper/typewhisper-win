using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Zoom;

/// <summary>
/// Streams a dictation to Zoom Scribe's Live mode while it is recorded.
/// </summary>
/// <remarks>
/// The session waits for <c>session.created</c>, sends the settings in one <c>session.update</c> and waits for
/// <c>session.updated</c> before any audio, so a wrong key or a model the account cannot use fails before the recording
/// streams. Audio goes out as binary 16 kHz PCM16 frames of 100 ms. Scribe splits the speech into items:
/// <c>transcription.delta</c> holds the whole partial text of an item so far, <c>transcription.completed</c> its final
/// text. When the recording stops, the session sends <c>session.close</c> and waits for <c>session.closed</c>, which follows
/// the last <c>transcription.completed</c> (0.3 to 0.5 s after the stop in the macOS tests).
/// <para>
/// The host joins final events with spaces, but Zoom writes Chinese and Japanese without spaces between items. So every
/// update is a preview of the whole transcript, and the whole final text arrives as one final event once Zoom closed the
/// session. A session that ends any other way fails, and the host transcribes the complete recording instead.
/// </para>
/// https://developers.zoom.us/docs/ai-services/scribe/live-mode/
/// </remarks>
internal sealed class ZoomStreamingSession : IStreamingSession
{
    internal static readonly Uri LiveUri = new("wss://api.zoom.us/v2/aiservices/scribe/live");
    internal const int FrameBytes = 3200; // 100 ms, mono PCM16 at 16 kHz.
    private readonly WebSocket _socket;
    private readonly string? _pinnedLanguage;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MemoryStream _buffer = new();
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receiver;
    private readonly TimeSpan _timeout;
    // Items in the order they started. Only the receiver changes them; FinalizeAsync reads them once the receiver ended.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, (string Text, bool Completed)> _items = [];
    private string? _detectedLanguage;
    private string _previewText = "";
    private bool _finishing;
    private bool _closeSent;
    private int _disposed;
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal ZoomStreamingSession(WebSocket socket, string? pinnedLanguage, TimeSpan? timeout = null)
    {
        _socket = socket;
        _pinnedLanguage = pinnedLanguage;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    /// <summary>Scribe's <c>session.update</c> for <paramref name="request"/>, with partial text.</summary>
    internal static byte[] SessionUpdate(ZoomRequest request) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["type"] = "session.update", ["config"] = request.Config(interimResults: true),
        ["audio"] = new Dictionary<string, object> { ["format"] = "pcm16" }
    });

    internal static async Task<IStreamingSession> ConnectAsync(string key, ZoomRequest request, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        socket.Options.AddSubProtocol("live-asr");
        ZoomStreamingSession? session = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(LiveUri, deadline.Token).ConfigureAwait(false);
            session = new(socket, request.LanguageCode);
            await session.StartAsync(request, deadline.Token).ConfigureAwait(false);
            return session;
        }
        catch (WebSocketException ex) when (!ct.IsCancellationRequested)
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            throw new IOException("Could not open the Zoom Scribe live session.", ex);
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            throw;
        }
    }

    /// <summary>Waits for <c>session.created</c>, sends the settings and waits until Scribe applied them.</summary>
    internal async Task StartAsync(ZoomRequest request, CancellationToken ct)
    {
        await WaitAsync(_created.Task, "Zoom Scribe did not start the live session.", ct).ConfigureAwait(false);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _socket.SendAsync(SessionUpdate(request), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
        await WaitAsync(_configured.Task, "Zoom Scribe did not apply the live session's settings.", ct).ConfigureAwait(false);
    }

    private async Task WaitAsync(Task signal, string timeoutMessage, CancellationToken ct)
    {
        Task winner;
        try { winner = await Task.WhenAny(signal, _receiver).WaitAsync(_timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IOException(timeoutMessage); }
        await winner.ConfigureAwait(false);
        if (!signal.IsCompletedSuccessfully) throw new IOException("Zoom Scribe ended the live session before it started.");
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
                var count = Math.Min(FrameBytes - (int)_buffer.Length, pcm16Audio.Length);
                _buffer.Write(pcm16Audio.Span[..count]);
                pcm16Audio = pcm16Audio[count..];
                if (_buffer.Length == FrameBytes) await FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally { _sendLock.Release(); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Length == 0) return;
        var audio = _buffer.ToArray();
        _buffer.SetLength(0);
        await _socket.SendAsync(audio.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        // The limit covers the whole stop: waiting for a running send, the last audio, session.close and session.closed.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            await _sendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                await EnsureOpenAsync().ConfigureAwait(false);
                if (_finishing) throw new InvalidOperationException("Stream already finalized.");
                _finishing = true;
                await FlushAsync(deadline.Token).ConfigureAwait(false);
                // Set first: the receiver may read session.closed before this send returns.
                Volatile.Write(ref _closeSent, true);
                await _socket.SendAsync(CloseMessage, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                // The receiver ends at session.closed, which follows the last completed item; anything else is a failure.
                await _receiver.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new IOException("Zoom Scribe did not finish the live transcript."); }
        var text = CurrentText;
        if (text.Length > 0) TranscriptReceived?.Invoke(new(text, true) { DetectedLanguage = _detectedLanguage ?? _pinnedLanguage });
    }

    private static ReadOnlyMemory<byte> CloseMessage => "{\"type\":\"session.close\"}"u8.ToArray();

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (_socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0 || _receiver.IsCompleted)
            throw new IOException("Zoom Scribe live connection is closed.");
    }

    /// <summary>The final text of every finished item and the partial text of the others, in the order the items started.</summary>
    internal string CurrentText => JoinedText(_order.Select(id => _items[id].Text));

    /// <summary>Joins item texts with a space, except between Chinese or Japanese characters, which Zoom writes without spaces.</summary>
    internal static string JoinedText(IEnumerable<string> texts)
    {
        var joined = new System.Text.StringBuilder();
        foreach (var text in texts)
        {
            if (text.Length == 0) continue;
            if (joined.Length > 0 && !(IsWrittenWithoutSpaces(joined, joined.Length - 1) && IsWrittenWithoutSpaces(text, 0))) joined.Append(' ');
            joined.Append(text);
        }
        return joined.ToString();
    }

    private static bool IsWrittenWithoutSpaces(System.Text.StringBuilder text, int index) =>
        IsWrittenWithoutSpaces(char.IsLowSurrogate(text[index]) && index > 0 ? char.ConvertToUtf32(text[index - 1], text[index]) : text[index]);

    private static bool IsWrittenWithoutSpaces(string text, int index) =>
        IsWrittenWithoutSpaces(char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? char.ConvertToUtf32(text, index) : text[index]);

    /// <summary>Chinese and Japanese put no spaces between words or after their punctuation. Korean does, so Hangul is not included.</summary>
    private static bool IsWrittenWithoutSpaces(int codePoint) => codePoint is
        >= 0x3000 and <= 0x303F // CJK symbols and punctuation
        or >= 0x3040 and <= 0x30FF // Hiragana and Katakana
        or >= 0x3400 and <= 0x4DBF // CJK extension A
        or >= 0x4E00 and <= 0x9FFF // CJK ideographs
        or >= 0xF900 and <= 0xFAFF // CJK compatibility ideographs
        or >= 0xFF00 and <= 0xFFEF // Halfwidth and fullwidth forms
        or >= 0x20000 and <= 0x323AF; // CJK extensions B to H

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await ReceiveMessagesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException)
        { throw new IOException("Zoom Scribe returned an invalid live response."); }
        catch (WebSocketException ex) when (!ct.IsCancellationRequested)
        { throw new IOException("Zoom Scribe live connection failed.", ex); }
    }

    private async Task ReceiveMessagesAsync(CancellationToken ct)
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
                if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Zoom Scribe live connection ended before completion.");
                if (message.Length + frame.Count > 1024 * 1024) throw new InvalidDataException("Zoom Scribe live response is too large.");
                message.Write(bytes, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (frame.MessageType != WebSocketMessageType.Text) continue;
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unexpected Zoom Scribe live response.");
            switch (ZoomPlugin.OptionalString(root, "type"))
            {
                case "session.created": _created.TrySetResult(); break;
                case "session.updated": _configured.TrySetResult(); break;
                case "transcription.delta":
                    if (ItemId(root) is { } deltaId && !(_items.TryGetValue(deltaId, out var open) && open.Completed))
                        Update(deltaId, ZoomPlugin.OptionalString(root, "delta"), completed: false);
                    break;
                case "transcription.completed":
                    if (ItemId(root) is not { } itemId) break;
                    if (ZoomPlugin.OptionalString(root, "language") is { Length: > 0 } language) _detectedLanguage = ZoomModels.LanguageCode(language);
                    Update(itemId, ZoomPlugin.OptionalString(root, "transcript"), completed: true);
                    break;
                case "session.closed":
                    if (!Volatile.Read(ref _closeSent)) throw new IOException("Zoom Scribe closed the live session before the dictation ended.");
                    return;
                case "error":
                    // Only a fatal error ends the session. Its message is left out, so no request content reaches logs.
                    var error = root.TryGetProperty("error", out var details) ? details : default;
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("fatal", out var fatal) && fatal.ValueKind == JsonValueKind.False)
                        break;
                    var code = error.ValueKind == JsonValueKind.Object ? ZoomPlugin.Reason(error.GetRawText()) : null;
                    throw new IOException("Zoom Scribe rejected the live transcription request" + (code is null ? "." : $" ({code})."));
                case null: throw new InvalidDataException("Unexpected Zoom Scribe live response.");
            }
        }
    }

    private static string? ItemId(JsonElement root) => ZoomPlugin.OptionalString(root, "item_id") is { Length: > 0 } id ? id : null;

    private void Update(string itemId, string? text, bool completed)
    {
        if (!_items.ContainsKey(itemId)) _order.Add(itemId);
        _items[itemId] = ((text ?? "").Trim(), completed);
        var preview = CurrentText;
        if (preview.Length == 0 || preview == _previewText) return;
        _previewText = preview;
        TranscriptReceived?.Invoke(new(preview, false));
    }

    /// <summary>A dictation that is cancelled still closes its Zoom session, so Zoom stops transcribing it.</summary>
    private async Task CloseCancelledSessionAsync()
    {
        if (Volatile.Read(ref _closeSent) || _socket.State != WebSocketState.Open || _receiver.IsCompleted) return;
        // A send still in progress keeps the lock; the socket is then aborted without session.close.
        if (!await _sendLock.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Volatile.Write(ref _closeSent, true);
            await _socket.SendAsync(CloseMessage, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort: DisposeAsync aborts the socket next, and Zoom then ends the session by itself.
        }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseCancelledSessionAsync().ConfigureAwait(false);
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
