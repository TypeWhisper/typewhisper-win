using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public sealed class CompatibleStreamingTests
{
    [Fact]
    public async Task RealtimeSessionWaitsForItsCommittedFinalTranscript()
    {
        var socket = new ScriptedSocket();
        await using var session = new CompatibleRealtimeStreamingSession(socket, new());
        var updates = new List<string>(); session.TranscriptReceived += e => updates.Add(e.Text);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var start = session.StartAsync("custom-deployment", ["de", "en"], "fixture", timeout.Token);
        Assert.False(start.IsCompleted);
        socket.Push("""{"type":"session.updated"}"""); await start;
        await session.SendAudioAsync(new byte[320], timeout.Token);
        using var audio = JsonDocument.Parse(socket.Sent.Single(s => s.Contains("input_audio_buffer.append")));
        Assert.Equal(480, Convert.FromBase64String(audio.RootElement.GetProperty("audio").GetString()!).Length);
        var finish = session.FinalizeAsync(timeout.Token);
        socket.Push("""{"type":"input_audio_buffer.committed","item_id":"final"}""");
        socket.Push("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"final","delta":"Guten"}""");
        socket.Push("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"final","transcript":"Guten Tag"}""");
        await finish;
        Assert.Equal("Guten Tag", updates.Last());
        Assert.Single(socket.Sent, s => s.Contains("input_audio_buffer.commit"));
    }

    [Fact]
    public async Task RealtimeFailureCannotReturnSuccessfulPartialText()
    {
        var socket = new ScriptedSocket();
        await using var session = new CompatibleRealtimeStreamingSession(socket, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        socket.Push("""{"type":"session.updated"}"""); await session.StartAsync("custom", [], null, timeout.Token);
        var finish = session.FinalizeAsync(timeout.Token);
        socket.Push("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"a","delta":"partial"}""");
        socket.Push("""{"type":"error","error":{"message":"fixture failure"}}""");
        await Assert.ThrowsAsync<IOException>(() => finish);
    }

    [Fact]
    public async Task UnacknowledgedRealtimeSessionHonorsCallerCancellation()
    {
        var socket = new ScriptedSocket();
        await using var session = new CompatibleRealtimeStreamingSession(socket, new());
        using var cancelled = new CancellationTokenSource();
        var start = session.StartAsync("custom", [], null, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    [Fact]
    public void MissingItemIdsAccumulateIntoOneTranscript()
    {
        var collector = new CompatibleRealtimeTranscriptCollector();
        collector.ApplyEvent("""{"type":"conversation.item.input_audio_transcription.delta","delta":"Hello "}""", out _);
        collector.ApplyEvent("""{"type":"conversation.item.input_audio_transcription.delta","delta":"world"}""", out _);
        Assert.Equal("Hello world", collector.CurrentText);
        collector.ApplyEvent("""{"type":"conversation.item.input_audio_transcription.completed","transcript":"Hello world."}""", out _);
        Assert.Equal("Hello world.", collector.CurrentText);
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        public List<string> Sent { get; } = [];
        private WebSocketState _state = WebSocketState.Open;
        public void Push(string json) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => CloseAsync(closeStatus, statusDescription, ct);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var message = await _incoming.Reader.ReadAsync(ct);
            message.CopyTo(buffer.AsSpan());
            return new(message.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Sent.Add(Encoding.UTF8.GetString(buffer)); return Task.CompletedTask; }
    }
}
