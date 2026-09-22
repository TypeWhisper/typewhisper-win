using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace PortableMigration.Tests;

public sealed class SonioxStreamingTests
{
    [Theory]
    [InlineData("us", "stt-rt.soniox.com")]
    [InlineData("eu", "stt-rt.eu.soniox.com")]
    [InlineData("jp", "stt-rt.jp.soniox.com")]
    public void ConfigurationPreservesRegionAndOrderedLanguages(string region, string host)
    {
        Assert.Equal(new Uri("wss://" + host + "/transcribe-websocket"), SonioxStreamingSession.Endpoint(region));
        using var doc = JsonDocument.Parse(SonioxStreamingSession.Configuration("fixture-key", ["de", "en"]));
        var config = doc.RootElement;
        Assert.Equal("fixture-key", config.GetProperty("api_key").GetString());
        Assert.Equal("stt-rt-v5", config.GetProperty("model").GetString());
        Assert.Equal("pcm_s16le", config.GetProperty("audio_format").GetString());
        Assert.Equal(16000, config.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, config.GetProperty("num_channels").GetInt32());
        Assert.Equal(["de", "en"], config.GetProperty("language_hints").EnumerateArray().Select(x => x.GetString()));
        Assert.True(config.GetProperty("enable_endpoint_detection").GetBoolean());
        Assert.True(config.GetProperty("enable_language_identification").GetBoolean());
    }

    [Fact]
    public async Task ExistingModelAdvertisesLiveCompletionAndRequiresCredentials()
    {
        using var plugin = new SonioxPlugin();
        Assert.True(plugin.SupportsStreaming); Assert.True(plugin.SupportsStreamingCompletion);
        Assert.Equal("default", plugin.SelectedModelId);
        Assert.Single(plugin.TranscriptionModels);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.StartStreamingAsync("de", default));
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.StartStreamingAsync(null, new(true)));
        Assert.Throws<ArgumentException>(() => SonioxStreamingSession.Endpoint("elsewhere"));
    }

    [Fact]
    public async Task HostReceivesReplaceablePreviewAndWholeFinalWordsWithoutDuplicates()
    {
        var socket = new Socket();
        await using var session = new SonioxStreamingSession(socket);
        using var plugin = new StreamingEngine(session);
        var previews = new List<string>(); var failed = false;
        await using var host = new StreamingDictation((run, ct) => run(plugin, ct), ["de"], text => previews.Add(text), () => failed = true, default);
        host.Append(new float[1600]); await socket.AudioSent.Reader.ReadAsync();
        socket.Text(Response(Token("Hal", true), Token("unrichtig", false)), fragmented: true);
        socket.Text(Response(Token("lo", true), Token(" Welt", false)));
        socket.Text(Response(Token(" Welt.", true), Token("<end>", true)));
        socket.Text(Response(Token(" Zweiter", false)));
        var finish = host.FinishAsync(1600);
        await socket.Terminated.Reader.ReadAsync();
        socket.Text(Response(Token(" Zweiter Satz.", true), finished: true));
        Assert.Equal("Hallo Welt. Zweiter Satz.", await finish);
        Assert.False(failed);
        Assert.Contains("Halunrichtig", previews); Assert.Contains("Hallo Welt", previews);
        Assert.Equal("Hallo Welt. Zweiter Satz.", previews.Last());
        Assert.Equal("de", host.DetectedLanguage);
    }

    [Fact]
    public async Task AudioIsOrderedAndEmptyInputOnlyTerminatesDuringFinalize()
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket);
        var audio = Enumerable.Range(0, 70000).Select(x => (byte)x).ToArray();
        await session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, default);
        Assert.Empty(socket.Audio);
        await session.SendAudioAsync(audio, default);
        Assert.Equal(audio, socket.Audio.SelectMany(x => x));
        Assert.All(socket.Audio, x => Assert.InRange(x.Length, 2, 32000));
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        Assert.False(finish.IsCompleted);
        socket.Text(Response(finished: true)); await finish;
        Assert.Single(socket.Audio, x => x.Length == 0);
        await Assert.ThrowsAsync<PluginRequestException>(() => session.SendAudioAsync(new byte[2], default));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"tokens\":{}}")]
    [InlineData("{\"tokens\":[{\"text\":42,\"is_final\":true}]}")]
    [InlineData("{\"tokens\":[{\"text\":\"private\",\"is_final\":\"true\"}]}")]
    [InlineData("{\"tokens\":[],\"finished\":\"true\"}")]
    public async Task InvalidOrClosedStreamCannotReturnPartialSuccess(string response)
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        if (response == "close") socket.Close(); else socket.Text(response);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public async Task ProviderErrorsAreTypedWithoutEchoingPayloads(int status, PluginRequestFailureKind kind)
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(JsonSerializer.Serialize(new { error_code = status, error_message = "private-key and transcript" }));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finish);
        Assert.Equal(kind, error.FailureKind); Assert.Equal(status, error.HttpStatusCode);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MissingCompletionIsBoundedAndDisposalAbortsTheSocket(bool cancel)
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket, TimeSpan.FromMilliseconds(150));
        using var cts = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cts.Token); await socket.Terminated.Reader.ReadAsync();
        if (cancel) { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish); }
        else await Assert.ThrowsAsync<TimeoutException>(() => finish);
        await session.DisposeAsync(); Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task FinishedWithUnconfirmedTextFailsInsteadOfDroppingWords()
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(Response(Token("unfinished", false))); socket.Text(Response(finished: true));
        await Assert.ThrowsAsync<PluginRequestException>(() => finish);
    }

    [Fact]
    public async Task EmptySnapshotClearsRetractedInterimText()
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket);
        var events = Channel.CreateUnbounded<StreamingTranscriptEvent>();
        session.TranscriptReceived += update => events.Writer.TryWrite(update);
        socket.Text(Response(Token("retracted", false)));
        Assert.Equal("retracted", (await events.Reader.ReadAsync()).Text);
        socket.Text(Response());
        Assert.Equal("", (await events.Reader.ReadAsync()).Text);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(Response(finished: true)); await finish;
    }

    [Fact]
    public async Task OddPcmChunksAreRejectedWithoutNetworkWrites()
    {
        var socket = new Socket(); await using var session = new SonioxStreamingSession(socket);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAudioAsync(new byte[3], default));
        Assert.Empty(socket.Audio);
    }

    private static object Token(string text, bool final) => new { text, is_final = final, language = "de" };
    private static string Response(object? first = null, object? second = null, bool finished = false) =>
        JsonSerializer.Serialize(new { tokens = new[] { first, second }.Where(x => x is not null), finished });

    private sealed class StreamingEngine(IStreamingSession session) : ITranscriptionEnginePlugin
    {
        public string PluginId => "fixture"; public string PluginName => "fixture"; public string PluginVersion => "1.0.0";
        public string ProviderId => "fixture"; public string ProviderDisplayName => "fixture";
        public bool IsConfigured => true; public bool SupportsStreaming => true; public bool SupportsStreamingCompletion => true;
        public bool SupportsTranslation => false; public string? SelectedModelId => "fixture";
        public IReadOnlyList<TypeWhisper.PluginSDK.Models.PluginModelInfo> TranscriptionModels => [];
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask; public void SelectModel(string id) { } public void Dispose() { }
        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) => Task.FromResult(session);
        public Task<TypeWhisper.PluginSDK.Models.PluginTranscriptionResult> TranscribeAsync(byte[] audio, string? language, bool translate, string? prompt, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
        public readonly Channel<bool> Terminated = Channel.CreateUnbounded<bool>();
        public readonly Channel<bool> AudioSent = Channel.CreateUnbounded<bool>();
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
            Assert.Equal(buffer.Count == 0 ? WebSocketMessageType.Text : WebSocketMessageType.Binary, type);
            Audio.Add(buffer.ToArray());
            if (buffer.Count == 0) Terminated.Writer.TryWrite(true); else AudioSent.Writer.TryWrite(true);
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
