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
            using var pcm = JsonDocument.Parse(await Receive(socket, ct));
            Assert.Equal("AQIDBA==", pcm.RootElement.GetProperty("realtimeInput").GetProperty("audio").GetProperty("data").GetString());
            var transcript = Encoding.UTF8.GetBytes("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Hallo Welt\"}}}");
            await socket.SendAsync(transcript.AsMemory(0, 12), WebSocketMessageType.Binary, false, ct);
            await socket.SendAsync(transcript.AsMemory(12), WebSocketMessageType.Binary, true, ct);
            using var end = JsonDocument.Parse(await Receive(socket, ct));
            Assert.True(end.RootElement.GetProperty("realtimeInput").GetProperty("audioStreamEnd").GetBoolean());
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        }, ct);
        await using var stream = await GeminiStreamingSession.ConnectAsync("fixture", GeminiPlugin.DefaultLiveTranscriptionModel,
            ["de-DE"], ["TypeWhisper"], GeminiTranscriptionMode.Smart, ct, new Uri($"ws://127.0.0.1:{port}/"));
        var completion = new TaskCompletionSource<StreamingTranscriptEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.TranscriptReceived += update => completion.TrySetResult(update);
        await stream.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, ct);
        var result = await completion.Task.WaitAsync(ct); Assert.Equal("Hallo Welt", result.Text); Assert.True(result.IsFinal);
        await stream.FinalizeAsync(ct); await server.WaitAsync(ct);
    }

    private static async Task<string> Receive(WebSocket socket, CancellationToken ct)
    {
        using var message = new MemoryStream(); var buffer = new byte[4096]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, ct); message.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }
}
