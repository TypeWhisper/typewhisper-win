using System.Net;
using System.Text;
using TypeWhisper.Plugin.Xai;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace PortableMigration.Tests;

public sealed class CurrentApiTests
{
    [Theory]
    [InlineData("grok-voice-transcribe-1.0")]
    [InlineData("grok-voice-transcribe-2.0")]
    public async Task Batch_SendsSelectedModelAndKeepsDictionaryTermBoundaries(string model)
    {
        var handler = new Handler(async request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var fields = new List<(string Name, string Value)>();
            foreach (var field in form)
                fields.Add((field.Headers.ContentDisposition!.Name!.Trim('"'), await field.ReadAsStringAsync()));
            Assert.Contains(("model", model), fields);
            Assert.Equal(new[] { "Washington, D.C.", "TypeWhisper" }, fields.Where(f => f.Name == "keyterm").Select(f => f.Value));
            Assert.Equal("file", fields[^1].Name);
            return Json("""{"text":"Hallo","duration":1}""");
        });
        using var fixture = new PortableFixture();
        using var plugin = new XaiPlugin(new HttpClient(handler));
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture");
        plugin.SelectModel(model);
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(["Washington, D.C.", "TypeWhisper"]);
        Assert.Equal("Hallo", (await plugin.TranscribeAsync([0, 0], "de", false, prompt, default)).Text);
    }

    [Fact]
    public void StreamingUri_PreservesModelAndEachKeyterm()
    {
        var uri = XaiStreamingSession.BuildStreamingUri("de", true, "grok-voice-transcribe-1.0", ["Washington, D.C.", "A&B"]);
        Assert.Contains("model=grok-voice-transcribe-1.0", uri.Query);
        Assert.Contains("keyterm=Washington%2C%20D.C.", uri.Query);
        Assert.Contains("keyterm=A%26B", uri.Query);
        Assert.Contains("language=de", uri.Query);
    }

    [Fact]
    public void Collector_DoesNotDuplicateChunksWhenLaterUtteranceIsStitched()
    {
        var collector = new XaiTranscriptCollector();
        collector.ApplyEvent("""{"type":"transcript.partial","text":"First sentence.","is_final":true,"speech_final":true}""");
        collector.ApplyEvent("""{"type":"transcript.partial","text":"Second","is_final":true}""");
        var complete = collector.ApplyEvent("""{"type":"transcript.partial","text":"Second sentence.","is_final":true,"speech_final":true}""");
        Assert.Equal("First sentence. Second sentence.", complete!.Text);
        collector.ApplyEvent("""{"type":"transcript.partial","text":"Second sentence.","is_final":true,"speech_final":true}""");
        Assert.Equal("First sentence. Second sentence. Second sentence.", collector.FinalResult("en").Text);
        Assert.Equal("Authoritative final.", collector.ApplyEvent("""{"type":"transcript.done","text":"Authoritative final."}""")!.Text);
    }

    [Fact]
    public async Task Settings_ExposeCatalogChoicesAndPersistOnlyOnSave()
    {
        using var fixture = new PortableFixture();
        using var plugin = new XaiPlugin();
        await plugin.ActivateAsync(fixture.Host);
        plugin.SetFetchedLlmModels([new("grok-test", "xai")]);
        plugin.SetFetchedVoices([new("test-voice", "Test voice", "de")]);
        Assert.Contains(plugin.TextSettings.Single(s => s.Id == "llmModel").Choices, c => c.Value == "grok-test");
        Assert.Contains(plugin.TextSettings.Single(s => s.Id == "voice").Choices, c => c.Value == "test-voice");
        Assert.All(plugin.TextSettings, s => Assert.False(s.SaveChoiceOnChange));
        await plugin.SaveTextSettingAsync("voice", "test-voice", default);
        await plugin.SaveTextSettingAsync("normalization", "true", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("voice", "missing", default));
        using var reopened = new XaiPlugin();
        await reopened.ActivateAsync(fixture.Host);
        Assert.Equal("test-voice", reopened.SelectedVoiceId);
        Assert.True(reopened.TtsTextNormalization);
    }

    [Fact]
    public async Task MissingCredits_ReportsPermissionFailureWithoutChangingKey()
    {
        using var fixture = new PortableFixture();
        using var plugin = new XaiPlugin(new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("""{"error":"Your team doesn't have any credits or licenses yet."}""") }))));
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ValidateConfigurationAsync(default));
        Assert.Equal(PluginRequestFailureKind.Permission, error.FailureKind);
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Contains("credits", error.Message);
        Assert.True(plugin.IsConfigured);
    }

    [Theory]
    [InlineData("grok-imagine-video")]
    [InlineData("grok-voice-transcribe-2.0")]
    public void TextModelCatalog_ExcludesNonTextModels(string id) => Assert.False(XaiPlugin.IsLlmModel(id));

    [Fact]
    public async Task LegacySpeechModel_MigratesToCurrentDefault()
    {
        using var fixture = new PortableFixture();
        fixture.Host.SetSetting("selectedModel", "grok-stt");
        using var plugin = new XaiPlugin();
        await plugin.ActivateAsync(fixture.Host);
        Assert.Equal("grok-voice-transcribe-2.0", plugin.SelectedModelId);
    }

    [Fact]
    public async Task HostStreamingEntryPoint_ForwardsLanguageModelAndStructuredTerms()
    {
        using var fixture = new PortableFixture();
        var reachedTransport = new IOException("Fixture transport reached.");
        Uri? requestedUri = null;
        using var plugin = new XaiPlugin(new HttpClient(), connectStreaming: (key, language, model, terms, ct) =>
        {
            Assert.Equal("fixture", key);
            requestedUri = XaiStreamingSession.BuildStreamingUri(language, true, model, terms);
            return Task.FromException<IStreamingSession>(reachedTransport);
        });
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture");
        plugin.SelectModel("grok-voice-transcribe-1.0");
        ITranscriptionEnginePlugin engine = plugin;
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(["Washington, D.C.", "TypeWhisper"]);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            engine.StartStreamingWithLanguageHintsAndPromptAsync(["", " de ", "en"], prompt, default));
        Assert.Same(reachedTransport, error);
        Assert.NotNull(requestedUri);
        Assert.Contains("language=de", requestedUri.Query);
        Assert.Contains("model=grok-voice-transcribe-1.0", requestedUri.Query);
        Assert.Contains("keyterm=Washington%2C%20D.C.", requestedUri.Query);
        Assert.Contains("keyterm=TypeWhisper", requestedUri.Query);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
