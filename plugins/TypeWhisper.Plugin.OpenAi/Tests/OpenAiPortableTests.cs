using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Fact]
    public async Task SettingsFailedWritesRetainPreviousValuesAndKey()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(); await plugin.ActivateAsync(host);
        host.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync("replacement"));
        Assert.Equal("fixture-key", plugin.ApiKey);
        foreach (var (id, value) in new[] { ("transcriptionContext", "context"), ("liveDelay", "high"), ("ttsInstructions", "slow"), ("selectedVoice", "cedar"), ("reasoningEffort", "high"), ("authMode", "chatgpt"), ("llmTemperatureValue", "1.5") })
        {
            var previous = plugin.TextSettings.Single(s => s.Id == id).Value;
            await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync(id, value, default));
            Assert.Equal(previous, plugin.TextSettings.Single(s => s.Id == id).Value);
        }
    }

    [Fact]
    public async Task ContextAndDictionaryUseSeparateMultipartFields()
    {
        var handler = new CapturingHandler(async (request, _) =>
        {
            var fields = new List<(string? Name, string Value)>();
            foreach (var part in Assert.IsType<MultipartFormDataContent>(request.Content))
                fields.Add((part.Headers.ContentDisposition?.Name?.Trim('"'), await part.ReadAsStringAsync()));
            Assert.Equal("Medical meeting", fields.Single(f => f.Name == "prompt").Value);
            Assert.Equal(["TypeWhisper", "OpenAI"], fields.Where(f => f.Name == "keywords[]").Select(f => f.Value));
            return JsonResponse("""{"text":"Result"}""");
        });
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(new HttpClient(handler), compressedUploadFactory: bytes => new(bytes, "audio.wav", "audio/wav"));
        await plugin.ActivateAsync(host);
        await plugin.SaveTextSettingAsync("transcriptionContext", "Medical meeting", default);
        Assert.Equal("Result", (await plugin.TranscribeAsync([0,0], "de", false, "TypeWhisper, OpenAI, TypeWhisper", default)).Text);
    }

    [Fact]
    public void LiveContextAndDelayAreAppliedOnlyToSupportedModels()
    {
        using var live = JsonDocument.Parse(OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload("gpt-live-transcribe", ["de", "en"], "context", ["TypeWhisper"], "high"));
        var options = live.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.Equal("high", options.GetProperty("delay").GetString());
        Assert.Equal("context", options.GetProperty("prompt").GetString());
        Assert.Equal("TypeWhisper", options.GetProperty("keywords")[0].GetString());
        using var legacy = JsonDocument.Parse(OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload("gpt-realtime-whisper", ["de", "en"], "context", ["TypeWhisper"], "high"));
        Assert.DoesNotContain("keywords", legacy.RootElement.ToString());
        Assert.DoesNotContain("context", legacy.RootElement.ToString());
    }

    [Theory]
    [InlineData("Finished")]
    [InlineData("")]
    public async Task LiveFinalizeWaitsForCommittedItemIncludingSilence(string transcript)
    {
        using var ws = new SocketFixture();
        await using var session = new OpenAiRealtimeStreamingSession(ws, new());
        ws.Receive("""{"type":"session.updated"}""");
        await session.StartAsync("gpt-live-transcribe", [], null, default);
        await session.SendAudioAsync(new byte[6400], default);
        var finish = session.FinalizeAsync(default);
        await ws.CommitSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ws.Receive("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"unrelated","transcript":"Earlier"}""");
        ws.Receive("""{"type":"input_audio_buffer.committed","item_id":"final"}""");
        Assert.False(finish.IsCompleted);
        ws.Receive(JsonSerializer.Serialize(new { type = "conversation.item.input_audio_transcription.completed", item_id = "final", transcript }));
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("{\"type\":\"error\",\"error\":{\"message\":\"private payload\"}}")]
    [InlineData("close")]
    [InlineData("silent-close")]
    public async Task LiveFailureAfterDeltaDoesNotSucceed(string message)
    {
        using var ws = new SocketFixture(); await using var session = new OpenAiRealtimeStreamingSession(ws, new());
        ws.Receive("""{"type":"session.updated"}""");
        await session.StartAsync("gpt-live-transcribe", [], null, default);
        var finish = session.FinalizeAsync(default);
        await ws.CommitSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ws.Receive("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"final","delta":"Partial"}""");
        ws.Receive(message);
        var error = await Assert.ThrowsAsync<IOException>(() => finish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("private payload", error.ToString());
    }

    [Fact]
    public async Task LiveFinalizeCancellationAndDisposeDrainReceive()
    {
        using var ws = new SocketFixture(); await using var session = new OpenAiRealtimeStreamingSession(ws, new());
        ws.Receive("""{"type":"session.updated"}""");
        await session.StartAsync("gpt-live-transcribe", [], null, default);
        using var cancel = new CancellationTokenSource();
        var finish = session.FinalizeAsync(cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        await session.DisposeAsync(); Assert.Equal(WebSocketState.Aborted, ws.State);
    }

    [Fact]
    public void ChatGptRequiresCompletedResponseAfterDeltas()
    {
        Assert.Throws<PluginRequestException>(() => OpenAiChatGptClient.ParseResponseText("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n"));
    }

    [Theory]
    [InlineData(400)] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task ProviderErrorsDoNotEchoPrivatePayload(int status)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("{\"error\":{\"message\":\"private-token-and-transcript\"}}") })));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => OpenAiApiTransport.SendWithErrorHandlingAsync(client, request, default));
        Assert.Equal(status, error.HttpStatusCode); Assert.DoesNotContain("private-token", error.ToString());
    }

    [Fact]
    public async Task SpeechUsesRequestVoiceWithoutChangingSavedVoiceAndRejectsInvalidPcm()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture-key";
        var bytes = new byte[4];
        using var plugin = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, body) =>
        {
            using var json = JsonDocument.Parse(body!); Assert.Equal("cedar", json.RootElement.GetProperty("voice").GetString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        })), _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        var playback = await plugin.SpeakAsync(new("Hello") { VoiceId = "cedar" }, default);
        Assert.Equal("marin", plugin.SelectedVoiceId); playback.Stop();
        bytes = [0];
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.SpeakAsync(new("Hello") { VoiceId = "cedar" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SpeakAsync(new(new string('x', 4001)), default));
    }

    [Fact]
    public async Task SignOutRemainsSignedOutAfterRestartAndKeepsApiKey()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture-key";
        host.Secrets["oauth-access-token"] = "old-access"; host.Secrets["oauth-refresh-token"] = "old-refresh";
        using var plugin = new OpenAiPlugin(); await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("logout", default);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.False(plugin.HasChatGptCredentials); Assert.True(plugin.IsConfigured);
    }

    [Theory]
    [InlineData("gpt-5-pro", "high")]
    [InlineData("gpt-5.5", "none")]
    [InlineData("gpt-5", "minimal")]
    [InlineData("gpt-5.6", "max")]
    public void ReasoningChoicesFollowModelCapabilities(string model, string effort)
    {
        Assert.Contains(effort, OpenAiPlugin.SupportedReasoningEfforts(model));
        Assert.Empty(OpenAiPlugin.SupportedReasoningEfforts("gpt-5-chat-latest"));
        Assert.Empty(OpenAiPlugin.SupportedReasoningEfforts("o1-mini"));
        Assert.DoesNotContain("xhigh", OpenAiPlugin.SupportedReasoningEfforts("o3"));
    }

    [Fact]
    public void InvalidWavCannotLoopOrUploadArbitraryBytes()
    {
        Assert.Throws<InvalidDataException>(() => OpenAiRealtimeStreamingSession.ExtractPcm16Data(new byte[44]));
        var wav = new byte[44]; "RIFF"u8.CopyTo(wav); "WAVE"u8.CopyTo(wav.AsSpan(8));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), -8);
        Assert.Throws<InvalidDataException>(() => OpenAiRealtimeStreamingSession.ExtractPcm16Data(wav));
    }

    [Fact]
    public async Task GermanSettingsKeepUmlautsAndExposeLoginActions()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("de-DE");
            using var plugin = new OpenAiPlugin(); await plugin.ActivateAsync(new TestPluginHostServices());
            Assert.Contains("Wörterbuchbegriffe", plugin.TextSettings.Single(f => f.Id == "transcriptionContext").Description);
            Assert.Equal("Verzögerung der Live-Transkription", plugin.TextSettings.Single(f => f.Id == "liveDelay").Title);
            await plugin.SaveTextSettingAsync("authMode", "chatgpt", default);
            Assert.Contains(plugin.SettingsActions, a => a.Title == "Mit ChatGPT anmelden");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task ConnectionSectionComesFirstAndSwitchingMethodsExposesLoginBesideIt()
    {
        using var plugin = new OpenAiPlugin(); await plugin.ActivateAsync(new TestPluginHostServices());
        var connection = plugin.TextSettings.First();
        Assert.Equal("authMode", connection.Id); Assert.True(connection.SaveChoiceOnChange);
        Assert.Equal(PluginSettingsSection.Connection, connection.Section);
        Assert.True(plugin.ShowApiKeySettings);
        Assert.DoesNotContain(plugin.SettingsActions, a => a.Id == "login");
        await plugin.SaveTextSettingAsync("authMode", "chatgpt", default);
        Assert.False(plugin.ShowApiKeySettings);
        Assert.Equal(PluginSettingsSection.Connection, plugin.SettingsActions.Single(a => a.Id == "login").Section);
        Assert.Equal(PluginSettingsSection.TextProcessing, plugin.SettingsActions.Single(a => a.Id == "refresh").Section);
        var sections = plugin.TextSettings.Select(f => f.Section).ToArray();
        Assert.Equal(sections.Order(), sections);
    }

    private sealed class SocketFixture : WebSocket
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private WebSocketState _state = WebSocketState.Open;
        public TaskCompletionSource CommitSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Receive(string json) => _messages.Writer.TryWrite(json);
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _messages.Writer.TryComplete(); }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        { if (Encoding.UTF8.GetString(buffer).Contains("input_audio_buffer.commit")) CommitSent.TrySetResult(); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var message = await _messages.Reader.ReadAsync(cancellationToken);
            if (message == "close") { _state = WebSocketState.CloseReceived; return new(0, WebSocketMessageType.Close, true); }
            if (message == "silent-close") { _state = WebSocketState.Closed; message = "{}"; }
            var bytes = Encoding.UTF8.GetBytes(message); bytes.CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
    }
}
