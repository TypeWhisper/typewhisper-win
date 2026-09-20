using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Soniox;

/// <summary>Streams PCM to Soniox and confirms complete utterances before publishing final segments.</summary>
internal sealed class SonioxStreamingSession : IStreamingSession
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeSpan _completionTimeout;
    private readonly Task _receiver;
    private readonly StringBuilder _confirmed = new();
    private readonly HashSet<string> _languages = new(StringComparer.OrdinalIgnoreCase);
    private string _interim = "";
    private volatile bool _finishing;
    private int _disposed;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal SonioxStreamingSession(WebSocket socket, TimeSpan? completionTimeout = null)
    {
        _socket = socket;
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static Uri Endpoint(string region) => new(region switch
    {
        "us" => "wss://stt-rt.soniox.com/transcribe-websocket",
        "eu" => "wss://stt-rt.eu.soniox.com/transcribe-websocket",
        "jp" => "wss://stt-rt.jp.soniox.com/transcribe-websocket",
        _ => throw new ArgumentException("Unknown Soniox region.", nameof(region))
    });

    internal static byte[] Configuration(string key, IReadOnlyList<string> languages) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        api_key = key, model = "stt-rt-v5", audio_format = "pcm_s16le", sample_rate = 16000, num_channels = 1,
        language_hints = languages, enable_language_identification = true, enable_endpoint_detection = true
    });

    internal static async Task<IStreamingSession> ConnectAsync(string key, string region, IReadOnlyList<string> languages, CancellationToken ct)
    {
        var uri = Endpoint(region);
        var socket = new ClientWebSocket();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            await socket.SendAsync(Configuration(key, languages).AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            return new SonioxStreamingSession(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (pcm16Audio.Length % 2 != 0) throw new ArgumentException("PCM16 requires complete samples.", nameof(pcm16Audio));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
            if (_finishing) throw new InvalidOperationException("Soniox stream already finalized.");
            // An empty audio frame terminates Soniox's stream; ignore empty host chunks.
            while (!pcm16Audio.IsEmpty)
            {
                var count = Math.Min(32000, pcm16Audio.Length);
                await _socket.SendAsync(pcm16Audio[..count], WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
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
            if (_finishing) throw new InvalidOperationException("Soniox stream already finalized.");
            _finishing = true;
            // Use the empty text message from Soniox's reference clients as end-of-audio.
            await _socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            await _receiver.WaitAsync(_completionTimeout, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (_receiver.IsCompleted || _socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0)
            throw Incomplete("Soniox live connection is closed.");
    }

    private static PluginRequestException Incomplete(string message = "Soniox returned an invalid or incomplete live response.") =>
        new(message, PluginRequestFailureKind.OutputIncomplete);

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await ReceiveFramesAsync(ct).ConfigureAwait(false); }
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
                if (frame.MessageType != WebSocketMessageType.Text) throw Incomplete();
                if (message.Length + frame.Count > MaximumMessageBytes) throw Incomplete();
                message.Write(bytes, 0, frame.Count);
            } while (!frame.EndOfMessage);
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = doc.RootElement;
            if (root.TryGetProperty("error_code", out var error))
            {
                var status = error.GetInt32();
                var kind = status switch
                {
                    401 => PluginRequestFailureKind.Authentication, 402 or 403 => PluginRequestFailureKind.Permission,
                    408 => PluginRequestFailureKind.Timeout, 413 => PluginRequestFailureKind.RequestTooLarge,
                    429 => PluginRequestFailureKind.RateLimit, >= 500 => PluginRequestFailureKind.ServerError,
                    _ => PluginRequestFailureKind.InvalidRequest
                };
                // Never include provider payloads that might echo credentials or transcript text.
                throw new PluginRequestException("Soniox rejected the live transcription request.", kind, status);
            }
            if (root.TryGetProperty("error_message", out _) || root.TryGetProperty("error_type", out _)) throw Incomplete();
            var finished = root.TryGetProperty("finished", out var done) && done.GetBoolean();
            var tokens = root.GetProperty("tokens");
            if (tokens.ValueKind != JsonValueKind.Array) throw Incomplete();
            var previousInterim = _interim;
            var interim = new StringBuilder();
            var sawFinal = false;
            var endpoint = false;
            foreach (var token in tokens.EnumerateArray())
            {
                var text = token.GetProperty("text").GetString() ?? throw Incomplete();
                var final = token.GetProperty("is_final").GetBoolean();
                if (text is "<end>" or "<fin>" or "<eos>")
                {
                    if (interim.Length > 0) throw Incomplete();
                    if (final) { PublishFinal(); endpoint = true; }
                    continue;
                }
                if (final)
                {
                    if (interim.Length > 0) throw Incomplete();
                    _confirmed.Append(text); sawFinal = true;
                    if (token.TryGetProperty("language", out var language) && language.ValueKind != JsonValueKind.Null
                        && !string.IsNullOrWhiteSpace(language.GetString())) _languages.Add(language.GetString()!);
                }
                else interim.Append(text);
                if (_confirmed.Length + interim.Length > MaximumMessageBytes) throw Incomplete();
            }
            _interim = interim.ToString();
            if (finished)
            {
                if (!_finishing || !string.IsNullOrWhiteSpace(_interim)
                    || tokens.GetArrayLength() == 0 && !string.IsNullOrWhiteSpace(previousInterim)) throw Incomplete();
                PublishFinal();
                return;
            }
            if (sawFinal || interim.Length > 0 || endpoint || previousInterim.Length > 0)
                TranscriptReceived?.Invoke(new(_confirmed.ToString() + _interim, false));
        }
    }

    private void PublishFinal()
    {
        var text = _confirmed.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            TranscriptReceived?.Invoke(new(text, true) { DetectedLanguage = _languages.Count == 1 ? _languages.Single() : null });
        _confirmed.Clear(); _languages.Clear(); _interim = "";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _socket.Abort();
        try { await _receiver.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Failure is observed by send/finalize; disposal only releases resources. */ }
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { _socket.Dispose(); _lifetime.Dispose(); }
        finally { _sendLock.Release(); }
    }
}
