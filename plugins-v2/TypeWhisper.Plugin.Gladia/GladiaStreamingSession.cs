using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Gladia;

/// <summary>Streams PCM to Gladia and waits for the explicit end-of-session event.</summary>
internal sealed class GladiaStreamingSession : IStreamingSession
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeSpan _completionTimeout;
    private readonly Task _receiver;
    private readonly Dictionary<string, string> _finals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private int _transcriptCharacters;
    private volatile bool _finishing;
    private int _disposed;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal GladiaStreamingSession(WebSocket socket, TimeSpan? completionTimeout = null)
    {
        _socket = socket;
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static async Task<IStreamingSession> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new GladiaStreamingSession(socket);
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
            if (_finishing) throw new InvalidOperationException("Gladia stream already finalized.");
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
            if (_finishing) throw new InvalidOperationException("Gladia stream already finalized.");
            _finishing = true;
            await _socket.SendAsync("{\"type\":\"stop_recording\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            await _receiver.WaitAsync(_completionTimeout, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (_receiver.IsCompleted || _socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0)
            throw Incomplete("Gladia live connection is closed.");
    }

    private static PluginRequestException Incomplete(string message = "Gladia returned an invalid or incomplete live response.") =>
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
            var type = root.GetProperty("type").GetString() ?? throw Incomplete();
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw ProviderError(error);
            if (type == "error") throw ProviderError(root.TryGetProperty("data", out var details) ? details : root);
            if (root.TryGetProperty("acknowledged", out var acknowledged) && !acknowledged.GetBoolean()) throw Incomplete();
            if (type == "end_session")
            {
                if (!_finishing || _pending.Values.Any(text => !string.IsNullOrWhiteSpace(text))) throw Incomplete();
                return;
            }
            if (type != "transcript") continue;
            var data = root.GetProperty("data");
            var id = ProviderConnection.RequiredText(data, "id");
            var final = data.GetProperty("is_final").GetBoolean();
            var utterance = data.GetProperty("utterance");
            var text = utterance.GetProperty("text").GetString() ?? throw Incomplete();
            if (text.Length > MaximumMessageBytes || id.Length > 256) throw Incomplete();
            if (_finals.TryGetValue(id, out var confirmed))
            {
                if (final && confirmed != text) throw Incomplete();
                continue; // A repeated event must not insert the same utterance twice.
            }
            if (final)
            {
                _transcriptCharacters += text.Length;
                if (_transcriptCharacters > MaximumMessageBytes || _finals.Count >= 10000) throw Incomplete();
                _finals.Add(id, text);
                _pending.Remove(id);
                TranscriptReceived?.Invoke(new(text, true) { DetectedLanguage = ProviderConnection.Text(utterance, "language") });
            }
            else
            {
                // The host owns confirmed segments; only replace the unconfirmed preview.
                _pending[id] = text;
                if (_pending.Count > 100 || _pending.Values.Sum(value => value.Length) > MaximumMessageBytes) throw Incomplete();
                TranscriptReceived?.Invoke(new(string.Join(" ", _pending.Values), false));
            }
        }
    }

    private static PluginRequestException ProviderError(JsonElement error)
    {
        var status = 0;
        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("status_code", out var code))
        {
            if (code.ValueKind == JsonValueKind.Number) code.TryGetInt32(out status);
            else if (code.ValueKind == JsonValueKind.String) int.TryParse(code.GetString(), out status);
        }
        var kind = status switch
        {
            401 => PluginRequestFailureKind.Authentication, 402 or 403 => PluginRequestFailureKind.Permission,
            408 => PluginRequestFailureKind.Timeout, 413 => PluginRequestFailureKind.RequestTooLarge,
            429 => PluginRequestFailureKind.RateLimit, >= 500 => PluginRequestFailureKind.ServerError,
            _ => PluginRequestFailureKind.InvalidRequest
        };
        return new("Gladia rejected the live transcription request.", kind, status == 0 ? null : status);
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
