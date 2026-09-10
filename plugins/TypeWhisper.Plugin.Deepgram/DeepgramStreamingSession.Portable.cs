using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Deepgram;

internal sealed class DeepgramStreamingSession : IStreamingSession
{
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receiver;
    private readonly HashSet<double> _finalSegments = [];
    private int _finishing;
    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal DeepgramStreamingSession(WebSocket socket)
    {
        _socket = socket;
        _receiver = ReceiveAsync(_lifetime.Token);
    }

    internal static Uri BuildUri(string model, string? language) => new(
        "wss://api.deepgram.com/v1/listen?encoding=linear16&sample_rate=16000&channels=1" +
        "&interim_results=true&punctuate=true&smart_format=true&model=" + Uri.EscapeDataString(model) +
        "&language=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(language) || language == "auto" ? "multi" : language));

    public static async Task<DeepgramStreamingSession> ConnectAsync(string key, string model, string? language, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Token " + key);
        try
        {
            await socket.ConnectAsync(BuildUri(model, language), ct).ConfigureAwait(false);
            return new(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (_socket.State != WebSocketState.Open || Volatile.Read(ref _finishing) != 0)
            throw new IOException("Deepgram live connection is closed.");
        await _socket.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_receiver.IsCompleted) await _receiver.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _finishing, 1) != 0) throw new InvalidOperationException("Stream already finished.");
        await _socket.SendAsync("{\"type\":\"CloseStream\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        // CloseStream flushes the final Results before the server closes the WebSocket.
        await _receiver.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult frame;
            do
            {
                frame = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    if (Volatile.Read(ref _finishing) == 0 || frame.CloseStatus != WebSocketCloseStatus.NormalClosure)
                        throw new IOException("Deepgram live connection ended before completion.");
                    return;
                }
                if (message.Length + frame.Count > 1024 * 1024) throw new InvalidDataException("Deepgram live response is too large.");
                message.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Unexpected Deepgram live response.");
            using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "Error") throw new IOException("Deepgram rejected the live transcription request.");
            if (type != "Results") continue;
            var final = root.GetProperty("is_final").GetBoolean();
            var start = root.GetProperty("start").GetDouble();
            if (_finalSegments.Contains(start)) continue;
            var text = root.GetProperty("channel").GetProperty("alternatives")[0].GetProperty("transcript").GetString() ?? "";
            if (final && !string.IsNullOrWhiteSpace(text)) _finalSegments.Add(start);
            var alternative = root.GetProperty("channel").GetProperty("alternatives")[0];
            var language = alternative.TryGetProperty("languages", out var languages) && languages.GetArrayLength() == 1
                ? languages[0].GetString() : null;
            TranscriptReceived?.Invoke(new(text, final) { DetectedLanguage = language });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _socket.Abort();
        try { await _receiver.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
