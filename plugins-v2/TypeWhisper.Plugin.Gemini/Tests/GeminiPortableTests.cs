using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class GeminiPluginTests
{
    [Fact]
    public async Task PortableSettings_PersistModelModeAndTemperatureWithoutExposingKey()
    {
        string? sent = null;
        var host = new TestPluginHostServices();
        using var http = new HttpClient(new CapturingHandler((_, body) =>
        { sent = body; return JsonResponse("""{"choices":[{"finish_reason":"stop","message":{"content":"Ergebnis"}}]}"""); }));
        using var plugin = new GeminiPlugin(http);
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("fixture-secret");
        await plugin.SaveTextSettingAsync("selectedLlmModel", "gemini-pro-latest", default);
        await plugin.SaveTextSettingAsync("llmTemperatureValue", "0.7", default);
        await plugin.SaveTextSettingAsync("llmTemperatureMode", "custom", default);
        await plugin.SaveTextSettingAsync("transcriptionMode", "verbatim", default);
        await plugin.DeactivateAsync();
        Assert.False(plugin.IsConfigured);
        await plugin.ActivateAsync(host);
        Assert.Equal("Ergebnis", await plugin.ProcessAsync("Rewrite", "Hallo", "", default));
        using var body = JsonDocument.Parse(sent!);
        Assert.Equal("gemini-pro-latest", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(GeminiTranscriptionMode.Verbatim, plugin.TranscriptionMode);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(plugin.TextSettings));
        await plugin.SetApiKeyAsync(""); Assert.False(plugin.IsConfigured); Assert.Empty(host.Secrets);
    }

    [Fact]
    public async Task ProviderDefaultTemperature_IsOmittedAndExplicitWorkflowModelWins()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var http = new HttpClient(new CapturingHandler((_, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            Assert.False(json.RootElement.TryGetProperty("temperature", out var unused));
            Assert.Equal("gemini-pro-latest", json.RootElement.GetProperty("model").GetString());
            return JsonResponse("""{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""");
        }));
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        await plugin.ProcessAsync("", "text", "gemini-pro-latest", default);
    }

    [Theory]
    [InlineData("llmTemperatureValue", "NaN")]
    [InlineData("llmTemperatureValue", "-0.1")]
    [InlineData("llmTemperatureValue", "2.1")]
    [InlineData("llmTemperatureMode", "random")]
    [InlineData("selectedLlmModel", "unknown")]
    [InlineData("transcriptionMode", "unknown")]
    public async Task InvalidSettings_DoNotWrite(string id, string value)
    {
        var host = new TestPluginHostServices(); using var plugin = new GeminiPlugin(); await plugin.ActivateAsync(host);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync(id, value, default));
        Assert.Equal(0, host.SetSettingCount);
    }

    [Theory]
    [InlineData("llmTemperatureValue", "0.8")]
    [InlineData("transcriptionMode", "verbatim")]
    public async Task FailedSettingSave_RetainsActiveValue(string id, string value)
    {
        var host = new TestPluginHostServices(); using var plugin = new GeminiPlugin(); await plugin.ActivateAsync(host);
        var before = plugin.TextSettings.Single(s => s.Id == id).Value;
        host.SetSettingException = new IOException("disk unavailable");
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync(id, value, default));
        Assert.Equal(before, plugin.TextSettings.Single(s => s.Id == id).Value);
    }

    [Fact]
    public async Task CanceledSettingsAndInvalidKey_DoNotWrite()
    {
        var host = new TestPluginHostServices(); using var plugin = new GeminiPlugin(); await plugin.ActivateAsync(host);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.SaveTextSettingAsync("transcriptionMode", "verbatim", new(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SetApiKeyAsync("bad\r\nkey"));
        Assert.Equal(0, host.SetSettingCount); Assert.Empty(host.Secrets);
    }

    [Theory]
    [InlineData("{\"output_text\":\" Hallo \"}")]
    [InlineData("{\"outputs\":[{\"content\":[{\"text\":\"Hallo\"}]}]}")]
    [InlineData("{\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"Hallo\"}]}]}")]
    public void Transcription_AcceptsMacResponseVariants(string json) => Assert.Equal("Hallo", GeminiTranscriptionClient.ParseInteractionText(json));

    [Theory]
    [InlineData("{\"status\":\"incomplete\",\"output_text\":\"partial\"}")]
    [InlineData("[]")]
    [InlineData("{\"outputs\":[5]}")]
    public void Transcription_RejectsIncompleteOrMalformedOutput(string json) =>
        Assert.Throws<PluginRequestException>(() => GeminiTranscriptionClient.ParseInteractionText(json));

    [Fact]
    public async Task Refresh_EmptyOrIncompleteCatalogPreservesSavedModels()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel> { new("gemini-custom-flash", "Custom") });
        using var http = new HttpClient(new CapturingHandler((_, _) => JsonResponse("""{"models":[]}""")));
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host); host.ResetTracking();
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
        Assert.Equal("gemini-custom-flash", Assert.Single(plugin.SupportedModels).Id); Assert.Equal(0, host.SetSettingCount);
    }

    [Fact]
    public async Task LiveStart_PropagatesLanguageHintsVocabularyAndModeThroughHostContract()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var http = new HttpClient();
        using var plugin = new GeminiPlugin(http, (key, model, languages, vocabulary, mode, ct) =>
        {
            Assert.Equal("fixture", key); Assert.Equal(GeminiPlugin.DefaultLiveTranscriptionModel, model);
            Assert.Equal(new[] { "de-DE", "en-GB", "cmn-Hans-CN" }, languages);
            Assert.Equal(new[] { "TypeWhisper", "Gemini" }, vocabulary); Assert.Equal(GeminiTranscriptionMode.Verbatim, mode);
            return Task.FromResult<IStreamingSession>(new StubStream());
        });
        await plugin.ActivateAsync(host); await plugin.SaveTextSettingAsync("transcriptionMode", "verbatim", default);
        ITranscriptionEnginePlugin engine = plugin;
        await using var session = await engine.StartStreamingWithLanguageHintsAndPromptAsync(["de", "de-DE", "en_GB", "zh", "auto"], "TypeWhisper, Gemini", default);
    }

    private sealed class StubStream : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent>? TranscriptReceived { add { } remove { } }
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TranscriptionError_StillDeletesUploadedAudio()
    {
        var deleted = false;
        using var http = new HttpClient(new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) { deleted = true; return new(HttpStatusCode.NoContent); }
            if (request.RequestUri!.AbsolutePath == "/upload/v1beta/files")
            {
                var response = JsonResponse("{}"); response.Headers.Add("X-Goog-Upload-URL", "https://generativelanguage.googleapis.com/upload/session"); return response;
            }
            if (request.RequestUri.AbsolutePath == "/upload/session")
                return JsonResponse("""{"file":{"name":"files/test","uri":"https://generativelanguage.googleapis.com/v1beta/files/test"}}""");
            return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        }));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(CreatePcm16Wav(), "de", false, null, default));
        Assert.Equal(PluginRequestFailureKind.RateLimit, failure.FailureKind); Assert.True(deleted);
    }
}
