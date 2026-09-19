using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.Gladia;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace PortableMigration.Tests;

public sealed class GladiaStreamingTests
{
    [Theory]
    [InlineData("de", 1)]
    [InlineData("auto", 0)]
    public void FixedLanguageIsSentWithoutCodeSwitchingWhileAutomaticRemainsAvailable(string language, int expectedCount)
    {
        using var plugin = new GladiaPlugin();
        Assert.Contains("de", plugin.SupportedLanguages);
        Assert.Contains("en", plugin.SupportedLanguages);
        Assert.DoesNotContain("auto", plugin.SupportedLanguages);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(GladiaPlugin.StreamingConfiguration([language], null)));
        var config = doc.RootElement.GetProperty("language_config");
        Assert.Equal(expectedCount, config.GetProperty("languages").GetArrayLength());
        if (expectedCount > 0) Assert.Equal(language, config.GetProperty("languages")[0].GetString());
        Assert.False(config.GetProperty("code_switching").GetBoolean());
    }

    [Fact]
    public void ConfigurationPreservesOrderedHintsVocabularyAndPcmFormat()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(GladiaPlugin.StreamingConfiguration(["de", "en", "de", "auto"], "TypeWhisper, Marco")));
        var root = doc.RootElement;
        Assert.Equal("solaria-1", root.GetProperty("model").GetString());
        Assert.Equal("wav/pcm", root.GetProperty("encoding").GetString());
        Assert.Equal(16000, root.GetProperty("sample_rate").GetInt32());
        Assert.Equal(16, root.GetProperty("bit_depth").GetInt32());
        Assert.Equal(1, root.GetProperty("channels").GetInt32());
        Assert.Equal(["de", "en"], root.GetProperty("language_config").GetProperty("languages").EnumerateArray().Select(x => x.GetString()));
        Assert.True(root.GetProperty("language_config").GetProperty("code_switching").GetBoolean());
        Assert.True(root.GetProperty("messages_config").GetProperty("receive_partial_transcripts").GetBoolean());
        Assert.True(root.GetProperty("messages_config").GetProperty("receive_lifecycle_events").GetBoolean());
        Assert.Equal(["TypeWhisper", "Marco"], root.GetProperty("realtime_processing").GetProperty("custom_vocabulary_config").GetProperty("vocabulary").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task ExistingModelSupportsStreamingAndRequiresCredentials()
    {
        using var plugin = new GladiaPlugin();
        Assert.True(plugin.SupportsStreaming); Assert.True(plugin.SupportsStreamingCompletion);
        Assert.Equal("default", plugin.SelectedModelId);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.StartStreamingAsync("de", default));
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.StartStreamingAsync(null, new(true)));
    }

    [Theory]
    [InlineData("ws://api.gladia.io/v2/live")]
    [InlineData("wss://attacker.invalid/v2/live")]
    [InlineData("wss://api.gladia.io.attacker.invalid/v2/live")]
    [InlineData("wss://user@api.gladia.io/v2/live")]
    [InlineData("wss://api.gladia.io:444/v2/live")]
    [InlineData("wss://api.gladia.io/elsewhere")]
    [InlineData("wss://api.gladia.io/v2/live#fragment")]
    public void InvalidWebSocketEndpointsAreRejected(string url) => Assert.Throws<PluginRequestException>(() => GladiaPlugin.StreamingEndpoint(url));

    [Fact]
    public async Task HostReplacesPartialsKeepsFinalsAndWaitsForLastUtterance()
    {
        var socket = new Socket(); await using var session = new GladiaStreamingSession(socket);
        using var plugin = new StreamingEngine(session);
        var previews = new List<string>(); var failed = false;
        await using var host = new StreamingDictation((run, ct) => run(plugin, ct), ["de"], text => previews.Add(text), () => failed = true, default);
        host.Append(new float[1600]); await socket.AudioSent.Reader.ReadAsync();
        socket.Text(Transcript("one", "falsch", false), fragmented: true);
        socket.Text(Transcript("one", "Hallo", false));
        socket.Text(Transcript("one", "Hallo Welt.", true));
        socket.Text(Transcript("one", "Hallo Welt.", true));
        socket.Text(Transcript("two", "Zweiter", false));
        var finish = host.FinishAsync(1600); await socket.Terminated.Reader.ReadAsync();
        Assert.False(finish.IsCompleted);
        socket.Text(Transcript("two", "Zweiter Satz.", true)); socket.Text("{\"type\":\"end_session\"}");
        Assert.Equal("Hallo Welt. Zweiter Satz.", await finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(failed); Assert.Contains("falsch", previews); Assert.Contains("Hallo", previews);
        Assert.Equal("de", host.DetectedLanguage);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"transcript\",\"data\":{}}")]
    [InlineData("{\"type\":\"stop_recording\",\"acknowledged\":false}")]
    public async Task InvalidOrClosedStreamFailsInsteadOfReturningPartialSuccess(string response)
    {
        var socket = new Socket(); await using var session = new GladiaStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        if (response == "close") socket.Close(); else socket.Text(response);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public async Task RejectedAudioAcknowledgmentsHaveSafeTypedErrors(int status, PluginRequestFailureKind kind)
    {
        var socket = new Socket(); await using var session = new GladiaStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(JsonSerializer.Serialize(new { type="audio_chunk", acknowledged=false, error=new {status_code=status,message="private-key and transcript"}}));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finish);
        Assert.Equal(kind, error.FailureKind); Assert.Equal(status, error.HttpStatusCode);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MissingCompletionIsBoundedAndDisposalAbortsTheSocket(bool cancel)
    {
        var socket = new Socket(); await using var session = new GladiaStreamingSession(socket, TimeSpan.FromMilliseconds(150));
        using var cts = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cts.Token); await socket.Terminated.Reader.ReadAsync();
        if (cancel) { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish); }
        else await Assert.ThrowsAsync<TimeoutException>(() => finish);
        await session.DisposeAsync(); Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task UnconfirmedWordsCannotBeSilentlyDroppedAtCompletion()
    {
        var socket = new Socket(); await using var session = new GladiaStreamingSession(socket);
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text(Transcript("one", "unfinished", false)); socket.Text("{\"type\":\"end_session\"}");
        await Assert.ThrowsAsync<PluginRequestException>(() => finish);
    }

    [Fact]
    public async Task PcmIsChunkedInOrderAndStopRecordingIsSentOnlyByFinalize()
    {
        var socket = new Socket(); await using var session = new GladiaStreamingSession(socket);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAudioAsync(new byte[3], default));
        await session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, default); Assert.Empty(socket.Audio);
        var audio = Enumerable.Range(0, 70000).Select(x => (byte)x).ToArray();
        await session.SendAudioAsync(audio, default);
        Assert.Equal(audio, socket.Audio.SelectMany(x => x)); Assert.All(socket.Audio, x => Assert.InRange(x.Length, 2, 32000));
        var finish = session.FinalizeAsync(default); await socket.Terminated.Reader.ReadAsync();
        socket.Text("{\"type\":\"stop_recording\",\"acknowledged\":true,\"error\":null}");
        socket.Text("{\"type\":\"end_recording\",\"data\":{}}");
        socket.Text("{\"type\":\"end_session\"}"); await finish;
        await Assert.ThrowsAsync<PluginRequestException>(() => session.SendAudioAsync(new byte[2], default));
    }

    private static string Transcript(string id, string text, bool final) => JsonSerializer.Serialize(new
    { type="transcript", data=new { id, is_final=final, utterance=new {text,language="de"} } });

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
            if (type == WebSocketMessageType.Text)
            {
                Assert.Equal("{\"type\":\"stop_recording\"}", Encoding.UTF8.GetString(buffer));
                Terminated.Writer.TryWrite(true);
            }
            else { Assert.Equal(WebSocketMessageType.Binary, type); Audio.Add(buffer.ToArray()); AudioSent.Writer.TryWrite(true); }
            return Task.CompletedTask;
        }
        public override void Abort() { Aborted = true; _incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    }
}
