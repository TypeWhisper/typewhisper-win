using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>Voxtral Realtime transport: PCM16 input, accumulated previews, one authoritative final transcript.</summary>
internal sealed class MistralStreamingSession : IStreamingSession
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _updated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receiver;
    private readonly TimeSpan _completionTimeout;
    private readonly StringBuilder _preview = new();
    private string? _language;
    private volatile bool _finishing;
    private bool _ready;
    private int _disposed;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal MistralStreamingSession(WebSocket socket, TimeSpan? completionTimeout = null)
    {
        _socket = socket;
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static async Task<IStreamingSession> ConnectAsync(string key, string model, TimeSpan completionTimeout, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        socket.Options.CollectHttpResponseDetails = true;
        using var http = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
        MistralStreamingSession? session = null;
        try
        {
            await socket.ConnectAsync(new Uri("wss://api.mistral.ai/v1/audio/transcriptions/realtime?model=" + Uri.EscapeDataString(model)), http, ct).ConfigureAwait(false);
            session = new(socket, completionTimeout);
            await session.InitializeAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch (WebSocketException)
        {
            var status = (int)socket.HttpStatusCode;
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket.Dispose();
            throw status >= 400 ? ProviderError(status) : new PluginRequestException("Mistral live connection failed.", PluginRequestFailureKind.Network);
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket.Dispose();
            throw;
        }
    }

    internal async Task InitializeAsync(CancellationToken ct)
    {
        await WaitForEventAsync(_created.Task, ct).ConfigureAwait(false);
        await SendJsonAsync(new
        {
            type = "session.update",
            session = new { audio_format = new { encoding = "pcm_s16le", sample_rate = 16000 }, target_streaming_delay_ms = 480 }
        }, ct).ConfigureAwait(false);
        await WaitForEventAsync(_updated.Task, ct).ConfigureAwait(false);
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
                await SendJsonAsync(new { type = "input_audio.append", audio = Convert.ToBase64String(pcm16Audio.Span[..count]) }, ct).ConfigureAwait(false);
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
            await SendJsonAsync(new { type = "input_audio.flush" }, ct).ConfigureAwait(false);
            await SendJsonAsync(new { type = "input_audio.end" }, ct).ConfigureAwait(false);
            await _receiver.WaitAsync(_completionTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new PluginRequestException("Mistral did not finish the live transcript in time.", PluginRequestFailureKind.Timeout); }
        finally { _sendLock.Release(); }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload).AsMemory(), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false); }
        catch (WebSocketException) { throw new PluginRequestException("Mistral live connection failed.", PluginRequestFailureKind.Network); }
    }

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (!_ready || _finishing || _receiver.IsCompleted || _socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0)
            throw Incomplete("Mistral live connection is not open for audio.");
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await ReceiveFramesAsync(ct).ConfigureAwait(false); }
        catch (WebSocketException) { throw new PluginRequestException("Mistral live connection was interrupted.", PluginRequestFailureKind.Network); }
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
            var type = ProviderConnection.RequiredText(root, "type");
            switch (type)
            {
                case "session.created": _created.TrySetResult(); break;
                case "session.updated": _updated.TrySetResult(); break;
                case "error":
                    var error = ProviderConnection.Required(root, "error", JsonValueKind.Object);
                    var code = error.TryGetProperty("code", out var rawCode) && rawCode.TryGetInt32(out var number) ? number : 0;
                    throw ProviderError(code);
                case "transcription.language":
                    _language = ProviderConnection.RequiredText(root, "audio_language");
                    break;
                case "transcription.text.delta":
                    var delta = ProviderConnection.Text(root, "text") ?? throw Incomplete();
                    if (_preview.Length + delta.Length > MaximumMessageBytes) throw Incomplete();
                    _preview.Append(delta);
                    TranscriptReceived?.Invoke(new(_preview.ToString(), false));
                    break;
                case "transcription.done":
                    if (!_finishing) throw Incomplete();
                    var final = ProviderConnection.Text(root, "text") ?? throw Incomplete();
                    if (string.IsNullOrWhiteSpace(final) && !string.IsNullOrWhiteSpace(_preview.ToString())) throw Incomplete();
                    TranscriptReceived?.Invoke(new(final, true) { DetectedLanguage = ProviderConnection.Text(root, "language") ?? _language });
                    return;
            }
        }
    }

    private static PluginRequestException Incomplete(string message = "Mistral returned an invalid or incomplete live response.") =>
        new(message, PluginRequestFailureKind.OutputIncomplete);

    private static PluginRequestException ProviderError(int code) => new("Mistral rejected the live transcription request.", code switch
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
