using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Meta.Tests;

public sealed class MetaPluginTests
{
    [Fact]
    public async Task RefreshAvailableModelsAsync_LoadsAndPartitionsMetaCatalog()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            {
              "object": "list",
              "data": [
                {"id":"muse-spark-1.1","object":"model","owned_by":"meta"},
                {"id":"muse-voice-transcribe-1.0","object":"model","owned_by":"meta"},
                {"id":"muse-spark-1.2","object":"model","owned_by":"meta"},
                {"id":"muse-image-1.0","object":"model","owned_by":"meta"}
              ]
            }
            """);
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        var host = new FakePluginHostServices();
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync(" meta-key ");

        var catalog = await sut.RefreshAvailableModelsAsync();

        Assert.NotNull(catalog);
        Assert.Equal(
            ["muse-spark-1.2", "muse-spark-1.1"],
            catalog.LlmModels.Select(model => model.Id));
        Assert.Equal(
            ["muse-voice-transcribe-1.0"],
            catalog.TranscriptionModels.Select(model => model.Id));
        Assert.Equal("https://api.meta.ai/v1/models", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("meta-key", handler.AuthorizationParameter);
        Assert.Equal(2, sut.SupportedModels.Count);
        Assert.Single(sut.TranscriptionModels);
        Assert.True(host.NotifyCapabilitiesChangedCount >= 2);
    }

    [Fact]
    public async Task RefreshAvailableModelsAsync_RemovesModelsMissingFromSuccessfulRefresh()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"data":[{"id":"muse-spark-1.2"},{"id":"muse-voice-transcribe-1.0"}]}""");
        handler.EnqueueResponse(
            HttpStatusCode.OK,
            """{"data":[{"id":"muse-spark-1.2"}]}""");
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");

        await sut.RefreshAvailableModelsAsync();
        var refreshed = await sut.RefreshAvailableModelsAsync();

        Assert.NotNull(refreshed);
        Assert.Empty(refreshed.TranscriptionModels);
        Assert.Equal(0, sut.FetchedTranscriptionModelCount);
        Assert.Equal(
            MetaPlugin.DefaultTranscriptionModelId,
            Assert.Single(sut.TranscriptionModels).Id);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_ExposesAuthenticationFailureKind()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"bad token"}}""");
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.ValidateApiKeyAsync("invalid"));

        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
    }

    [Fact]
    public async Task ProcessAsync_UsesMetaChatCompletionsParameters()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"Bereinigt"},"finish_reason":"stop"}]}""");
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");
        sut.SetReasoningEffort("high");

        var result = await sut.ProcessAsync(
            "Correct the transcript.",
            "helo",
            "muse-spark-1.2",
            CancellationToken.None);

        Assert.Equal("Bereinigt", result);
        Assert.Equal("https://api.meta.ai/v1/chat/completions", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var root = body.RootElement;
        Assert.Equal("muse-spark-1.2", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.True(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task TranscribeWithLanguageHintsAsync_SendsMetaMultipartRequestAndParsesResponse()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            {
              "sessionId":"session-1",
              "transcript":"Hallo TypeWhisper.",
              "audioDurationMs":1250,
              "turns":[]
            }
            """);
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");

        var result = await sut.TranscribeWithLanguageHintsAsync(
            CreatePcm16Wav(),
            ["de-DE", "en"],
            translate: false,
            prompt: "TypeWhisper, e7b076c2",
            CancellationToken.None);

        Assert.Equal("Hallo TypeWhisper.", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal("https://api.meta.ai/v1/asr/transcribe", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("application/json", handler.Accept);
        Assert.Contains("name=request", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("muse-voice-transcribe-1.0", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("German", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("English", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("TypeWhisper", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("e7b076c2", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("name=audio", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptionRequest_UsesDocumentedMetaFieldNames()
    {
        var json = MetaPlugin.CreateTranscriptionRequestJson(
            "muse-voice-transcribe-1.0",
            ["German", "English"],
            ["TypeWhisper"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("WAV", root.GetProperty("audioEncoding").GetString());
        Assert.Equal("PUSH_TO_TALK", root.GetProperty("mode").GetString());
        Assert.Equal("German", root.GetProperty("languageBias")[0].GetString());
        Assert.Equal("TypeWhisper", root.GetProperty("keywords")[0].GetString());
    }

    [Fact]
    public void RealtimeHandshake_AuthenticatesInFirstFrameAndUsesPcm16KHz()
    {
        var json = MetaRealtimeStreamingSession.CreateHandshakeJson(
            "secret",
            "muse-voice-transcribe-1.0",
            ["German"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            "Bearer secret",
            root.GetProperty("authorization").GetProperty("accessToken").GetString());
        Assert.Equal("PCM_16KHZ", root.GetProperty("audioEncoding").GetString());
        Assert.Equal("PUSH_TO_TALK", root.GetProperty("mode").GetString());
        Assert.Equal("CUMULATIVE", root.GetProperty("partialMode").GetString());
        Assert.False(root.GetProperty("emitAudioProgress").GetBoolean());
    }

    [Theory]
    [InlineData("de", "German")]
    [InlineData("pt-BR", "Portuguese")]
    [InlineData("zh-CN", "Mandarin Chinese")]
    [InlineData("fil", "Tagalog")]
    public void NormalizeLanguageHints_MapsIsoCodesToMetaLanguageNames(
        string language,
        string expected)
    {
        Assert.Equal(expected, Assert.Single(MetaPlugin.NormalizeLanguageHints([language])));
    }

    [Theory]
    [InlineData("German", "de")]
    [InlineData("de-DE", "de")]
    [InlineData("iw", "he")]
    [InlineData("cmn", "zh")]
    [InlineData("fil", "tl")]
    [InlineData("Klingon", null)]
    [InlineData("auto", null)]
    public void FirstLanguageCode_ReturnsCanonicalAcceptedCode(string language, string? expected)
    {
        Assert.Equal(expected, MetaPlugin.FirstLanguageCode([language]));
    }

    [Fact]
    public void ParseTranscriptEvent_HandlesPartialAndFinalEvents()
    {
        var partial = MetaRealtimeStreamingSession.ParseTranscriptEvent(
            """{"type":"transcript","transcript":"hello wor","final":false,"audioProcessedMs":800}""");
        var final = MetaRealtimeStreamingSession.ParseTranscriptEvent(
            """{"type":"transcript","transcript":"Hello world.","final":true,"audioProcessedMs":1200}""");

        Assert.NotNull(partial);
        Assert.False(partial.IsFinal);
        Assert.Equal("hello wor", partial.Text);
        Assert.NotNull(final);
        Assert.True(final.IsFinal);
        Assert.Equal("Hello world.", final.Text);
    }

    [Fact]
    public void ParseTranscriptEvent_RecognizesEmptyTerminalEvent()
    {
        var transcript = MetaRealtimeStreamingSession.ParseTranscriptEvent(
            """{"type":"transcript","transcript":"","final":true}""",
            out var isTerminal);

        Assert.Null(transcript);
        Assert.True(isTerminal);
    }

    [Fact]
    public void ParseTranscriptionResponse_MapsTurnTimestampsToSeconds()
    {
        var result = MetaPlugin.ParseTranscriptionResponse(
            """
            {
              "transcript":"Hello. Hi.",
              "audioDurationMs":8240,
              "turns":[
                {"turnId":1,"startMs":1520,"endMs":4640,"transcript":"Hello.","speaker":"A"},
                {"turnId":2,"startMs":5900,"endMs":8240,"transcript":"Hi.","speaker":"B"}
              ]
            }
            """,
            requestedLanguage: null);

        Assert.Equal(8.24, result.DurationSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(1.52, result.Segments[0].Start);
        Assert.Equal(8.24, result.Segments[1].End);
    }

    [Fact]
    public void Manifest_DeclaresTranscriptionAndLlmCapabilities()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Meta", "manifest.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.Equal("com.typewhisper.meta", root.GetProperty("id").GetString());
        Assert.Equal("TypeWhisper.Plugin.Meta.MetaPlugin", root.GetProperty("pluginClass").GetString());
        Assert.Contains(
            "transcription",
            root.GetProperty("categories").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            "llm",
            root.GetProperty("categories").EnumerateArray().Select(value => value.GetString()));
    }

    private static byte[] CreatePcm16Wav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 4);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(4);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = [];

        public RecordingHandler(HttpStatusCode statusCode, string responseBody)
        {
            EnqueueResponse(statusCode, responseBody);
        }

        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? Accept { get; private set; }
        public string? RequestBody { get; private set; }

        public void EnqueueResponse(HttpStatusCode statusCode, string responseBody) =>
            _responses.Enqueue((statusCode, responseBody));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Accept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakePluginHostServices : IPluginHostServices
    {
        private readonly Dictionary<string, object?> _settings = [];

        public Dictionary<string, string> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public string PluginDataDirectory => Path.GetTempPath();
        public string PluginAssetDirectory => PluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new NoOpPluginLocalization();

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void SetSetting<T>(string key, T value) => _settings[key] = value;
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
    }

    private sealed class NoOpPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoOpPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }
}
