using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.ElevenLabs;

// Explicit commits keep each acknowledgement tied to audio already sent. Commit before
// the provider's automatic ~36-second boundary; do not count both forms of a final event.
internal sealed class ElevenLabsStreamingSession : IStreamingSession
{
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MemoryStream _buffer = new();
    private readonly Task _receiver;
    private TaskCompletionSource? _commit;
    private int _uncommittedBytes;
    private int _quietBytes;
    private bool _finishing;
    private int _disposed;
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal ElevenLabsStreamingSession(WebSocket socket)
    {
        _socket = socket;
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static Uri BuildRealtimeUri(string model, string? language, bool noVerbatim)
    {
        var query = "model_id=" + Uri.EscapeDataString(model) +
            "&audio_format=pcm_16000&commit_strategy=manual&include_timestamps=true&include_language_detection=true" +
            "&no_verbatim=" + (noVerbatim ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            query += "&language_code=" + Uri.EscapeDataString(language);
        return new("wss://api.elevenlabs.io/v1/speech-to-text/realtime?" + query);
    }

    public static async Task<ElevenLabsStreamingSession> ConnectAsync(string key, string model, string? language, bool noVerbatim, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", key);
        try
        {
            await socket.ConnectAsync(BuildRealtimeUri(model, language, noVerbatim), ct).ConfigureAwait(false);
            return new(socket);
        }
        catch { socket.Dispose(); throw; }
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
                var count = Math.Min(32000 - (int)_buffer.Length, pcm16Audio.Length);
                _buffer.Write(pcm16Audio.Span[..count]);
                pcm16Audio = pcm16Audio[count..];
                if (_buffer.Length >= 3200)
                {
                    var chunk = _buffer.ToArray();
                    _buffer.SetLength(0);
                    _uncommittedBytes += chunk.Length;
                    foreach (var sample in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(chunk))
                        _quietBytes = Math.Abs((int)sample) <= 250 ? Math.Min(6400, _quietBytes + 2) : 0;
                    // Do not cut words at a fixed deadline. Wait for 200 ms of near-silence
                    // after 20 seconds; fall back before the provider auto-commits at ~36 s.
                    var commit = _uncommittedBytes >= 640000 && _quietBytes >= 6400;
                    if (_uncommittedBytes >= 1024000 && !commit)
                        throw new IOException("ElevenLabs live transcription requires a pause. Retrying the complete recording.");
                    await SendChunkAsync(chunk, commit, ct).ConfigureAwait(false);
                }
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
            if (_finishing) throw new InvalidOperationException("Stream already finalized.");
            _finishing = true;
            var tail = _buffer.ToArray();
            _buffer.SetLength(0);
            if (_uncommittedBytes + tail.Length > 0)
                await SendChunkAsync(tail, true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task EnsureOpenAsync()
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (_socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0)
            throw new IOException("ElevenLabs live connection is closed.");
    }

    private async Task SendChunkAsync(byte[] audio, bool commit, CancellationToken ct)
    {
        await EnsureOpenAsync().ConfigureAwait(false);
        var acknowledgement = commit ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (acknowledgement is not null) Volatile.Write(ref _commit, acknowledgement);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            message_type = "input_audio_chunk", audio_base_64 = Convert.ToBase64String(audio), sample_rate = 16000, commit
        });
        await _socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        if (acknowledgement is not null)
        {
            // A closed/erroring receiver must fail promptly instead of waiting for a final
            // result that will never arrive. The host retries with the complete recording.
            var winner = await Task.WhenAny(acknowledgement.Task, _receiver).WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
            await acknowledgement.Task.ConfigureAwait(false);
            _uncommittedBytes = 0;
            _quietBytes = 0;
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await ReceiveFramesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new IOException("Unexpected ElevenLabs live response."); }
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
                if (frame.MessageType == WebSocketMessageType.Close)
                    throw new IOException("ElevenLabs live connection ended before completion.");
                if (message.Length + frame.Count > 1024 * 1024) throw new InvalidDataException("ElevenLabs live response is too large.");
                message.Write(bytes, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Unexpected ElevenLabs live response.");
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = doc.RootElement;
            var type = root.GetProperty("message_type").GetString() ?? "";
            if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
                throw new IOException("ElevenLabs rejected the live transcription request.");
            if (type is not ("partial_transcript" or "committed_transcript_with_timestamps")) continue;
            var final = type == "committed_transcript_with_timestamps";
            var acknowledgement = final ? Interlocked.Exchange(ref _commit, null) : null;
            if (final && acknowledgement is null)
                throw new IOException("ElevenLabs returned an unexpected transcript commit.");
            var text = root.GetProperty("text").GetString() ?? "";
            var language = root.TryGetProperty("language_code", out var value) ? value.GetString() : null;
            TranscriptReceived?.Invoke(new(text, final) { DetectedLanguage = language });
            acknowledgement?.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
