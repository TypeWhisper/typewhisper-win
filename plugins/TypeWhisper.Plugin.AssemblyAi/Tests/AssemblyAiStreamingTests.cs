using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK;

public sealed class AssemblyAiStreamingTests
{
    [Theory]
    [InlineData("universal-3-5-pro", "de", "universal-3-5-pro")]
    [InlineData("universal-2", "en", "universal-streaming-english")]
    [InlineData("universal-2", "de", "universal-streaming-multilingual")]
    [InlineData("universal-2", null, "universal-streaming-multilingual")]
    [InlineData("universal-2", "auto", "universal-streaming-multilingual")]
    public void ConnectionPreservesModelLanguageAndDictionary(string model, string? language, string streamingModel)
    {
        var uri = AssemblyAiStreamingSession.BuildUri(AssemblyAiModels.Require(model), language, "Grüße, ACME & Sons");
        Assert.Equal("wss", uri.Scheme); Assert.Equal("streaming.assemblyai.com", uri.Host);
        Assert.Contains("speech_model=" + streamingModel, uri.Query); Assert.DoesNotContain("api-key", uri.Query);
        Assert.Contains("keyterms_prompt=", uri.Query); Assert.Contains("ACME%20", uri.Query);
        if (model == AssemblyAiModels.DefaultId) { Assert.Contains("language_codes=", uri.Query); Assert.DoesNotContain("format_turns", uri.Query); }
        else { Assert.Contains("format_turns=true", uri.Query); Assert.DoesNotContain("language_codes=", uri.Query); }
    }

    [Fact]
    public void UnsupportedLiveLanguageAndOversizedDictionaryRequireBatch()
    {
        Assert.Throws<NotSupportedException>(() => AssemblyAiStreamingSession.BuildUri(AssemblyAiModels.All[1], "ja", null));
        Assert.Throws<NotSupportedException>(() => AssemblyAiStreamingSession.BuildUri(AssemblyAiModels.All[0], "de", new string('x', 51)));
    }

    [Theory]
    [InlineData(100)] [InlineData(1600)] [InlineData(32002)] [InlineData(96000)]
    public async Task FinalizeFlushesTailWithinChunkLimitsAndWaitsForTermination(int size)
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        socket.Text("{\"type\":\"Begin\"}"); await session.WaitForBeginAsync(default);
        var events = new List<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Add;
        var audio = Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();
        await session.SendAudioAsync(audio, default);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync(); Assert.False(finish.IsCompleted);
        socket.Text(Turn(0, "grüße", true, false));
        socket.Text(Turn(0, "Grüße!", true, true), fragmented: true);
        socket.Text(Turn(0, "Grüße!", true, true)); // formatting re-delivery
        socket.Text(Turn(1, "Grüße!", true, true)); // repeated speech is retained
        socket.Text("{\"type\":\"Termination\"}"); await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "Grüße!", "Grüße!" }, events.Where(e => e.IsFinal).Select(e => e.Text));
        Assert.All(events, e => Assert.Equal("de", e.DetectedLanguage));
        Assert.Equal(audio, socket.Audio.SelectMany(b => b).Take(size));
        Assert.All(socket.Audio, b => Assert.InRange(b.Length, 1600, 32000));
        Assert.All(socket.Audio.SelectMany(b => b).Skip(size), b => Assert.Equal(0, b));
        Assert.Single(socket.Control); Assert.Equal("Terminate", socket.Control[0]);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("{\"type\":\"Error\",\"error\":\"private key and recording\"}")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"Turn\",\"transcript\":42}")]
    public async Task BrokenStreamCannotReportSuccessOrLeakPayload(string response)
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        await session.SendAudioAsync(new byte[1600], default);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        if (response == "close") socket.Close(); else socket.Text(response);
        var ex = await Assert.ThrowsAsync<IOException>(() => finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("private", ex.ToString());
    }

    [Fact]
    public async Task TerminationWithUnconfirmedTurnFails()
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(Turn(0, "Partial", false, false)); socket.Text("{\"type\":\"Termination\"}");
        await Assert.ThrowsAsync<IOException>(() => finish);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MissingTerminationTimesOutOrCancels(bool cancel)
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket, TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cts.Token); await socket.Terminated.Reader.ReadAsync();
        if (cancel) { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish); }
        else await Assert.ThrowsAsync<TimeoutException>(() => finish);
        await session.DisposeAsync(); Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task FailedHandshakeIsObservedBeforeSessionStarts()
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        socket.Close(); await Assert.ThrowsAsync<IOException>(() => session.WaitForBeginAsync(default));
    }

    [Fact]
    public async Task PcmRequiresCompleteSamples()
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAudioAsync(new byte[3], default));
        Assert.Empty(socket.Audio);
    }

    [Theory]
    [InlineData(1000, true)] // the last word ends where the speech ends
    [InlineData(500, false)] // speech after the last word may still be on its way
    public async Task FinalTurnCoveringTheSentAudioFinishesBeforeTermination(int lastWordEndMs, bool early)
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        var events = new List<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Add;
        await session.SendAudioAsync(Pcm(1.0, 8000), default); // speech
        await session.SendAudioAsync(Pcm(0.5, 0), default);    // silence before the stop
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(WordTurn(0, "Hallo Welt.", lastWordEndMs));
        if (early) await finish.WaitAsync(TimeSpan.FromSeconds(2));
        else
        {
            await Task.Delay(200); Assert.False(finish.IsCompleted);
            socket.Text("{\"type\":\"Termination\"}"); await finish.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal("Hallo Welt.", Assert.Single(events, e => e.IsFinal).Text);
    }

    [Fact]
    public async Task OpenTurnAfterTerminateWaitsForTermination()
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        await session.SendAudioAsync(Pcm(1.0, 8000), default);
        socket.Text(WordTurn(0, "Hallo.", 1000));
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        await finish.WaitAsync(TimeSpan.FromSeconds(2)); // already covered when Terminate went out

        var second = new Socket(); await using var open = new AssemblyAiStreamingSession(second);
        await open.SendAudioAsync(Pcm(1.0, 8000), default);
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.TranscriptReceived += e => { if (!e.IsFinal) partial.TrySetResult(); };
        second.Text(WordTurn(0, "Hallo.", 1000)); second.Text(Turn(1, "und", false, false));
        await partial.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var waiting = open.FinalizeAsync(default); await second.Terminated.Reader.ReadAsync();
        await Task.Delay(200); Assert.False(waiting.IsCompleted);
        second.Text(Turn(1, "Und.", true, true)); second.Text("{\"type\":\"Termination\"}");
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FinalizeDoesNotFinishBeforeTheHostHasTheFinalTurn()
    {
        var socket = new Socket(); await using var session = new AssemblyAiStreamingSession(socket);
        var delivering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var delivered = new List<string>();
        session.TranscriptReceived += e => { if (!e.IsFinal) return; delivering.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); delivered.Add(e.Text); };
        await session.SendAudioAsync(Pcm(1.0, 8000), default);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(WordTurn(0, "Hallo Welt.", 1000));
        await delivering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(200); Assert.False(finish.IsCompleted);
        release.Set(); await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hallo Welt.", Assert.Single(delivered));
    }

    [Fact]
    public async Task CancelledSessionSendsTerminateToEndBilling()
    {
        var socket = new Socket(); var session = new AssemblyAiStreamingSession(socket);
        await session.SendAudioAsync(Pcm(0.2, 8000), default);
        await session.DisposeAsync();
        Assert.Equal("Terminate", Assert.Single(socket.Control)); Assert.True(socket.Aborted);
    }

    private static byte[] Pcm(double seconds, short amplitude)
    {
        var samples = (int)(seconds * 16000);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(i % 2 == 0 ? amplitude : -amplitude);
            bytes[2 * i] = (byte)value; bytes[2 * i + 1] = (byte)(value >> 8);
        }
        return bytes;
    }

    private static string WordTurn(int order, string text, int lastWordEndMs) => JsonSerializer.Serialize(new
    {
        type = "Turn", turn_order = order, transcript = text, end_of_turn = true, turn_is_formatted = true,
        words = new[] { new { text = "Hallo", start = 100, end = lastWordEndMs / 2 }, new { text = "Welt.", start = lastWordEndMs / 2, end = lastWordEndMs } }
    });

    private static string Turn(int order, string text, bool end, bool formatted) => JsonSerializer.Serialize(new
    { type = "Turn", turn_order = order, transcript = text, end_of_turn = end, turn_is_formatted = formatted, language_code = "de" });

    private sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
        public readonly Channel<bool> Terminated = Channel.CreateUnbounded<bool>();
        public readonly List<byte[]> Audio = [];
        public readonly List<string> Control = [];
        public bool Aborted;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public void Text(string text, bool fragmented = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (fragmented)
            {
                var split = bytes.Length / 2;
                _incoming.Writer.TryWrite((bytes[..split], WebSocketMessageType.Text, false));
                _incoming.Writer.TryWrite((bytes[split..], WebSocketMessageType.Text, true));
            }
            else _incoming.Writer.TryWrite((bytes, WebSocketMessageType.Text, true));
        }
        public void Close() => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close, true));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var frame = await _incoming.Reader.ReadAsync(ct); frame.Bytes.CopyTo(buffer.AsSpan());
            return new(frame.Bytes.Length, frame.Type, frame.End);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (type == WebSocketMessageType.Binary) Audio.Add(buffer.ToArray());
            else
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan());
                Control.Add(payload.GetProperty("type").GetString()!);
                if (Control.Last() == "Terminate") Terminated.Writer.TryWrite(true);
            }
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
