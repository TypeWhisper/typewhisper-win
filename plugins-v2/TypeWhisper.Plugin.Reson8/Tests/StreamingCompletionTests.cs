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
