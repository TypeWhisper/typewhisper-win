using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Speechmatics;

/// <summary>Speechmatics Realtime transport: PCM16 input, accumulated previews, one authoritative final transcript.</summary>
internal sealed class SpeechmaticsStreamingSession : IStreamingSession
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sequence;
    private string _pending = "";
    private readonly Task _receiver;
    private readonly TimeSpan _completionTimeout;
    private readonly StringBuilder _preview = new();
    private readonly string _language;
    private volatile bool _finishing;
    private bool _ready;
    private int _disposed;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal SpeechmaticsStreamingSession(WebSocket socket, string language, TimeSpan? completionTimeout = null)
    {
        _socket = socket;
        _language = language;
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static async Task<IStreamingSession> ConnectAsync(Uri endpoint, string key, string language, object configuration, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        socket.Options.CollectHttpResponseDetails = true;
        using var http = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
        SpeechmaticsStreamingSession? session = null;
        try
        {
            await socket.ConnectAsync(endpoint, http, ct).ConfigureAwait(false);
            session = new(socket, language);
            await session.InitializeAsync(configuration, ct).ConfigureAwait(false);
            return session;
        }
        catch (WebSocketException)
        {
            var status = (int)socket.HttpStatusCode;
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket.Dispose();
            throw status >= 400 ? ProviderError(status) : new PluginRequestException("Speechmatics live connection failed.", PluginRequestFailureKind.Network);
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket.Dispose();
            throw;
        }
    }

    internal async Task InitializeAsync(object configuration, CancellationToken ct)
    {
        await SendJsonAsync(new
        {
            message = "StartRecognition",
            audio_format = new { type = "raw", encoding = "pcm_s16le", sample_rate = 16000 },
            transcription_config = configuration
        }, ct).ConfigureAwait(false);
        await WaitForEventAsync(_created.Task, ct).ConfigureAwait(false);
        _ready = true;
    }

    private async Task WaitForEventAsync(Task signal, CancellationToken ct)
    {
        await Task.WhenAny(signal, _receiver).WaitAsync(ct).ConfigureAwait(false);
        if (_receiver.IsCompleted) { await _receiver.ConfigureAwait(false); throw Incomplete(); }
        await signal.ConfigureAwait(false);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (pcm16Audio.Length % 2 != 0) throw new ArgumentException("PCM16 requires complete samples.", nameof(pcm16Audio));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
            while (!pcm16Audio.IsEmpty)
            {
                var count = Math.Min(32000, pcm16Audio.Length);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                try { await _socket.SendAsync(pcm16Audio[..count], WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false); }
                catch (WebSocketException) { throw new PluginRequestException("Speechmatics live connection failed.", PluginRequestFailureKind.Network); }
                _sequence++;
                pcm16Audio = pcm16Audio[count..];
            }
        }
        finally { _sendLock.Release(); }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
            _finishing = true;
            await SendJsonAsync(new { message = "EndOfStream", last_seq_no = _sequence }, ct).ConfigureAwait(false);
            await _receiver.WaitAsync(_completionTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new PluginRequestException("Speechmatics did not finish the live transcript in time.", PluginRequestFailureKind.Timeout); }
        finally { _sendLock.Release(); }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload).AsMemory(), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false); }
        catch (WebSocketException) { throw new PluginRequestException("Speechmatics live connection failed.", PluginRequestFailureKind.Network); }
    }

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (!_ready || _finishing || _receiver.IsCompleted || _socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0)
            throw Incomplete("Speechmatics live connection is not open for audio.");
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await ReceiveFramesAsync(ct).ConfigureAwait(false); }
        catch (WebSocketException) { throw new PluginRequestException("Speechmatics live connection was interrupted.", PluginRequestFailureKind.Network); }
        catch (Exception ex) when (ex is not PluginRequestException && ex is (JsonException or InvalidOperationException or KeyNotFoundException or FormatException))
        { throw Incomplete(); }
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
                if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > MaximumMessageBytes) throw Incomplete();
                message.Write(bytes, 0, frame.Count);
            } while (!frame.EndOfMessage);
            using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = document.RootElement;
            var type = ProviderConnection.RequiredText(root, "message");
            switch (type)
            {
                case "RecognitionStarted": _created.TrySetResult(); break;
                case "Error":
                    throw new PluginRequestException("Speechmatics rejected the live transcription request.", ProviderConnection.Text(root, "type") switch
                    {
                        "not_authorised" => PluginRequestFailureKind.Authentication,
                        "quota_exceeded" => PluginRequestFailureKind.RateLimit,
                        "job_error" => PluginRequestFailureKind.ServerError,
                        _ => PluginRequestFailureKind.InvalidRequest
                    });
                case "AddPartialTranscript":
                case "AddTranscript":
                    var metadata = ProviderConnection.Required(root, "metadata", JsonValueKind.Object);
                    var text = ProviderConnection.Text(metadata, "transcript") ?? throw Incomplete();
                    if (_preview.Length + text.Length > MaximumMessageBytes) throw Incomplete();
                    if (type == "AddTranscript")
                    {
                        AppendTranscript(_preview, text);
                        _pending = "";
                    }
                    else _pending = text;
                    var display = new StringBuilder(_preview.ToString());
                    AppendTranscript(display, _pending);
                    TranscriptReceived?.Invoke(new(display.ToString(), false));
                    break;
                case "EndOfTranscript":
                    if (!_finishing || !string.IsNullOrWhiteSpace(_pending)) throw Incomplete();
                    TranscriptReceived?.Invoke(new(_preview.ToString().Trim(), true) { DetectedLanguage = _language });
                    return;
            }
        }
    }

    private static void AppendTranscript(StringBuilder target, string fragment)
    {
        // Speechmatics can emit punctuation in its own fragment, preceded by a space.
        var trimmed = fragment.TrimStart();
        if (target.Length > 0 && trimmed.Length > 0 && ".,!?。！，？".Contains(trimmed[0]))
        {
            while (target.Length > 0 && char.IsWhiteSpace(target[target.Length - 1])) target.Length--;
            fragment = trimmed;
        }
        target.Append(fragment);
    }

    private static PluginRequestException Incomplete(string message = "Speechmatics returned an invalid or incomplete live response.") =>
        new(message, PluginRequestFailureKind.OutputIncomplete);

    private static PluginRequestException ProviderError(int code) => new("Speechmatics rejected the live transcription request.", code switch
    {
        401 => PluginRequestFailureKind.Authentication, 402 or 403 => PluginRequestFailureKind.Permission,
        408 => PluginRequestFailureKind.Timeout, 413 => PluginRequestFailureKind.RequestTooLarge,
        429 => PluginRequestFailureKind.RateLimit, >= 500 and <= 599 => PluginRequestFailureKind.ServerError,
        _ => PluginRequestFailureKind.InvalidRequest
    }, code is >= 400 and <= 599 ? code : null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _socket.Abort();
        try { await _receiver.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Send/finalize observe failures; disposal releases resources. */ }
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { _socket.Dispose(); _lifetime.Dispose(); }
        finally { _sendLock.Release(); }
    }
}
