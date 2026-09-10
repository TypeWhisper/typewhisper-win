using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.PluginSDK;

public sealed class ElevenLabsStreamingTests
{
    [Theory]
    [InlineData(null)] [InlineData("auto")] [InlineData("de&bad=true")]
    public void ConnectionUsesManualCommitAndEscapesLanguage(string? language)
    {
        var uri = ElevenLabsStreamingSession.BuildRealtimeUri("scribe_v2_realtime", language, true);
        Assert.Equal("wss", uri.Scheme);
        Assert.Contains("commit_strategy=manual", uri.Query);
        Assert.Contains("no_verbatim=true", uri.Query);
        Assert.Contains("include_timestamps=true", uri.Query);
        if (language?.StartsWith("de") == true) Assert.Contains("language_code=de%26bad%3Dtrue", uri.Query);
        else Assert.DoesNotContain("language_code", uri.Query);
    }

    [Theory]
    [InlineData(3200)] [InlineData(3202)] [InlineData(100)]
    public async Task FinalizationFlushesEvenWithoutBufferedTailAndWaitsForTimestampedFinal(int length)
    {
        var socket = new Socket();
        await using var session = new ElevenLabsStreamingSession(socket);
        var updates = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += updates.Add;
        var audio = Enumerable.Range(0, length).Select(n => (byte)(n % 256)).ToArray();
        await session.SendAudioAsync(audio, default);
        var finish = session.FinalizeAsync(default);
        await socket.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(finish.IsCompleted);
        socket.Text("""{"message_type":"committed_transcript","text":"Grüße!"}""");
        socket.Text(Final("Grüße!"), fragmented: true);
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Grüße!", Assert.Single(updates).Text);
        Assert.Equal("de", updates.Single().DetectedLanguage);
        Assert.Equal(audio, socket.Sent.SelectMany(p => Convert.FromBase64String(p.GetProperty("audio_base_64").GetString()!)));
        Assert.True(socket.Sent.Last().GetProperty("commit").GetBoolean());
    }

    [Fact]
    public async Task PeriodicCommitRetainsIdenticalRepeatedSentences()
    {
        var socket = new Socket();
        await using var session = new ElevenLabsStreamingSession(socket);
        var finals = new List<string>();
        session.TranscriptReceived += e => { if (e.IsFinal) finals.Add(e.Text); };
        var send = session.SendAudioAsync(new byte[640000], default);
        await socket.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(send.IsCompleted);
        socket.Text(Final("Ja."));
        await send.WaitAsync(TimeSpan.FromSeconds(2));
        await session.SendAudioAsync(new byte[3200], default);
        var finish = session.FinalizeAsync(default);
        await socket.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        socket.Text(Final("Ja."));
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "Ja.", "Ja." }, finals);
        Assert.All(socket.Sent, p => Assert.InRange(Convert.FromBase64String(p.GetProperty("audio_base_64").GetString()!).Length, 0, 32000));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TransportFailureCannotReturnPartialSuccess(bool error)
    {
        var socket = new Socket();
        await using var session = new ElevenLabsStreamingSession(socket);
        await session.SendAudioAsync(new byte[3200], default);
        var finish = session.FinalizeAsync(default);
        await socket.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        if (error) socket.Text("""{"message_type":"auth_error","error":"private key and audio"}""");
        else socket.Close();
        var ex = await Assert.ThrowsAsync<IOException>(() => finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("private", ex.Message);
    }

    [Fact]
    public async Task CancelledCommitDisposesWithoutHanging()
    {
        var socket = new Socket();
        var session = new ElevenLabsStreamingSession(socket);
        await session.SendAudioAsync(new byte[3200], default);
        using var cancel = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cancel.Token);
        await socket.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        await session.DisposeAsync(); Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task PeriodicCommitWaitsForSilenceInsteadOfSplittingSpeech()
    {
        var socket = new Socket(); await using var session = new ElevenLabsStreamingSession(socket);
        var speech = Enumerable.Repeat((byte)127, 640000).ToArray();
        await session.SendAudioAsync(speech, default);
        Assert.False(socket.Commits.Reader.TryRead(out _));
        var pause = session.SendAudioAsync(new byte[6400], default);
        await socket.Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        socket.Text(Final("Complete words."));
        await pause.WaitAsync(TimeSpan.FromSeconds(2));
        await session.FinalizeAsync(default);
        Assert.False(socket.Commits.Reader.TryRead(out _));
    }

    [Fact]
    public async Task UnbrokenSpeechFallsBackBeforeProviderAutomaticCommit()
    {
        var socket = new Socket(); await using var session = new ElevenLabsStreamingSession(socket);
        await Assert.ThrowsAsync<IOException>(() => session.SendAudioAsync(Enumerable.Repeat((byte)127, 1024000).ToArray(), default));
        Assert.False(socket.Commits.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EmptyRecordingDoesNotSendAnEmptyCommit()
    {
        var socket = new Socket(); await using var session = new ElevenLabsStreamingSession(socket);
        await session.FinalizeAsync(default); Assert.Empty(socket.Sent);
    }

    private static string Final(string text) => JsonSerializer.Serialize(new { message_type = "committed_transcript_with_timestamps", text, language_code = "de", words = Array.Empty<object>() });
    private sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
        public readonly Channel<bool> Commits = Channel.CreateUnbounded<bool>();
        public readonly List<JsonElement> Sent = [];
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
            Assert.Equal(WebSocketMessageType.Text, type);
            var payload = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan());
            Sent.Add(payload);
            if (payload.GetProperty("commit").GetBoolean()) Commits.Writer.TryWrite(true);
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
