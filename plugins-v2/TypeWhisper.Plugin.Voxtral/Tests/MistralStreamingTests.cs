using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public sealed partial class ProviderTests
{
    [Fact]
    public async Task StreamingCapabilityFollowsSelectedModelAndLanguageIsAutomatic()
    {
        using var plugin = new VoxtralPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        Assert.False(plugin.SupportsStreaming); Assert.Contains("de", plugin.SupportedLanguages);
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.StartStreamingAsync("de", default));
        plugin.SelectModel(VoxtralPlugin.RealtimeModel);
        Assert.True(plugin.SupportsStreaming); Assert.True(plugin.SupportsStreamingCompletion);
        Assert.Empty(plugin.SupportedLanguages);
        Assert.Contains("automatisch", plugin.TextSettings.Single(s => s.Id == "model").Description);
        var socket = new MistralSocket();
        plugin.ConnectStreaming = async (key, model, budget, ct) =>
        {
            Assert.Equal("fixture-key", key); Assert.Equal(VoxtralPlugin.RealtimeModel, model);
            Assert.Equal(TimeSpan.FromSeconds(15), budget);
            return await ReadySession(socket, ct: ct);
        };
        await using var session = await plugin.StartStreamingAsync("de", default);
        using var payload = JsonDocument.Parse(socket.Sent[0]);
        var config = payload.RootElement.GetProperty("session");
        Assert.Equal(16000, config.GetProperty("audio_format").GetProperty("sample_rate").GetInt32());
        Assert.Equal("pcm_s16le", config.GetProperty("audio_format").GetProperty("encoding").GetString());
        Assert.False(config.TryGetProperty("language", out _));
        Assert.InRange(config.GetProperty("target_streaming_delay_ms").GetInt32(), 240, 2400);
        plugin.SelectModel("voxtral-mini-latest"); Assert.False(plugin.SupportsStreaming);
    }

    [Fact]
    public async Task FragmentedDeltasReplacePreviewAndDonePublishesOneAuthoritativeFinal()
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        var events = Channel.CreateUnbounded<StreamingTranscriptEvent>();
        session.TranscriptReceived += value => events.Writer.TryWrite(value);
        socket.Text("""{"type":"transcription.text.delta","text":"Grüße "}""", fragmented: true);
        Assert.Equal(new("Grüße ", false), await events.Reader.ReadAsync());
        socket.Text("""{"type":"transcription.text.delta","text":"Welt"}""");
        Assert.Equal(new("Grüße Welt", false), await events.Reader.ReadAsync());
        socket.Text("""{"type":"transcription.language","audio_language":"de"}""");
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text("""{"type":"transcription.done","text":"Grüße, Welt!","language":null}""", fragmented: true);
        await finish;
        var final = await events.Reader.ReadAsync();
        Assert.True(final.IsFinal); Assert.Equal("Grüße, Welt!", final.Text); Assert.Equal("de", final.DetectedLanguage);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task PcmIsBase64ChunkedAndStopSendsFlushThenEndExactlyOnce()
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAudioAsync(new byte[3], default));
        await session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, default); Assert.Single(socket.Sent);
        var audio = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
        await session.SendAudioAsync(audio, default);
        Assert.Equal(audio, socket.Audio.SelectMany(chunk => chunk));
        Assert.All(socket.Audio, chunk => Assert.InRange(chunk.Length, 2, 32000));
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        Assert.Equal(new[] { "input_audio.flush", "input_audio.end" }, socket.Sent.TakeLast(2).Select(s => JsonDocument.Parse(s).RootElement.GetProperty("type").GetString()));
        socket.Text("""{"type":"transcription.done","text":"done"}"""); await finish;
        await Assert.ThrowsAsync<PluginRequestException>(() => session.FinalizeAsync(default));
        await Assert.ThrowsAsync<PluginRequestException>(() => session.SendAudioAsync(new byte[2], default));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"transcription.text.delta\",\"text\":42}")]
    [InlineData("{\"type\":\"transcription.done\"}")]
    public async Task MalformedLiveResponsesCannotBecomeSuccessfulTranscript(string payload)
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text(payload);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finish);
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Fact]
    public async Task DisconnectAfterPreviewFailsInsteadOfReturningPartialText()
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text("""{"type":"transcription.text.delta","text":"partial"}"""); socket.Close();
        await Assert.ThrowsAsync<PluginRequestException>(() => finish);
    }

    [Fact]
    public async Task EmptyDoneCannotDiscardRecognizedWords()
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text("""{"type":"transcription.text.delta","text":"partial"}""");
        socket.Text("""{"type":"transcription.done","text":""}""");
        await Assert.ThrowsAsync<PluginRequestException>(() => finish);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    [InlineData(3000, PluginRequestFailureKind.InvalidRequest)]
    public async Task ProviderErrorsAreTypedWithoutEchoingSensitiveDetails(int code, PluginRequestFailureKind kind)
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        socket.Text(JsonSerializer.Serialize(new { type = "error", error = new { code, message = "private-key and private transcript" } }));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finish);
        Assert.Equal(kind, error.FailureKind); Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MissingCompletionTimesOutOrCancelsAndDisposalAborts(bool cancel)
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket, TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cts.Token); await socket.Ended.Reader.ReadAsync();
        if (cancel) { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish); }
        else Assert.Equal(PluginRequestFailureKind.Timeout, (await Assert.ThrowsAsync<PluginRequestException>(() => finish)).FailureKind);
        await session.DisposeAsync(); Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task HandshakeErrorIsObservedBeforeAudio()
    {
        var socket = new MistralSocket(); await using var session = new MistralStreamingSession(socket);
        socket.Text("""{"type":"error","error":{"code":401,"message":"secret"}}""");
        await Assert.ThrowsAsync<PluginRequestException>(() => session.InitializeAsync(default));
        Assert.Empty(socket.Sent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OversizedMessagesAndAccumulatedTranscriptsAreRejected(bool accumulated)
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        var finish = session.FinalizeAsync(default); await socket.Ended.Reader.ReadAsync();
        var text = new string('x', accumulated ? 600_000 : 1_100_000);
        socket.Text(JsonSerializer.Serialize(new { type = "transcription.text.delta", text }));
        if (accumulated) socket.Text(JsonSerializer.Serialize(new { type = "transcription.text.delta", text }));
        await Assert.ThrowsAsync<PluginRequestException>(() => finish);
    }

    [Fact]
    public async Task CancellationBeforeSendDoesNotTransmitAudio()
    {
        var socket = new MistralSocket(); await using var session = await ReadySession(socket);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAudioAsync(new byte[320], new(true)));
        Assert.Empty(socket.Audio);
    }

    [Fact]
    public async Task ActualHostCollectsCompleteResultWithoutDuplicatingPreviews()
    {
        using var plugin = new VoxtralPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        plugin.SelectModel(VoxtralPlugin.RealtimeModel);
        var socket = new MistralSocket { CompleteAutomatically = true };
        plugin.ConnectStreaming = (_, _, _, ct) => ReadySessionAsInterface(socket, ct);
        var failed = false;
        await using var stream = new StreamingDictation((run, ct) => run(plugin, ct), [], _ => { }, () => failed = true, default);
        stream.Append(new float[4000]);
        Assert.Equal("Hello, world!", await stream.FinishAsync(4000)); Assert.False(failed);
    }

    [Fact]
    public async Task RealtimeWavPathUsesSameSocketModelAndCompleteResult()
    {
        using var plugin = new VoxtralPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        plugin.SelectModel(VoxtralPlugin.RealtimeModel);
        var socket = new MistralSocket { CompleteAutomatically = true };
        plugin.ConnectStreaming = (_, model, budget, ct) =>
        {
            Assert.Equal(VoxtralPlugin.RealtimeModel, model);
            Assert.Equal(TimeSpan.FromSeconds(15 + 4 / 32000d), budget);
            return ReadySessionAsInterface(socket, ct);
        };
        var result = await plugin.TranscribeAsync(Audio(), "de", false, null, default);
        Assert.Equal("Hello, world!", result.Text); Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(4 / 32000d, result.DurationSeconds); Assert.Equal(4, Assert.Single(socket.Audio).Length);
    }

    [Fact]
    public async Task CompletedRecordingReceivesDurationAwareFinalizationBudget()
    {
        using var plugin = new VoxtralPlugin(); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        plugin.SelectModel(VoxtralPlugin.RealtimeModel);
        var wav = new byte[44 + 32000 * 60]; Audio().AsSpan(0, 44).CopyTo(wav);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), wav.Length - 44);
        var socket = new MistralSocket { CompleteAutomatically = true };
        plugin.ConnectStreaming = async (_, _, budget, ct) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(75), budget);
            return await ReadySession(socket, budget, ct);
        };
        var result = await plugin.TranscribeAsync(wav, null, false, null, default);
        Assert.Equal(60, result.DurationSeconds); Assert.Equal("Hello, world!", result.Text);
    }

    [Theory]
    [InlineData(0)] [InlineData(20)] [InlineData(22)] [InlineData(24)] [InlineData(34)] [InlineData(40)]
    public void InvalidWavIsRejectedBeforeConnecting(int offset)
    {
        var wav = Audio(); wav[offset] = 255;
        Assert.Throws<InvalidDataException>(() => VoxtralPlugin.ExtractPcm(wav));
    }

    private static async Task<IStreamingSession> ReadySessionAsInterface(MistralSocket socket, CancellationToken ct) => await ReadySession(socket, ct: ct);
    private static async Task<MistralStreamingSession> ReadySession(MistralSocket socket, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var session = new MistralStreamingSession(socket, timeout);
        socket.Text("""{"type":"session.created"}""");
        await session.InitializeAsync(ct); return session;
    }

    private sealed class MistralSocket : WebSocket
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
            ct.ThrowIfCancellationRequested(); Assert.Equal(WebSocketMessageType.Text, type); Assert.True(end);
            var message = Encoding.UTF8.GetString(buffer); Sent.Add(message);
            using var payload = JsonDocument.Parse(message);
            switch (payload.RootElement.GetProperty("type").GetString())
            {
                case "session.update": Text("""{"type":"session.updated"}"""); break;
                case "input_audio.append":
                    Audio.Add(Convert.FromBase64String(payload.RootElement.GetProperty("audio").GetString()!));
                    if (CompleteAutomatically) Text("""{"type":"transcription.text.delta","text":"Hello world"}""");
                    break;
                case "input_audio.end":
                    Ended.Writer.TryWrite(true);
                    if (CompleteAutomatically) Text("""{"type":"transcription.done","text":"Hello, world!","language":"en"}""");
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
