using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.Zoom;
using TypeWhisper.PluginSDK;

public sealed class ZoomStreamingTests
{
    private static readonly ZoomRequest German = ZoomRequest.For(ZoomModels.Language, "de", "TypeWhisper");

    private static async Task<(Socket Socket, ZoomStreamingSession Session)> StartedAsync(ZoomRequest? request = null, TimeSpan? timeout = null)
    {
        request ??= German;
        var socket = new Socket(); var session = new ZoomStreamingSession(socket, request.LanguageCode, timeout);
        var start = session.StartAsync(request, default);
        socket.Text("""{"type":"session.created"}""");
        await NextSentAsync(socket, start);
        socket.Text("""{"type":"session.updated","phrase_count":1}""");
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        return (socket, session);
    }

    /// <summary>
    /// The next text frame the session sends. <paramref name="operation"/> sends it; when it ends first, its fault is
    /// rethrown instead of waiting for a frame that never comes.
    /// </summary>
    private static async Task<string> NextSentAsync(Socket socket, Task operation)
    {
        var sent = socket.Sent.Reader.ReadAsync().AsTask();
        if (await Task.WhenAny(sent, operation).WaitAsync(TimeSpan.FromSeconds(2)) == operation && !sent.IsCompleted)
        {
            await operation;
            throw new Xunit.Sdk.XunitException("The operation ended without sending a frame.");
        }
        return await sent;
    }

    [Fact]
    public async Task SessionSendsSettingsThenAudioAndReturnsTheCompletedItems()
    {
        var (socket, session) = await StartedAsync();
        await using var owned = session;
        var update = JsonSerializer.Deserialize<JsonElement>(socket.Control[0]);
        Assert.Equal("session.update", update.GetProperty("type").GetString());
        Assert.Equal("pcm16", update.GetProperty("audio").GetProperty("format").GetString());
        var config = update.GetProperty("config");
        Assert.Equal("zoom-scribe-de", config.GetProperty("model").GetString()); Assert.Equal("de-DE", config.GetProperty("language").GetString());
        Assert.True(config.GetProperty("interim_results").GetBoolean());
        Assert.Equal("TypeWhisper", Assert.Single(config.GetProperty("vocabulary").GetProperty("phrases").EnumerateArray()).GetString());

        var events = new List<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Add;
        var audio = Enumerable.Range(0, 8000).Select(i => (byte)i).ToArray();
        await session.SendAudioAsync(audio.AsMemory(0, 1000), default);
        await session.SendAudioAsync(audio.AsMemory(1000), default);
        socket.Text("""{"type":"transcription.delta","item_id":"a","delta":"Hallo"}""");
        socket.Text("""{"type":"transcription.delta","item_id":"a","delta":"Hallo Welt"}""");
        socket.Text("""{"type":"transcription.completed","item_id":"a","transcript":"Hallo Welt.","language":"de-DE"}""", fragmented: true);
        socket.Text("""{"type":"transcription.delta","item_id":"a","delta":"stale"}""");
        socket.Text("""{"type":"transcription.delta","item_id":"b","delta":"Wie"}""");
        socket.Text("""{"type":"error","error":{"fatal":false,"message":"private warning"}}""");
        var finish = session.FinalizeAsync(default);
        Assert.Equal("session.close", JsonSerializer.Deserialize<JsonElement>(await NextSentAsync(socket, finish)).GetProperty("type").GetString());
        Assert.False(finish.IsCompleted);
        socket.Text("""{"type":"transcription.completed","item_id":"b","transcript":"Wie geht's?"}""");
        socket.Text("""{"type":"session.closed"}""");
        await finish.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["Hallo", "Hallo Welt", "Hallo Welt.", "Hallo Welt. Wie", "Hallo Welt. Wie geht's?"],
            events.Where(e => !e.IsFinal).Select(e => e.Text));
        // One final event with the whole text, so the host joins nothing.
        var final = Assert.Single(events, e => e.IsFinal);
        Assert.Equal("Hallo Welt. Wie geht's?", final.Text); Assert.Equal("de", final.DetectedLanguage);
        Assert.Equal(audio, socket.Audio.SelectMany(b => b));
        Assert.All(socket.Audio.SkipLast(1), b => Assert.Equal(ZoomStreamingSession.FrameBytes, b.Length));
        Assert.Equal(["session.update", "session.close"], socket.Control.Select(c => JsonSerializer.Deserialize<JsonElement>(c).GetProperty("type").GetString()));
    }

    [Fact]
    public async Task WithoutALanguageZoomDetectsIt()
    {
        var (socket, session) = await StartedAsync(ZoomRequest.For(ZoomModels.Multilingual, null, "ignored"));
        await using var owned = session;
        var config = JsonSerializer.Deserialize<JsonElement>(socket.Control[0]).GetProperty("config");
        Assert.Equal("continuous", config.GetProperty("language_detection").GetProperty("mode").GetString());
        Assert.False(config.TryGetProperty("language", out _)); Assert.False(config.TryGetProperty("vocabulary", out _));
        var events = new List<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Add;
        var finish = session.FinalizeAsync(default); await NextSentAsync(socket, finish);
        socket.Text("""{"type":"transcription.completed","item_id":"a","transcript":"Bonjour.","language":"fr"}""");
        socket.Text("""{"type":"session.closed"}""");
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("fr", Assert.Single(events, e => e.IsFinal).DetectedLanguage);
    }

    [Fact]
    public async Task SilenceFinishesWithoutAFinalEvent()
    {
        var (socket, session) = await StartedAsync();
        await using var owned = session;
        var events = new List<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Add;
        var finish = session.FinalizeAsync(default); await NextSentAsync(socket, finish);
        socket.Text("""{"type":"transcription.completed","item_id":"a","transcript":"  "}""");
        socket.Text("""{"type":"session.closed"}""");
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"fatal":true,"message":"private key and recording","code":"INVALID_MODEL"}}""", "(INVALID_MODEL)")]
    [InlineData("""{"type":"error","error":{"message":"private"}}""", null)]
    [InlineData("close", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    [InlineData("{}", null)]
    public async Task ZoomRejectingTheSettingsFailsTheStart(string response, string? reason)
    {
        var socket = new Socket(); await using var session = new ZoomStreamingSession(socket, "de");
        var start = session.StartAsync(German, default);
        socket.Text("""{"type":"session.created"}"""); await NextSentAsync(socket, start);
        if (response == "close") socket.Close(); else socket.Text(response);
        var ex = await Assert.ThrowsAsync<IOException>(() => start.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("private", ex.ToString());
        if (reason is not null) Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public async Task ASessionThatNeverStartsTimesOut()
    {
        var socket = new Socket(); await using var session = new ZoomStreamingSession(socket, "de", TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<IOException>(() => session.StartAsync(German, default));
        Assert.Contains("did not start", ex.Message); Assert.Empty(socket.Control);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("""{"type":"error","error":{"fatal":true,"message":"private"}}""")]
    [InlineData("timeout")]
    public async Task ASessionThatEndsBeforeSessionClosedFails(string ending)
    {
        var (socket, session) = await StartedAsync(timeout: TimeSpan.FromMilliseconds(300));
        await using var owned = session;
        var events = new List<StreamingTranscriptEvent>(); session.TranscriptReceived += events.Add;
        socket.Text("""{"type":"transcription.completed","item_id":"a","transcript":"Hallo."}""");
        var finish = session.FinalizeAsync(default); await NextSentAsync(socket, finish);
        if (ending == "close") socket.Close(); else if (ending != "timeout") socket.Text(ending);
        var ex = await Assert.ThrowsAsync<IOException>(() => finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("private", ex.ToString());
        // The host then transcribes the complete recording; a preview is not a final text.
        Assert.DoesNotContain(events, e => e.IsFinal);
    }

    [Fact]
    public async Task FinishingFailsWithinTheLimitWhenTheCloseSendStalls()
    {
        var (socket, session) = await StartedAsync(timeout: TimeSpan.FromMilliseconds(300));
        await using var owned = session;
        socket.StallTextSends = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<IOException>(() => session.FinalizeAsync(default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("did not finish", ex.Message); Assert.InRange(started.ElapsedMilliseconds, 250, 1500);
        // Cancelling the caller's token stays a cancellation.
        var (_, other) = await StartedAsync(timeout: TimeSpan.FromSeconds(5));
        await using var otherOwned = other;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.FinalizeAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SessionClosedBeforeTheDictationEndsFails()
    {
        var (socket, session) = await StartedAsync();
        await using var owned = session;
        socket.Text("""{"type":"session.closed"}""");
        await Task.Delay(50);
        await Assert.ThrowsAsync<IOException>(() => session.SendAudioAsync(new byte[3200], default));
    }

    [Fact]
    public async Task CancelledSessionClosesItsZoomSession()
    {
        var (socket, session) = await StartedAsync();
        await session.SendAudioAsync(new byte[6400], default);
        await session.DisposeAsync();
        Assert.Equal("session.close", JsonSerializer.Deserialize<JsonElement>(socket.Control[^1]).GetProperty("type").GetString());
        Assert.True(socket.Aborted);
    }

    [Theory]
    [InlineData(new[] { "今日は", "いい天気です。" }, "今日はいい天気です。")]
    [InlineData(new[] { "你好。", "再见" }, "你好。再见")]
    [InlineData(new[] { "안녕하세요.", "반갑습니다." }, "안녕하세요. 반갑습니다.")]
    [InlineData(new[] { "Hallo", "", "Welt" }, "Hallo Welt")]
    [InlineData(new[] { "TypeWhisper", "です。" }, "TypeWhisper です。")]
    [InlineData(new[] { "𠀀", "𠀁" }, "𠀀𠀁")]
    public void ItemsJoinWithoutSpacesBetweenChineseOrJapaneseText(string[] items, string expected) =>
        Assert.Equal(expected, ZoomStreamingSession.JoinedText(items));

    private sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
        public readonly Channel<string> Sent = Channel.CreateUnbounded<string>();
        public readonly List<byte[]> Audio = [];
        public readonly List<string> Control = [];
        public bool Aborted;
        public bool StallTextSends;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => "live-asr";
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
            if (Aborted) throw new WebSocketException("aborted");
            if (StallTextSends && type == WebSocketMessageType.Text) return Task.Delay(Timeout.Infinite, ct);
            if (type == WebSocketMessageType.Binary) Audio.Add(buffer.ToArray());
            else { var text = Encoding.UTF8.GetString(buffer); Control.Add(text); Sent.Writer.TryWrite(text); }
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
