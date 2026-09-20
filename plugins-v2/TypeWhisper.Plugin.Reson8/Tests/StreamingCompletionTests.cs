using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;
namespace PortableMigration.Tests;
public sealed class StreamingCompletionTests
{
    [Fact]
    public async Task MissingFlushConfirmationTimesOutWithoutCallerDeadline()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var flush = await Receive(socket, default); Assert.Contains("flush_request", Encoding.UTF8.GetString(flush));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        await using var session = await Reson8StreamingSession.ConnectAsync("fixture", "https://api.reson8.dev", "Authorization",
            null, null, default, new Uri($"ws://127.0.0.1:{port}/"), finalizationTimeout: TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<TimeoutException>(() => session.FinalizeAsync(default));
        finished.TrySetResult(); await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectionHandshakeHasABoundedTimeout()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var request = listener.GetContextAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => Reson8StreamingSession.ConnectAsync("fixture", "https://api.reson8.dev",
            "Authorization", null, null, default, new Uri($"ws://127.0.0.1:{port}/"), TimeSpan.FromMilliseconds(250)));
        var context = await request.WaitAsync(TimeSpan.FromSeconds(5)); context.Response.Close();
    }

    [Fact]
    public async Task ReceiveFailurePreventsSubsequentAudioSends()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await Receive(socket, ct);
            await Send(socket, """{"type":"error","message":"Fixture failure"}""", ct);
            await finished.Task.WaitAsync(ct);
        }, ct);
        await using var session = await Reson8StreamingSession.ConnectAsync("fixture", "https://api.reson8.dev",
            "Authorization", null, null, ct, new Uri($"ws://127.0.0.1:{port}/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FinalizeAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAudioAsync(new byte[] { 0, 0 }, ct));
        finished.TrySetResult(); await server.WaitAsync(ct);
    }

    [Fact]
    public async Task SubscriberFailureFaultsFinalizationImmediately()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await Receive(socket, ct);
            await Send(socket, """{"type":"transcript","text":"Hello","is_final":true}""", ct);
            await finished.Task.WaitAsync(ct);
        }, ct);
        await using var session = await Reson8StreamingSession.ConnectAsync("fixture", "https://api.reson8.dev",
            "Authorization", null, "en", ct, new Uri($"ws://127.0.0.1:{port}/"));
        session.TranscriptReceived += _ => throw new ApplicationException("Callback failed");
        await session.SendAudioAsync(new byte[] { 0, 0 }, ct);
        await Assert.ThrowsAsync<ApplicationException>(() => session.FinalizeAsync(ct));
        finished.TrySetResult(); await server.WaitAsync(ct);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Loopback_AwaitsTerminalResponseAndRejectsPrematureClose(bool prematureClose)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));var ct=timeout.Token;
        var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();var port=((IPEndPoint)tcp.LocalEndpoint).Port;tcp.Stop();
        using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        var uri=new Uri($"ws://127.0.0.1:{port}/");
        var endReceived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=Task.Run(async()=>
        {
            var context=await listener.GetContextAsync().WaitAsync(ct);using var socket=(await context.AcceptWebSocketAsync(null)).WebSocket;
            Assert.Equal("ApiKey fixture",context.Request.Headers["Authorization"]);
            var audio=await Receive(socket,ct);Assert.Equal(4,audio.Length);
            await Send(socket,"""{"type":"transcript","text":"Hallo","is_final":true}""",ct);
            var end=await Receive(socket,ct);Assert.Contains("flush_request",Encoding.UTF8.GetString(end));endReceived.TrySetResult();
            await release.Task.WaitAsync(ct);
            if(!prematureClose) { await Send(socket,"""{"type":"transcript","text":"Welt","is_final":true}""",ct); await Send(socket,"""{"type":"flush_confirmation"}""",ct); }
            if(prematureClose) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,null,ct);
            await clientFinished.Task.WaitAsync(ct);
        },ct);
        await using var session=await Reson8StreamingSession.ConnectAsync("fixture","https://api.reson8.dev","Authorization",null,"de",ct,uri);var events=new ConcurrentQueue<StreamingTranscriptEvent>();session.TranscriptReceived+=events.Enqueue;
        await session.SendAudioAsync(new byte[]{1,2,3,4},ct);
        var finish=session.FinalizeAsync(ct);await endReceived.Task.WaitAsync(ct);Assert.False(finish.IsCompleted);release.TrySetResult();
        if(prematureClose) await Assert.ThrowsAnyAsync<WebSocketException>(()=>finish);
        else {await finish;Assert.Equal("Hallo Welt",string.Join(" ",events.Where(e=>e.IsFinal).Select(e=>e.Text)));}
        clientFinished.TrySetResult(); await server.WaitAsync(ct);
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
