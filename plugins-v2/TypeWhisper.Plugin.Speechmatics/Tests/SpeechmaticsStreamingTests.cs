using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.Speechmatics;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public sealed partial class ProviderTests
{
    [Theory]
    [InlineData("eu", "eu.rt.speechmatics.com")]
    [InlineData("us", "us.rt.speechmatics.com")]
    public async Task StreamingUsesRegionSelectedLanguageModelAndVocabulary(string region, string host)
    {
        using var plugin = new SpeechmaticsPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await plugin.SaveTextSettingAsync("region", region, default); plugin.SelectModel("standard");
        Assert.Contains("de", plugin.SupportedLanguages); Assert.True(plugin.SupportsStreamingCompletion);
        plugin.ConnectStreaming = async (uri, key, language, config, ct) =>
        {
            Assert.Equal(host, uri.Host); Assert.Equal("fixture-key", key); Assert.Equal("de", language);
            var json = JsonSerializer.SerializeToElement(config);
            Assert.Equal("de", json.GetProperty("language").GetString()); Assert.Equal("standard", json.GetProperty("model").GetString());
            Assert.True(json.GetProperty("enable_partials").GetBoolean());
            Assert.Equal("TypeWhisper", json.GetProperty("additional_vocab")[0].GetProperty("content").GetString());
            return await ReadySession(new SpeechmaticsSocket(), config: config);
        };
        await using var session = await plugin.StartStreamingWithLanguageHintsAndPromptAsync(["de"], "TypeWhisper", default);
    }

    [Fact]
    public async Task AutomaticUsesSavedLiveLanguageAndExplicitLanguageOverridesIt()
    {
        using var plugin = new SpeechmaticsPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await plugin.SaveTextSettingAsync("liveLanguage", "de", default);
        var actual = new List<string>();
        plugin.ConnectStreaming = async (_, _, language, _, _) => { actual.Add(language); return await ReadySession(new SpeechmaticsSocket()); };
        await using var first = await plugin.StartStreamingAsync(null, default);
        await using var second = await plugin.StartStreamingAsync("fr", default);
        Assert.Equal(new[] { "de", "fr" }, actual);
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.StartStreamingAsync("unsupported", default));
    }

    [Fact]
    public async Task RevisionsAndConfirmedSegmentsProduceOneCompleteFinalIncludingTextAfterStop()
    {
        var socket = new SpeechmaticsSocket(); await using var session = await ReadySession(socket);
        var events = Channel.CreateUnbounded<StreamingTranscriptEvent>(); session.TranscriptReceived += e => events.Writer.TryWrite(e);
        socket.Text("""{"message":"AddPartialTranscript","metadata":{"transcript":"Grüße Wel"}}""", true);
        Assert.Equal("Grüße Wel", (await events.Reader.ReadAsync()).Text);
        socket.Text("""{"message":"AddPartialTranscript","metadata":{"transcript":"Grüße Welt"}}""");
        Assert.Equal("Grüße Welt", (await events.Reader.ReadAsync()).Text);
        socket.Text("""{"message":"AddTranscript","metadata":{"transcript":"Grüße, Welt! "}}""");
        Assert.Equal("Grüße, Welt! ", (await events.Reader.ReadAsync()).Text);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text("""{"message":"AddTranscript","metadata":{"transcript":"Letzter Satz."}}""");
        Assert.Equal("Grüße, Welt! Letzter Satz.", (await events.Reader.ReadAsync()).Text);
        Assert.False(finish.IsCompleted);
        socket.Text("""{"message":"EndOfTranscript"}""", true); await finish;
        var final = await events.Reader.ReadAsync(); Assert.True(final.IsFinal); Assert.Equal("de", final.DetectedLanguage);
        Assert.Equal("Grüße, Welt! Letzter Satz.", final.Text); Assert.False(events.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(".")] [InlineData(":")] [InlineData(";")]
    [InlineData(")")] [InlineData("]")] [InlineData("}")]
    [InlineData("”")] [InlineData("’")]
    public async Task SeparatePunctuationFragmentAttachesToPreviousWord(string punctuation)
    {
        var socket = new SpeechmaticsSocket(); await using var session = await ReadySession(socket);
        var events = Channel.CreateUnbounded<StreamingTranscriptEvent>(); session.TranscriptReceived += e => events.Writer.TryWrite(e);
        socket.Text("""{"message":"AddTranscript","metadata":{"transcript":"Hello "}}"""); await events.Reader.ReadAsync();
        socket.Text(JsonSerializer.Serialize(new { message = "AddPartialTranscript", metadata = new { transcript = " " + punctuation + " Next sentence." } }));
        Assert.Equal("Hello" + punctuation + " Next sentence.", (await events.Reader.ReadAsync()).Text);
        socket.Text(JsonSerializer.Serialize(new { message = "AddTranscript", metadata = new { transcript = " " + punctuation + " Next sentence." } }));
        Assert.Equal("Hello" + punctuation + " Next sentence.", (await events.Reader.ReadAsync()).Text);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync(); socket.Text("""{"message":"EndOfTranscript"}""");
        await finish; Assert.Equal("Hello" + punctuation + " Next sentence.", (await events.Reader.ReadAsync()).Text);
    }

    [Fact]
    public async Task BinaryPcmIsChunkedAndStopUsesExactSequenceCount()
    {
        var socket = new SpeechmaticsSocket(); await using var session = await ReadySession(socket);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAudioAsync(new byte[3], default));
        await session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, default);
        var pcm = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray(); await session.SendAudioAsync(pcm, default);
        Assert.Equal(pcm, socket.Audio.SelectMany(c => c)); Assert.All(socket.Audio, c => Assert.InRange(c.Length, 2, 32000));
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        var end = JsonSerializer.Deserialize<JsonElement>(socket.Sent.Last());
        Assert.Equal("EndOfStream", end.GetProperty("message").GetString()); Assert.Equal(3, end.GetProperty("last_seq_no").GetInt32());
        socket.Text("""{"message":"EndOfTranscript"}"""); await finish;
        await Assert.ThrowsAsync<PluginRequestException>(() => session.SendAudioAsync(new byte[2], default));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"AddTranscript\",\"metadata\":{\"transcript\":42}}")]
    [InlineData("{\"message\":\"AddPartialTranscript\",\"metadata\":{}}")]
    public async Task MalformedResponseFailsCompletion(string payload)
    {
        var socket = new SpeechmaticsSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync(); socket.Text(payload);
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, (await Assert.ThrowsAsync<PluginRequestException>(() => finish)).FailureKind);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task UnconfirmedPreviewAndDisconnectCannotSucceed(bool close)
    {
        var socket = new SpeechmaticsSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text("""{"message":"AddPartialTranscript","metadata":{"transcript":"pending"}}""");
        if (close) socket.Close(); else socket.Text("""{"message":"EndOfTranscript"}""");
        await Assert.ThrowsAsync<PluginRequestException>(() => finish);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MissingCompletionTimesOutOrCancels(bool cancel)
    {
        var socket = new SpeechmaticsSocket(); await using var session = await ReadySession(socket, TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(); var finish = session.FinalizeAsync(cts.Token); await socket.Ended.Reader.ReadAsync();
        if (cancel) { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish); }
        else Assert.Equal(PluginRequestFailureKind.Timeout, (await Assert.ThrowsAsync<PluginRequestException>(() => finish)).FailureKind);
        await session.DisposeAsync(); Assert.True(socket.Aborted);
    }

    [Theory]
    [InlineData("not_authorised", PluginRequestFailureKind.Authentication)]
    [InlineData("quota_exceeded", PluginRequestFailureKind.RateLimit)]
    [InlineData("job_error", PluginRequestFailureKind.ServerError)]
    [InlineData("invalid_model", PluginRequestFailureKind.InvalidRequest)]
    public async Task StartupErrorsAreTypedAndDoNotExposeSecrets(string type, PluginRequestFailureKind expected)
    {
        var socket = new SpeechmaticsSocket(); socket.Text(JsonSerializer.Serialize(new { message = "Error", type, reason = "private key and transcript" }));
        await using var session = new SpeechmaticsStreamingSession(socket, "de");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => session.InitializeAsync(new { language = "de" }, default));
        Assert.Equal(expected, error.FailureKind); Assert.DoesNotContain("private", error.ToString()); Assert.Empty(socket.Audio);
    }

    [Fact]
    public async Task ActualHostReceivesFinalOnceWithoutRepeatingPreviews()
    {
        using var plugin = new SpeechmaticsPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        plugin.ConnectStreaming = async (_, _, _, _, _) => await ReadySession(new SpeechmaticsSocket { CompleteAutomatically = true });
        var failed = false;
        await using var stream = new StreamingDictation((run, ct) => run(plugin, ct), ["de"], _ => { }, () => failed = true, default);
        stream.Append(new float[4000]); Assert.Equal("Hello, world!", await stream.FinishAsync(4000)); Assert.False(failed);
    }

    private static async Task<SpeechmaticsStreamingSession> ReadySession(SpeechmaticsSocket socket, TimeSpan? timeout = null, object? config = null)
    {
        var session = new SpeechmaticsStreamingSession(socket, "de", timeout);
        await session.InitializeAsync(config ?? new { language = "de", model = "enhanced" }, default); return session;
    }

    private sealed class SpeechmaticsSocket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
        public readonly Channel<bool> Ended = Channel.CreateUnbounded<bool>();
        public readonly List<string> Sent = [];
        public readonly List<byte[]> Audio = [];
        public bool CompleteAutomatically;
        public bool Aborted;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public void Text(string text, bool fragmented = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var count = fragmented ? Math.Max(1, bytes.Length / 2) : 4000;
            for (var offset = 0; offset < bytes.Length; offset += count)
                _incoming.Writer.TryWrite((bytes[offset..Math.Min(offset + count, bytes.Length)], WebSocketMessageType.Text, offset + count >= bytes.Length));
        }
        public void Close() => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close, true));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var frame = await _incoming.Reader.ReadAsync(ct); frame.Bytes.CopyTo(buffer.AsSpan());
            return new(frame.Bytes.Length, frame.Type, frame.End);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Assert.True(end);
            if (type == WebSocketMessageType.Binary) { Audio.Add(buffer.ToArray()); if (CompleteAutomatically) Text("""{"message":"AddPartialTranscript","metadata":{"transcript":"Hello world"}}"""); return Task.CompletedTask; }
            var message = Encoding.UTF8.GetString(buffer); Sent.Add(message);
            using var payload = JsonDocument.Parse(message);
            switch (payload.RootElement.GetProperty("message").GetString())
            {
                case "StartRecognition": Text("""{"message":"RecognitionStarted"}"""); break;
                case "EndOfStream":
                    Ended.Writer.TryWrite(true);
                    if (CompleteAutomatically)
                    {
                        Text("""{"message":"AddTranscript","metadata":{"transcript":"Hello, world!"}}""");
                        Text("""{"message":"EndOfTranscript"}""");
                    }
                    break;
            }
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
