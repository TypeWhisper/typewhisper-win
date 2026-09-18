using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class GeminiPluginTests
{
    [Theory]
    [InlineData("final")]
    [InlineData("early-final")]
    [InlineData("empty")]
    [InlineData("close")]
    [InlineData("error")]
    [InlineData("malformed")]
    [InlineData("cancel")]
    public async Task StreamingCompletion_WaitsForConfirmationAndRejectsInterruptedResults(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var reservation = new TcpListener(IPAddress.Loopback,0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var endReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlyFinalReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await Receive(socket,ct);
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"setupComplete":{}}"""),WebSocketMessageType.Text,true,ct);
            Assert.Contains("activityStart",await Receive(socket,ct));
            Assert.Contains("audio",await Receive(socket,ct));
            if (outcome == "early-final")
                await socket.SendAsync(Encoding.UTF8.GetBytes("""{"serverContent":{"inputTranscription":{"text":"Earlier segment"}}}"""), WebSocketMessageType.Text, true, ct);
            Assert.Contains("activityEnd",await Receive(socket,ct));
            endReceived.SetResult(); await release.Task.WaitAsync(ct);
            if(outcome == "close") await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,null,ct);
            else if(outcome != "cancel")
            {
                var response = outcome switch
                {
                    "final" or "early-final" => """{"serverContent":{"inputTranscription":{"text":"Hallo Welt"}}}""",
                    "empty" => """{"serverContent":{"inputTranscription":{"text":""}}}""",
                    "error" => """{"error":{"message":"Fixture provider error"}}""",
                    _ => "not json"
                };
                await socket.SendAsync(Encoding.UTF8.GetBytes(response),WebSocketMessageType.Text,true,ct);
            }
            await finished.Task.WaitAsync(ct);
            }
            catch (Exception ex)
            {
                endReceived.TrySetException(ex);
                earlyFinalReceived.TrySetException(ex);
                throw;
            }
        },ct);
        await using var stream = await GeminiStreamingSession.ConnectAsync("fixture",GeminiPlugin.DefaultLiveTranscriptionModel,
            ["de-DE"],[],GeminiTranscriptionMode.Smart,ct,new Uri($"ws://127.0.0.1:{port}/"));
        var updates = new System.Collections.Concurrent.ConcurrentQueue<StreamingTranscriptEvent>();
        stream.TranscriptReceived += update => { updates.Enqueue(update); if (update.Text == "Earlier segment") earlyFinalReceived.TrySetResult(); };
        await stream.SendAudioAsync(new byte[] { 1,2,3,4 },ct);
        if (outcome == "early-final") await earlyFinalReceived.Task.WaitAsync(ct);
        using var finishCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completion = stream.FinalizeAsync(finishCancellation.Token);
        await endReceived.Task.WaitAsync(ct); Assert.False(completion.IsCompleted);
        release.SetResult();
        try
        {
            if(outcome == "cancel") { finishCancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>completion); }
            else if(outcome == "close") await Assert.ThrowsAsync<IOException>(()=>completion);
            else if(outcome == "error") await Assert.ThrowsAsync<InvalidOperationException>(()=>completion);
            else if(outcome == "malformed") await Assert.ThrowsAnyAsync<JsonException>(()=>completion);
            else
            {
                await completion;
                Assert.Equal(outcome == "early-final" ? "Earlier segment Hallo Welt" : outcome == "final" ? "Hallo Welt" : "",string.Join(" ",updates.Where(e=>e.IsFinal).Select(e=>e.Text)));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>stream.SendAudioAsync(new byte[] { 0,0 },ct));
            }
        }
        finally { finished.TrySetResult(); }
        await server.WaitAsync(ct);
    }

    [Fact]
    public async Task LiveWebSocket_ExchangesSetupPcmTranscriptAndEndOfAudio()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            using var setup = JsonDocument.Parse(await Receive(socket, ct));
            Assert.Equal("TypeWhisper", setup.RootElement.GetProperty("setup").GetProperty("inputAudioTranscription").GetProperty("customVocabulary")[0].GetString());
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"setupComplete\":{}}"), WebSocketMessageType.Text, true, ct);
            Assert.True(setup.RootElement.GetProperty("setup").GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection").GetProperty("disabled").GetBoolean());
            Assert.Contains("activityStart",await Receive(socket,ct));
            using var pcm = JsonDocument.Parse(await Receive(socket, ct));
            Assert.Equal("AQIDBA==", pcm.RootElement.GetProperty("realtimeInput").GetProperty("audio").GetProperty("data").GetString());
            using var end = JsonDocument.Parse(await Receive(socket, ct));
            Assert.True(end.RootElement.GetProperty("realtimeInput").TryGetProperty("activityEnd",out _));
            var transcript = Encoding.UTF8.GetBytes("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Hallo Welt\"}}}");
            await socket.SendAsync(transcript.AsMemory(0, 12), WebSocketMessageType.Binary, false, ct);
            await socket.SendAsync(transcript.AsMemory(12), WebSocketMessageType.Binary, true, ct);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        }, ct);
        await using var stream = await GeminiStreamingSession.ConnectAsync("fixture", GeminiPlugin.DefaultLiveTranscriptionModel,
            ["de-DE"], ["TypeWhisper"], GeminiTranscriptionMode.Smart, ct, new Uri($"ws://127.0.0.1:{port}/"));
        var completion = new TaskCompletionSource<StreamingTranscriptEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.TranscriptReceived += update => completion.TrySetResult(update);
        await stream.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, ct);
        await stream.FinalizeAsync(ct);
        var result = await completion.Task.WaitAsync(ct); Assert.Equal("Hallo Welt", result.Text); Assert.True(result.IsFinal);
        await server.WaitAsync(ct);
    }

    private static async Task<string> Receive(WebSocket socket, CancellationToken ct)
    {
        using var message = new MemoryStream(); var buffer = new byte[4096]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, ct); message.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }
}
