using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using TypeWhisper.Plugin.Deepgram;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class DeepgramStreamingTests
{
    [Theory]
    [InlineData(null, "multi")]
    [InlineData("auto", "multi")]
    [InlineData("de", "de")]
    [InlineData("de&model=bad", "de%26model%3Dbad")]
    public void StreamingLanguageUsesMultilingualModeAndEscapesQuery(string? language, string expected)
    {
        var uri = DeepgramStreamingSession.BuildUri("nova-3", language);
        Assert.Equal("wss", uri.Scheme);
        Assert.Contains("language=" + expected, uri.Query);
        Assert.DoesNotContain("detect_language", uri.Query);
        Assert.Contains("channels=1", uri.Query);
    }

    [Fact]
    public async Task FinalizationWaitsForLastSegmentAndKeepsUnicodeWithoutDuplicateFinals()
    {
        var socket = new Socket();
        await using var session = new DeepgramStreamingSession(socket);
        var updates = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += updates.Add;
        await session.SendAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2 }, socket.Audio.Single());
        socket.Text(Result("Grü", false));
        socket.Text(Result("Grüße", true), fragmented: true);
        socket.Text(Result("Grüße", true));
        var finish = session.FinalizeAsync(CancellationToken.None);
        await socket.CloseRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(finish.IsCompleted);
        socket.Text(Result("Marco!", true, 1));
        socket.Close();
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "Grü", "Grüße", "Marco!" }, updates.Select(update => update.Text));
        Assert.Equal("de", updates.Last().DetectedLanguage);
    }

    [Fact]
    public async Task UnexpectedCloseDoesNotReturnPartialSuccess()
    {
        var socket = new Socket();
        await using var session = new DeepgramStreamingSession(socket);
        socket.Close(WebSocketCloseStatus.InternalServerError);
        await Assert.ThrowsAsync<IOException>(() => session.FinalizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProviderErrorIsSurfacedWithoutLeakingProviderPayload()
    {
        var socket = new Socket();
        await using var session = new DeepgramStreamingSession(socket);
        socket.Text("{\"type\":\"Error\",\"description\":\"private provider payload\"}");
        var error = await Assert.ThrowsAsync<IOException>(() => session.FinalizeAsync(CancellationToken.None));
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task CancelledFinalizationAbortsAndDrainsReceiveOnDispose()
    {
        var socket = new Socket();
        var session = new DeepgramStreamingSession(socket);
        using var cancel = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cancel.Token);
        await socket.CloseRequested.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        await session.DisposeAsync();
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task HostReplacesInterimTextAndRetainsProviderUntilFinalResult()
    {
        var socket = new Socket();
        var session = new DeepgramStreamingSession(socket);
        var engine = new Engine(session);
        var published = new List<string>();
        var leaseReleased = false;
        await using var stream = new StreamingDictation(async (use, ct) =>
        {
            try { return await use(engine, ct); }
            finally { leaseReleased = true; }
        }, ["de"], published.Add, () => throw new Exception("Unexpected fallback"), CancellationToken.None);
        stream.Append(new float[] { -1, 0, 1 });
        socket.Text(Result("Hallo", false));
        socket.Text(Result("Hallo Welt", false));
        socket.Text(Result("Hallo Welt.", true));
        var finish = stream.FinishAsync(3);
        await socket.CloseRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(leaseReleased);
        socket.Text(Result("Grüße!", true, 1));
        socket.Close();
        Assert.Equal("Hallo Welt. Grüße!", await finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(leaseReleased);
        Assert.Equal(new[] { "Hallo", "Hallo Welt", "Hallo Welt.", "Hallo Welt. Grüße!" }, published);
        Assert.Equal(new byte[] { 0, 128, 0, 0, 255, 127 }, socket.Audio.Single());
        Assert.Equal("de", stream.DetectedLanguage);
        Assert.Equal("de", engine.Language);
    }

    [Fact]
    public async Task HostRequiresFullRecordingFallbackAfterMissingAudio()
    {
        var socket = new Socket();
        var engine = new Engine(new DeepgramStreamingSession(socket));
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine, ct), [], _ => { }, () => failed = true, CancellationToken.None);
        stream.Append(new float[] { 0.2f });
        Assert.Null(await stream.FinishAsync(2).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(failed);
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task HostCancellationDisposesSessionWithoutPublishingFailure()
    {
        var socket = new Socket();
        var engine = new Engine(new DeepgramStreamingSession(socket));
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine, ct), [], _ => { }, () => failed = true, CancellationToken.None);
        stream.Cancel();
        Assert.Null(await stream.FinishAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(failed);
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task HostDropsPartialFinalsWhenTransportFails()
    {
        var socket = new Socket();
        var engine = new Engine(new DeepgramStreamingSession(socket));
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine, ct), [], _ => { }, () => failed = true, CancellationToken.None);
        stream.Append(new float[] { 0.2f });
        socket.Text(Result("Incomplete sentence", true));
        socket.Text("{\"type\":\"Error\"}");
        Assert.Null(await stream.FinishAsync(1).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(failed);
    }

    [Fact]
    public async Task EmptyCompletedStreamDoesNotRequestBatchRetry()
    {
        var socket = new Socket();
        var engine = new Engine(new DeepgramStreamingSession(socket));
        await using var stream = new StreamingDictation((use, ct) => use(engine, ct), [], _ => { }, () => throw new Exception("Unexpected fallback"), CancellationToken.None);
        stream.Append(new float[] { 0 });
        var finish = stream.FinishAsync(1);
        await socket.CloseRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        socket.Close();
        Assert.Equal("", await finish.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static string Result(string text, bool final, int start = 0) => System.Text.Json.JsonSerializer.Serialize(new
    {
        type = "Results", is_final = final, start,
        channel = new { alternatives = new[] { new { transcript = text, languages = new[] { "de" } } } }
    });

    [Fact]
    public async Task QueueOverflowCancelsBlockedConnectionImmediately()
    {
        var failed = false;
        await using var stream = new StreamingDictation(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }, [], _ => { }, () => failed = true, CancellationToken.None);
        for (var i = 0; i < 257; i++) stream.Append(new float[] { 0.2f });
        Assert.Null(await stream.FinishAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(failed);
    }

    private sealed class Engine(IStreamingSession session) : ITranscriptionEnginePlugin
    {
        public string PluginId => "com.example.streaming";
        public string PluginName => "Streaming";
        public string PluginVersion => "1.1.2";
        public string ProviderId => "streaming";
        public string ProviderDisplayName => "Streaming";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => "nova-3";
        public bool SupportsTranslation => false;
        public bool SupportsStreaming => true;
        public bool SupportsStreamingCompletion => true;
        public string? Language;
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) => throw new Exception("Unexpected batch request");
        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        { Language = language; return Task.FromResult(session); }
        public void Dispose() { }
    }

    private sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End, WebSocketCloseStatus? Status)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool, WebSocketCloseStatus?)>();
        public readonly TaskCompletionSource CloseRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<byte[]> Audio = [];
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
                var split = Array.IndexOf(bytes, (byte)0xc3) + 1;
                _incoming.Writer.TryWrite((bytes[..split], WebSocketMessageType.Text, false, null));
                _incoming.Writer.TryWrite((bytes[split..], WebSocketMessageType.Text, true, null));
            }
            else _incoming.Writer.TryWrite((bytes, WebSocketMessageType.Text, true, null));
        }
        public void Close(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure) => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close, true, status));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var frame = await _incoming.Reader.ReadAsync(ct);
            frame.Bytes.CopyTo(buffer.AsSpan());
            return new(frame.Bytes.Length, frame.Type, frame.End, frame.Status, null);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (type == WebSocketMessageType.Binary) Audio.Add(buffer.ToArray());
            else { Assert.Equal("{\"type\":\"CloseStream\"}", Encoding.UTF8.GetString(buffer)); CloseRequested.TrySetResult(); }
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
