using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
/// Plays scripted realtime server events and records what the plugin sends.
/// "close" ends the connection with a close frame.
/// </summary>
internal sealed class SpeechSocketFixture : WebSocket
{
    private readonly Queue<string> _events;
    private WebSocketState _state = WebSocketState.Open;

    public SpeechSocketFixture(params string[] events) => _events = new(events);

    public List<string> Sent { get; } = [];
    public bool Disposed { get; private set; }

    public static string Audio(byte[] pcm) =>
        JsonSerializer.Serialize(new { type = "response.output_audio.delta", delta = Convert.ToBase64String(pcm) });

    public const string Done = """{"type":"response.done","response":{"status":"completed"}}""";

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() { Disposed = true; _state = WebSocketState.Closed; }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { _state = WebSocketState.Closed; return Task.CompletedTask; }
    public bool HangOnClose { get; init; }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (HangOnClose) await Task.Delay(Timeout.Infinite, cancellationToken);
        _state = WebSocketState.CloseSent;
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Sent.Add(Encoding.UTF8.GetString(buffer));
        return Task.CompletedTask;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pending is null)
        {
            if (!_events.TryDequeue(out var message))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }
            if (message == "close")
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            _pending = Encoding.UTF8.GetBytes(message);
            _offset = 0;
        }

        // Long events arrive in several frames, like a real socket with a small receive buffer.
        var count = Math.Min(_pending.Length - _offset, buffer.Count);
        _pending.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
        _offset += count;
        var end = _offset == _pending.Length;
        if (end) _pending = null;
        return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
    }

    private byte[]? _pending;
    private int _offset;
}
