using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using TypeWhisper.Plugin.Meta;
using TypeWhisper.PluginSDK;
namespace PortableMigration.Tests;
public sealed class StreamingCompletionTests
{
    [Theory]
    [InlineData(false, false)][InlineData(true, false)]
    [InlineData(false, true)][InlineData(true, true)]
    public async Task Loopback_AwaitsTerminalResponseAndRejectsPrematureClose(bool prematureClose, bool abruptClose)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));var ct=timeout.Token;
        using var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();var port=((IPEndPoint)tcp.LocalEndpoint).Port;tcp.Stop();
        using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        var uri=new Uri($"ws://127.0.0.1:{port}/");
        var endReceived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=Task.Run(async()=>
        {
            var context=await listener.GetContextAsync().WaitAsync(ct);using var socket=(await context.AcceptWebSocketAsync(null)).WebSocket;
            await Receive(socket,ct); await Send(socket,"""{"sessionId":"fixture"}""",ct);
            var audio=await Receive(socket,ct);Assert.Equal(4,audio.Length);
            await Send(socket,"""{"type":"transcript","transcript":"Hallo","final":false}""",ct);
            var end=await Receive(socket,ct);Assert.Contains("endStream",Encoding.UTF8.GetString(end));endReceived.TrySetResult();
            await release.Task.WaitAsync(ct);
            if(!prematureClose) { await Send(socket,"""{"type":"transcript","transcript":"Hallo Welt","final":true}""",ct);  }
            if (abruptClose)
            {
                // The explicit final response must complete the client even when no close frame follows.
                if (!prematureClose) await clientFinished.Task.WaitAsync(ct);
                socket.Abort();
                // HttpListener's managed transport on Linux releases the TCP connection
                // when the listener is closed, rather than on WebSocket.Abort alone.
                if (prematureClose) listener.Close();
            }
            else await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,null,ct);
            await clientFinished.Task.WaitAsync(ct);
        },ct);
        await using var session=await MetaRealtimeStreamingSession.ConnectAsync("fixture","model","PUSH_TO_TALK",[],[],ct,uri);var events=new ConcurrentQueue<StreamingTranscriptEvent>();session.TranscriptReceived+=events.Enqueue;
        await session.SendAudioAsync(new byte[]{1,2,3,4},ct);
        var finish=session.FinalizeAsync(ct);await endReceived.Task.WaitAsync(ct);Assert.False(finish.IsCompleted);release.TrySetResult();
        if(prematureClose) await Assert.ThrowsAnyAsync<WebSocketException>(()=>finish);
        else {await finish;Assert.Equal("Hallo Welt",string.Join(" ",events.Where(e=>e.IsFinal).Select(e=>e.Text)));}
        clientFinished.TrySetResult(); await server.WaitAsync(ct);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Diarization_DrainsAllTurnsBeforeCleanClosure(bool abnormalClose, bool missingLastTurn)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var firstObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await Receive(socket, ct); await Send(socket, """{"sessionId":"fixture"}""", ct);
            await Receive(socket, ct);
            await Send(socket, """{"type":"speechStart","turnId":1}""", ct);
            await Send(socket, """{"type":"speaker","label":"A"}""", ct);
            await Send(socket, """{"type":"speechStart","turnId":2}""", ct);
            await Send(socket, """{"type":"speaker","label":"B"}""", ct);
            Assert.Contains("endStream", Encoding.UTF8.GetString(await Receive(socket, ct)));
            await Send(socket, """{"type":"speechComplete","turnId":1,"transcript":"First sentence."}""", ct);
            await releaseLast.Task.WaitAsync(ct);
            if (!missingLastTurn)
                await Send(socket, """{"type":"speechComplete","turnId":2,"transcript":"Last sentence."}""", ct);
            await socket.CloseOutputAsync(abnormalClose ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure, null, ct);
            await clientFinished.Task.WaitAsync(ct);
        }, ct);
        await using var session = await MetaRealtimeStreamingSession.ConnectAsync("fixture", "model", "DIARIZATION", [], [], ct, new Uri($"ws://127.0.0.1:{port}/"));
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        session.TranscriptReceived += e => { events.Enqueue(e); if (e.Text.Contains("First sentence.")) firstObserved.TrySetResult(); };
        await session.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, ct);
        var finish = session.FinalizeAsync(ct);
        try
        {
            await firstObserved.Task.WaitAsync(ct);
            await Task.WhenAny(finish, Task.Delay(100, ct));
            Assert.False(finish.IsCompleted);
            releaseLast.TrySetResult();
            if (abnormalClose || missingLastTurn)
            {
                await Assert.ThrowsAnyAsync<WebSocketException>(() => finish);
                Assert.DoesNotContain(events, e => e.IsFinal);
            }
            else
            {
                await finish;
                var final = Assert.Single(events, e => e.IsFinal);
                Assert.Contains("First sentence.", final.Text);
                Assert.Contains("Last sentence.", final.Text);
            }
        }
        finally { releaseLast.TrySetResult(); clientFinished.TrySetResult(); }
        await server.WaitAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentDiarizationCompletesAndPendingReceiveDisposes(bool disposeWithoutFinalizing)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await Receive(socket, ct); await Send(socket, """{"sessionId":"fixture"}""", ct);
            if (!disposeWithoutFinalizing)
            {
                Assert.Contains("endStream", Encoding.UTF8.GetString(await Receive(socket, ct)));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }
            await done.Task.WaitAsync(ct);
        }, ct);
        await using var session = await MetaRealtimeStreamingSession.ConnectAsync("fixture", "model", "DIARIZATION", [], [], ct, new Uri($"ws://127.0.0.1:{port}/"));
        var events = new ConcurrentQueue<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Enqueue;
        try
        {
            if (disposeWithoutFinalizing) await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            else { await session.FinalizeAsync(ct); Assert.Equal("", Assert.Single(events, e => e.IsFinal).Text); }
        }
        finally { done.TrySetResult(); }
        await server.WaitAsync(ct);
    }

    private static async Task<byte[]> Receive(WebSocket socket,CancellationToken ct)
    {
        using var message=new MemoryStream();var buffer=new byte[4096];WebSocketReceiveResult frame;
        do { frame=await socket.ReceiveAsync(buffer,ct);message.Write(buffer,0,frame.Count); }while(!frame.EndOfMessage);
        return message.ToArray();
    }
    private static async Task Send(WebSocket socket,string value,CancellationToken ct)
    {
        var bytes=Encoding.UTF8.GetBytes(value);var half=bytes.Length/2;
        await socket.SendAsync(bytes.AsMemory(0,half),WebSocketMessageType.Text,false,ct);
        await socket.SendAsync(bytes.AsMemory(half),WebSocketMessageType.Text,true,ct);
    }
}
