using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public partial class AssemblyAiTests
{
    private static async Task<PluginTranscriptionResult> Dictate(AssemblyAiPlugin plugin, byte[] audio, string? language = null, string? prompt = null)
    {
        using (TranscriptionRequestContext.Begin(TranscriptionPurpose.Dictation))
            return await plugin.TranscribeAsync(audio, language, false, prompt, default);
    }

    private static Task DictationMode(AssemblyAiPlugin plugin) => plugin.SaveTextSettingAsync("transcriptionMode", "dictation", default);

    [Fact]
    public async Task DictationModeSendsDictationsToTheDictationApiAndReturnsTheCleanedText()
    {
        var audio = Wav(2.5);
        var calls = 0;
        using var plugin = Plugin(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://dictation.assemblyai.com/v1/transcribe", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("Authorization")));
            // The Dictation API always runs Universal-3.6 Pro and takes no model header.
            Assert.False(request.Headers.Contains("X-AAI-Model"));
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("audio", parts[0].Headers.ContentDisposition!.Name!.Trim('"')); Assert.Equal(audio, await parts[0].ReadAsByteArrayAsync(ct));
            // It rejects unknown fields such as timestamps with a 400.
            var config = JsonSerializer.Deserialize<JsonElement>(await parts[1].ReadAsStringAsync(ct));
            Assert.Equal(new[] { "language_codes", "keyterms_prompt" }, config.EnumerateObject().Select(p => p.Name));
            Assert.Equal("de", Assert.Single(config.GetProperty("language_codes").EnumerateArray()).GetString());
            Assert.Equal(new[] { "Grüße", "TypeWhisper" }, config.GetProperty("keyterms_prompt").EnumerateArray().Select(e => e.GetString()));
            return Json("""{"text":"Ähm, Grüße an äh TypeWhisper","words":[{"text":"Ähm,","confidence":0.5}],"llm_response":" Grüße an TypeWhisper! ","audio_duration_ms":2500}""");
        });
        await plugin.ActivateAsync(Host()); await DictationMode(plugin);
        var result = await Dictate(plugin, audio, "de", "Grüße, TypeWhisper");
        Assert.Equal(1, calls); Assert.Equal("Grüße an TypeWhisper!", result.Text);
        Assert.Equal("de", result.DetectedLanguage); Assert.Equal(2.5, result.DurationSeconds); Assert.Empty(result.Segments);
    }

    [Theory]
    [InlineData("""{"text":"Hallo zusammen","llm_response":null,"llm_error":"timeout"}""")]
    [InlineData("""{"text":"Hallo zusammen","llm_response":"  "}""")]
    [InlineData("""{"text":"Hallo zusammen"}""")]
    public async Task DictationModeReturnsTheTranscriptWhenTheRewriteFails(string response)
    {
        using var plugin = Plugin((_, _) => Task.FromResult(Json(response)));
        await plugin.ActivateAsync(Host()); await DictationMode(plugin);
        Assert.Equal("Hallo zusammen", (await Dictate(plugin, Wav(1))).Text);
    }

    [Fact]
    public async Task DictationModeKeepsRecordingsFilesAndTrainingVerbatimOnTheSyncApi()
    {
        var hosts = new List<string>();
        using var plugin = Plugin(async (request, ct) =>
        {
            hosts.Add(request.RequestUri!.Host);
            Assert.Equal("universal-3-6-pro", Assert.Single(request.Headers.GetValues("X-AAI-Model")));
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("""{"timestamps":true}""", await parts[1].ReadAsStringAsync(ct));
            return Json("""{"text":"Ähm, Hallo","words":[{"text":"Ähm,","start":0,"end":300},{"text":"Hallo","start":400,"end":800}]}""");
        });
        await plugin.ActivateAsync(Host()); await DictationMode(plugin);
        // Without a purpose (a file, a recording, the HTTP API or dictionary training) and with an explicit Verbatim.
        var file = await plugin.TranscribeAsync(Wav(1), null, false, null, default);
        using (TranscriptionRequestContext.Begin(TranscriptionPurpose.Verbatim))
            Assert.Equal("Ähm, Hallo", (await plugin.TranscribeAsync(Wav(1), null, false, null, default)).Text);
        Assert.Equal("Ähm, Hallo", file.Text); Assert.Equal(2, file.Segments.Count);
        Assert.Equal(new[] { "sync.assemblyai.com", "sync.assemblyai.com" }, hosts);
    }

    [Theory]
    [InlineData("live")] [InlineData("sync")]
    public async Task OtherModesSendDictationsToTheSyncApi(string mode)
    {
        var hosts = new List<string>();
        using var plugin = Plugin((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(Json("""{"text":"Hallo","llm_response":"Hallo!"}"""));
        });
        await plugin.ActivateAsync(Host()); await plugin.SaveTextSettingAsync("transcriptionMode", mode, default);
        Assert.Equal("Hallo", (await Dictate(plugin, Wav(1))).Text);
        Assert.Equal("sync.assemblyai.com", Assert.Single(hosts));
    }

    [Theory]
    [InlineData(500)] [InlineData(502)] [InlineData(415)] [InlineData(400)] [InlineData(0)]
    public async Task FailedDictationFallsBackToTheAsyncApiWithoutCleanupAndIsNotSentAgain(int failure)
    {
        var paths = new List<string>();
        using var plugin = Plugin(async (request, ct) =>
        {
            paths.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            if (request.RequestUri.Host == "dictation.assemblyai.com")
                return failure == 0 ? throw new HttpRequestException("connection reset") : Json("""{"error":"private","error_code":"x"}""", (HttpStatusCode)failure);
            if (request.RequestUri.AbsolutePath == "/v2/upload") return Json("""{"upload_url":"https://cdn.assemblyai.com/upload/fixture"}""");
            if (request.Method == HttpMethod.Post)
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                Assert.False(body.TryGetProperty("speaker_labels", out _)); Assert.False(body.GetProperty("disfluencies").GetBoolean());
                return Json("""{"id":"fixture-id"}""");
            }
            return Json("""{"status":"completed","text":"Hallo","language_code":"de"}""");
        });
        await plugin.ActivateAsync(Host()); await DictationMode(plugin);
        var audio = Wav(1);
        Assert.Equal("Hallo", (await Dictate(plugin, audio)).Text);
        // The host's retry of the same dictation goes straight to the async API: AssemblyAI may have billed the first request.
        Assert.Equal("Hallo", (await Dictate(plugin, audio)).Text);
        Assert.Equal(new[]
        {
            "dictation.assemblyai.com/v1/transcribe", "api.assemblyai.com/v2/upload", "api.assemblyai.com/v2/transcript", "api.assemblyai.com/v2/transcript/fixture-id",
            "api.assemblyai.com/v2/upload", "api.assemblyai.com/v2/transcript", "api.assemblyai.com/v2/transcript/fixture-id"
        }, paths);
    }

    [Theory]
    [InlineData("assemblyai-stt-3-7", 1201, 0)] [InlineData("assemblyai-stt-3-7", 1, 101)] [InlineData("universal-2", 1, 0)]
    public async Task DictationsBeyondTheDictationApiLimitsUseTheAsyncApi(string model, int seconds, int termCount)
    {
        var hosts = new List<string>();
        using var plugin = Plugin((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/v2/upload" => Json("""{"upload_url":"https://cdn.assemblyai.com/upload/fixture"}"""),
                "/v2/transcript" => Json("""{"id":"fixture-id"}"""),
                _ => Json("""{"status":"completed","text":"Hallo"}""")
            });
        });
        await plugin.ActivateAsync(Host()); await DictationMode(plugin); plugin.SelectModel(model);
        var prompt = termCount > 0 ? string.Join(",", Enumerable.Range(0, termCount).Select(i => "term" + i)) : null;
        // Over 20 minutes, over 100 dictionary terms, or a model without the Dictation API.
        Assert.Equal("Hallo", (await Dictate(plugin, Wav(seconds), null, prompt)).Text);
        Assert.DoesNotContain("dictation.assemblyai.com", hosts); Assert.DoesNotContain("sync.assemblyai.com", hosts);
    }

    [Fact]
    public async Task DictationsLeaveSpeakerLabelsOutAndRecordingsKeepThem()
    {
        var bodies = new List<JsonElement>();
        var hosts = new List<string>();
        using var plugin = Plugin(async (request, ct) =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host != "api.assemblyai.com") return Json("""{"text":"Hallo"}""");
            if (request.RequestUri.AbsolutePath == "/v2/upload") return Json("""{"upload_url":"https://cdn.assemblyai.com/upload/fixture"}""");
            if (request.Method == HttpMethod.Post) { bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct))); return Json("""{"id":"x"}"""); }
            return Json("""{"status":"completed","text":"Hallo","utterances":[{"text":"Hallo","start":0,"end":500,"speaker":"A"}]}""");
        });
        await plugin.ActivateAsync(Host());
        await plugin.SaveTextSettingAsync("speakerDiarizationEnabled", "true", default);
        // Speaker diarization turns live text off, as before; a dictation then goes to the Sync API without labels.
        Assert.False(plugin.SupportsStreaming);
        Assert.Equal("Hallo", (await Dictate(plugin, Wav(1))).Text);
        Assert.Equal("sync.assemblyai.com", Assert.Single(hosts));
        Assert.Equal("Speaker A: Hallo", (await plugin.TranscribeAsync(Wav(1), null, false, null, default)).Text);
        Assert.True(Assert.Single(bodies).GetProperty("speaker_labels").GetBoolean());
    }

    [Theory]
    [InlineData("Dictation", 5, 0, 35)]
    [InlineData("Dictation", 60, 0, 90)]
    [InlineData("Dictation", null, 0, 90)]
    [InlineData("Dictation", 600, 2_000_000, 92)]
    [InlineData("Sync", 5, 0, 45)]
    [InlineData("Sync", 600, 3_000_000, 48)]
    public void DictationRequestsGetThirtySecondsPlusTheirLengthUpToNinety(string endpoint, int? seconds, long bytes, double expected)
    {
        var timeout = AssemblyAiPlugin.SyncTimeout(Enum.Parse<AssemblyAiPlugin.SyncEndpoint>(endpoint), seconds is { } s ? TimeSpan.FromSeconds(s) : null, bytes);
        Assert.Equal(expected, timeout.TotalSeconds);
    }

    [Fact]
    public async Task SavedSelectionsOfRetiredModelsResolveForWorkflowsAndRequests()
    {
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network"));
        await plugin.ActivateAsync(Host());
        var stt37 = plugin.TranscriptionModels.Single(m => m.Id == "assemblyai-stt-3-7");
        Assert.Equal(new[] { "universal-3-5-pro", "universal-3-pro" }, stt37.FormerIds);
        Assert.Empty(plugin.TranscriptionModels.Single(m => m.Id == "universal-2").FormerIds);
        plugin.SelectModel("universal-2"); plugin.SelectModel("universal-3-5-pro");
        Assert.Equal("assemblyai-stt-3-7", plugin.SelectedModelId);
        Assert.Throws<ArgumentException>(() => plugin.SelectModel("universal-3-6-pro"));
    }
}
