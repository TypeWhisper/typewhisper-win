using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK;

public partial class AssemblyAiTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static AssemblyAiPlugin Plugin(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        TimeSpan? poll = null, TimeSpan? timeout = null) => new(new HttpClient(new Handler(handler)), poll ?? TimeSpan.Zero, timeout);
    private static TestPluginHostServices Host()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture-key"; return host;
    }

    [Theory]
    [InlineData(null, "universal-3-5-pro", 18)]
    [InlineData("universal-3-pro", "universal-3-5-pro", 18)]
    [InlineData("stale", "universal-3-5-pro", 18)]
    [InlineData("universal-2", "universal-2", 37)]
    public async Task ActivationResolvesMacCatalogAndReadsOnlyHostSettings(string? stored, string expected, int languages)
    {
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network call"));
        var host = Host(); host.SetSetting("selectedModel", stored);
        await plugin.ActivateAsync(host);
        Assert.Equal(expected, plugin.SelectedModelId); Assert.Equal(languages, plugin.SupportedLanguages.Count);
        Assert.True(plugin.IsConfigured); Assert.True(plugin.SupportsDictionaryTerms); Assert.True(plugin.SupportsStreamingCompletion);
        Assert.False(plugin.SupportsTranslation); Assert.Equal(3, plugin.TextSettings.Count); Assert.Single(plugin.SettingsActions);
        await plugin.DeactivateAsync(); Assert.False(plugin.IsConfigured);
    }

    [Theory]
    [InlineData("universal-3-5-pro", null, "keyterms_prompt")]
    [InlineData("universal-2", "de", "word_boost")]
    [InlineData("universal-3-5-pro", " auto ", "keyterms_prompt")]
    public async Task UploadSubmitPollPreservesAuthAudioDictionaryAndTimestamps(string model, string? language, string dictionaryField)
    {
        var calls = 0;
        var audio = new byte[] { 82, 73, 70, 70, 1, 2, 3, 4 };
        using var plugin = Plugin(async (request, ct) =>
        {
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.Equal("api.assemblyai.com", request.RequestUri!.Host);
            switch (++calls)
            {
                case 1:
                    Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/v2/upload", request.RequestUri.AbsolutePath);
                    Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                    Assert.Equal(audio, await request.Content.ReadAsByteArrayAsync(ct));
                    return Json("""{"upload_url":"https://cdn.assemblyai.com/upload/fixture"}""");
                case 2:
                    Assert.Equal("/v2/transcript", request.RequestUri.AbsolutePath);
                    var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal(model, body.GetProperty("speech_models")[0].GetString());
                    Assert.Equal(new[] { "Grüße", "TypeWhisper" }, body.GetProperty(dictionaryField).EnumerateArray().Select(e => e.GetString()));
                    Assert.False(body.TryGetProperty("speaker_labels", out _));
                    if (language == "de") Assert.Equal("de", body.GetProperty("language_code").GetString());
                    else Assert.True(body.GetProperty("language_detection").GetBoolean());
                    if (model == "universal-2") Assert.Equal("high", body.GetProperty("boost_param").GetString());
                    return Json("""{"id":"fixture-id"}""");
                case 3:
                    Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/v2/transcript/fixture-id", request.RequestUri.AbsolutePath);
                    return Json("""{"status":"processing"}""");
                default: return Json("""{"status":"completed","text":"Grüße TypeWhisper!","language_code":"de","audio_duration":2.5,"words":[{"text":"Grüße","start":100,"end":600},{"text":"TypeWhisper!","start":700,"end":1500}]}""");
            }
        });
        await plugin.ActivateAsync(Host()); plugin.SelectModel(model);
        var result = await plugin.TranscribeAsync(audio, language, false, "Grüße, TypeWhisper, grüße", default);
        Assert.Equal(4, calls); Assert.Equal("Grüße TypeWhisper!", result.Text); Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds); Assert.Equal(0.1, result.Segments[0].Start); Assert.Equal(1.5, result.Segments[1].End);
        Assert.Null(result.NoSpeechProbability);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)] [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(408, PluginRequestFailureKind.Timeout)] [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)] [InlineData(500, PluginRequestFailureKind.ServerError)]
    [InlineData(400, PluginRequestFailureKind.InvalidRequest)] [InlineData(302, PluginRequestFailureKind.InvalidRequest)]
    public async Task HttpFailuresAreClassifiedWithoutResponseOrKeyLeakage(int code, PluginRequestFailureKind kind)
    {
        using var plugin = Plugin((_, _) => Task.FromResult(Json("private recording and fixture-key", (HttpStatusCode)code)));
        await plugin.ActivateAsync(Host());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync([1, 2], null, false, null, default));
        Assert.Equal(kind, error.FailureKind); Assert.Equal(code, error.HttpStatusCode);
        Assert.DoesNotContain("private", error.ToString()); Assert.DoesNotContain("fixture-key", error.ToString());
    }

    [Theory]
    [InlineData("bad json")] [InlineData("[]")] [InlineData("{}")]
    [InlineData("{\"upload_url\":\"http://untrusted.invalid/audio\"}")]
    [InlineData("{\"upload_url\":42}")]
    public async Task MalformedUploadStopsBeforeSubmission(string payload)
    {
        var calls = 0;
        using var plugin = Plugin((_, _) => { calls++; return Task.FromResult(Json(payload)); });
        await plugin.ActivateAsync(Host());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync([1, 2], null, false, null, default));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"status\":\"error\",\"error\":\"private recording\"}", PluginRequestFailureKind.InvalidRequest)]
    [InlineData("{\"status\":\"alien\"}", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{\"status\":\"completed\"}", PluginRequestFailureKind.OutputIncomplete)]
    public async Task PollFailureNeverReturnsPartialSuccess(string final, PluginRequestFailureKind kind)
    {
        var calls = 0;
        using var plugin = Plugin((_, _) => Task.FromResult(Json(++calls == 1 ? "{\"upload_url\":\"https://cdn.assemblyai.com/x\"}" : calls == 2 ? "{\"id\":\"x\"}" : final)));
        await plugin.ActivateAsync(Host());
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync([1, 2], null, false, null, default));
        Assert.Equal(kind, ex.FailureKind); Assert.DoesNotContain("private", ex.Message);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task PollDeadlineAndUserCancellationAreDistinct(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var plugin = Plugin((_, _) =>
        {
            if (++calls == 1) return Task.FromResult(Json("""{"upload_url":"https://cdn.assemblyai.com/x"}"""));
            submitted.TrySetResult(); return Task.FromResult(Json("""{"id":"x"}"""));
        }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        await plugin.ActivateAsync(Host());
        var request = plugin.TranscribeAsync([1, 2], null, false, null, cts.Token);
        await submitted.Task;
        if (cancel) { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); }
        else Assert.Equal(PluginRequestFailureKind.Timeout, (await Assert.ThrowsAsync<PluginRequestException>(() => request)).FailureKind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidOptionsAndCancellationDoNotUpload()
    {
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected upload"));
        await plugin.ActivateAsync(Host());
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribeAsync([1, 2], null, true, null, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribeAsync([1, 2], "pl", false, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribeAsync([], null, false, null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribeAsync([1, 2], null, false, null, new(true)));
    }

    [Fact]
    public async Task SettingsAndKeyCommitTogetherSurviveRestartAndRemove()
    {
        var host = Host();
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network"));
        await plugin.ActivateAsync(host);
        await plugin.SaveProfileSettingsAsync("assemblyai", new Dictionary<string, string> { ["selectedModel"] = "universal-2", ["speakerDiarizationEnabled"] = "true" }, " replacement-key ", default);
        Assert.False(plugin.SupportsStreaming); Assert.Equal("universal-2", plugin.SelectedModelId);
        Assert.Equal("replacement-key", Assert.Single(host.Secrets).Value);
        using var restart = Plugin((_, _) => Task.FromResult(Json("{\"transcripts\":[]}")));
        await restart.ActivateAsync(host); Assert.True(restart.IsConfigured); Assert.False(restart.SupportsStreaming);
        await restart.SetApiKeyAsync(""); Assert.Empty(host.Secrets); Assert.False(restart.IsConfigured);
        await restart.DeactivateAsync(); await restart.ActivateAsync(host); Assert.False(restart.IsConfigured);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FailedSavePreservesPreviousConfigurationAndSecret(bool secretFailure)
    {
        var host = Host(); using var plugin = Plugin((_, _) => Task.FromResult(Json("{\"transcripts\":[]}")));
        await plugin.ActivateAsync(host); host.FailWrites = secretFailure; host.FailSettings = !secretFailure;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync("assemblyai",
            new Dictionary<string, string> { ["selectedModel"] = "universal-2", ["speakerDiarizationEnabled"] = "true" }, "replacement-key", default));
        Assert.True(plugin.SupportsStreaming); Assert.Equal(AssemblyAiModels.DefaultId, plugin.SelectedModelId);
        Assert.Equal("fixture-key", Assert.Single(host.Secrets).Value); Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task DraftValidationUsesEnteredKeyWithoutSavingOrUploading()
    {
        var host = Host();
        using var plugin = Plugin((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.EndsWith("/v2/transcript?limit=1", request.RequestUri!.AbsoluteUri);
            Assert.Equal("draft-key", Assert.Single(request.Headers.GetValues("Authorization")));
            return Task.FromResult(Json("{\"transcripts\":[]}"));
        });
        await plugin.ActivateAsync(host);
        var result = await plugin.ExecuteProfileActionAsync("assemblyai", "checkConnection", new Dictionary<string, string>(), "draft-key", default);
        Assert.False(result.HasPendingChanges); Assert.Equal(0, host.SettingWrites); Assert.Equal("fixture-key", Assert.Single(host.Secrets).Value);
    }

    [Fact]
    public void DiarizationPreservesLabelsAndConvertsOnlyValidTimestamps()
    {
        using var doc = JsonDocument.Parse("""{"text":"Hello there.","audio_duration":4,"language_code":"en","utterances":[{"text":"Hello.","start":100,"end":500,"speaker":"A","confidence":0.9},{"text":"There.","start":600,"end":1500,"speaker":2},{"text":"Unlabeled.","start":1600,"end":2000,"speaker":"unknown"},{"text":"Invalid.","start":2000,"end":1000}]}""");
        var result = AssemblyAiPlugin.ParseCompleted(doc.RootElement, null, true);
        Assert.Equal("Speaker A: Hello.\nSpeaker 2: There.\nUnlabeled.", result.Text);
        Assert.Equal(3, result.Segments.Count); Assert.Equal(0.1, result.Segments[0].Start); Assert.Equal(1.5, result.Segments[1].End);
        Assert.Equal(4, result.DurationSeconds);
    }

    [Fact]
    public void CompletedSilenceIsValidButMalformedTextIsNot()
    {
        using var silent = JsonDocument.Parse("{\"text\":null,\"audio_duration\":1}");
        Assert.Empty(AssemblyAiPlugin.ParseCompleted(silent.RootElement, "de", false).Text);
        using var malformed = JsonDocument.Parse("{\"text\":42}");
        Assert.Throws<PluginRequestException>(() => AssemblyAiPlugin.ParseCompleted(malformed.RootElement, null, false));
    }

    [Fact]
    public async Task DictionaryBudgetsAndStreamingFallbackMatchModelContracts()
    {
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network")); await plugin.ActivateAsync(Host());
        var terms = string.Join(",", Enumerable.Range(0, 1100).Select(i => "term" + i));
        Assert.Equal(1000, AssemblyAiModels.Terms(terms, AssemblyAiModels.All[0]).Count);
        Assert.False(plugin.SupportsStreamingForPrompt(terms)); Assert.False(plugin.SupportsStreamingForPrompt(new string('a', 51)));
        Assert.Empty(AssemblyAiModels.Terms("one two three four five six seven", AssemblyAiModels.All[0]));
        plugin.SelectModel("universal-2"); Assert.Equal(100, plugin.DictionaryTermsBudget.MaxTerms);
        Assert.Equal(100, AssemblyAiModels.Terms(terms, AssemblyAiModels.All[1]).Count);
        Assert.True(plugin.SupportsStreamingForPrompt("TypeWhisper, Grüße"));
    }
}
