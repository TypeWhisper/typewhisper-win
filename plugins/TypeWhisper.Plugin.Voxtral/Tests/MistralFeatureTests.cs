using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed partial class ProviderTests
{
    private const string MiniTranscribe = "voxtral-mini-transcribe-3-0";
    private static readonly string[] DefaultTextModels =
        ["mistral-large-4", "mistral-medium-2604", "mistral-small-2603", "ministral-14b-2512", "ministral-8b-2512", "ministral-3b-2512"];

    // Shape and groups of the live /v1/models response on 2026-11-06, trimmed to one entry per case.
    private const string LiveCatalog = """
        {"data":[
          {"id":"voxtral-mini-2602","name":"Voxtral Mini Transcribe","aliases":["voxtral-mini-latest"],
           "capabilities":{"audio_transcription":true},"audio_transcription_capabilities":{"context_bias":true,"languages":["de","en"]}},
          {"id":"voxtral-mini-latest","name":"Voxtral Mini Transcribe","aliases":["voxtral-mini-2602"],
           "capabilities":{"audio_transcription":true},"audio_transcription_capabilities":{"context_bias":true,"languages":["de","en"]}},
          {"id":"voxtral-mini-transcribe-3","name":"Voxtral Mini Transcribe 3.0","aliases":["voxtral-mini-transcribe-3-0"],
           "capabilities":{"audio_transcription":true},"audio_transcription_capabilities":{"context_bias":true,"languages":["de","en","sv","DE","x1"]}},
          {"id":"voxtral-mini-transcribe-3-0","name":"Voxtral Mini Transcribe 3.0","aliases":["voxtral-mini-transcribe-3"],
           "capabilities":{"audio_transcription":true},"audio_transcription_capabilities":{"context_bias":true,"languages":["de","en","sv","DE","x1"]}},
          {"id":"voxtral-small-transcribe-3-0","name":"Voxtral Small Transcribe 3.0","aliases":["voxtral-small-transcribe-3"],
           "capabilities":{"audio_transcription":true},"audio_transcription_capabilities":{"context_bias":true,"languages":["de"]}},
          {"id":"voxtral-mini-realtime-2602","name":"Voxtral Mini Transcribe Realtime","aliases":["voxtral-mini-transcribe-realtime-2602","voxtral-mini-realtime-latest"],
           "capabilities":{"audio_transcription_realtime":true},"audio_transcription_capabilities":{"context_bias":false,"languages":[]}},
          {"id":"voxtral-mini-transcribe-realtime-3-0","name":"Voxtral Mini Transcribe Realtime 3.0","aliases":["voxtral-mini-transcribe-realtime-3"],
           "capabilities":{"audio_transcription_realtime":true},"audio_transcription_capabilities":{"context_bias":true,"languages":["de","en"]}},
          {"id":"voxtral-small-2507","name":"Voxtral Small Transcribe","aliases":["voxtral-small-latest"],"capabilities":{"completion_chat":true,"audio":true}},
          {"id":"mistral-small-2603","name":"mistral-small-2603","aliases":["mistral-small-latest","mistral-vibe-cli-fast","magistral-small-latest"],
           "capabilities":{"completion_chat":true,"reasoning":true}},
          {"id":"mistral-vibe-cli-fast","name":"mistral-small-2603","aliases":["mistral-small-2603","mistral-small-latest","magistral-small-latest"],
           "capabilities":{"completion_chat":true,"reasoning":true}},
          {"id":"mistral-medium-latest","name":"mistral-medium-latest","aliases":["mistral-medium","mistral-medium-3-5","mistral-medium-2604"],
           "capabilities":{"completion_chat":true,"reasoning":true}},
          {"id":"mistral-large-4","name":"mistral-large-4","aliases":["mistral-large-4-0"],"capabilities":{"completion_chat":true,"reasoning":true}},
          {"id":"ministral-14b-latest","name":"ministral-14b-2512","aliases":["ministral-14b-2512"],"capabilities":{"completion_chat":true}},
          {"id":"codestral-2508","name":"codestral-2508","aliases":["codestral-latest"],"capabilities":{"completion_chat":true,"completion_fim":true}},
          {"id":"labs-leanstral-1-5-1","name":"labs-leanstral-1-5-1","aliases":["labs-leanstral-1-5"],"capabilities":{"completion_chat":true,"reasoning":true}},
          {"id":"future-chat-latest","name":"future-chat-latest","aliases":["future-chat-2701"],"capabilities":{"completion_chat":true}},
          {"id":"voxtral-mini-tts-2603","capabilities":{"audio_speech":true,"completion_chat":true}},
          {"id":"mistral-embed","capabilities":{"completion_chat":false}},
          {"id":"retired-chat","archived":true,"capabilities":{"completion_chat":true}}
        ]}
        """;

    private static List<(string Name, string Value)> FormFields(HttpRequestMessage request) =>
        ((MultipartFormDataContent)request.Content!)
            .Where(part => part.Headers.ContentDisposition?.FileName is null)
            .Select(part => (part.Headers.ContentDisposition!.Name!.Trim('"'), part.ReadAsStringAsync().GetAwaiter().GetResult()))
            .ToList();

    private static async Task<VoxtralPlugin> RefreshedPlugin(Host host, Func<HttpRequestMessage, string?, HttpResponseMessage>? respond = null,
        string catalog = LiveCatalog)
    {
        var http = new HttpClient(new Handler((request, body) =>
            request.RequestUri!.AbsolutePath == "/v1/models" ? Json(catalog) : respond!(request, body)));
        var plugin = new VoxtralPlugin(http);
        await plugin.ActivateAsync(host); await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        return plugin;
    }

    [Fact]
    public async Task RefreshGroupsAliasesNamesKnownModelsAndHidesReplacedAndSpecialModels()
    {
        using var plugin = await RefreshedPlugin(new Host());
        Assert.Equal(new[] { MiniTranscribe, "voxtral-small-transcribe-3-0", VoxtralPlugin.RealtimeModel }, plugin.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(new[] { "Voxtral Mini Transcribe 3", "Voxtral Small Transcribe 3", "Voxtral Mini Transcribe Realtime 3" },
            plugin.TranscriptionModels.Select(m => m.DisplayName));
        Assert.Equal(new[] { "mistral-large-4", "mistral-medium-2604", "mistral-small-2603", "ministral-14b-2512", "future-chat-2701" },
            plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(new[] { "Mistral Large 4", "Mistral Medium 3.5", "Mistral Small 4", "Ministral 3 14B", "future-chat-2701" },
            plugin.SupportedModels.Select(m => m.DisplayName));
        Assert.Equal(MiniTranscribe, plugin.SelectedModelId);
        Assert.Equal("mistral-small-2603", plugin.TextSettings.Single(s => s.Id == "llmModel").Value);
        Assert.Equal(new[] { "de", "en", "sv" }, plugin.SupportedLanguages);
    }

    [Fact]
    public async Task ReplacedModelStaysVisibleWithoutItsSuccessor()
    {
        using var plugin = await RefreshedPlugin(new Host(), catalog: """
            {"data":[{"id":"voxtral-mini-2602","name":"Voxtral Mini Transcribe","aliases":["voxtral-mini-latest"],"capabilities":{"audio_transcription":true}}]}
            """);
        Assert.Equal("voxtral-mini-2602", Assert.Single(plugin.TranscriptionModels).Id);
        Assert.Equal("Voxtral Mini Transcribe", plugin.TranscriptionModels[0].DisplayName);
        Assert.Equal("voxtral-mini-2602", plugin.SelectedModelId);
        Assert.False(plugin.SupportsDictionaryTerms);
        Assert.Empty(plugin.SupportedLanguages);
    }

    [Theory]
    [InlineData("voxtral-mini-latest", "mistral-large-latest", MiniTranscribe, "mistral-large-4")]
    [InlineData("voxtral-mini-realtime-latest", "mistral-vibe-cli-fast", "voxtral-mini-transcribe-realtime-3-0", "mistral-small-2603")]
    [InlineData("voxtral-small-transcribe-3", "pixtral-large-latest", "voxtral-small-transcribe-3-0", "ministral-14b-2512")]
    [InlineData("unknown", "unknown", MiniTranscribe, "mistral-small-2603")]
    public async Task SavedAliasesAndRetiredModelsResolveToListedModels(string savedModel, string savedTextModel, string model, string textModel)
    {
        var host = new Host();
        using (var refreshed = await RefreshedPlugin(host)) { }
        var configuration = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(host.Settings["configuration"])!;
        var values = configuration["Values"].Deserialize<Dictionary<string, string>>()!;
        values["model"] = savedModel; values["llmModel"] = savedTextModel;
        host.Settings["configuration"] = JsonSerializer.SerializeToElement(new { SecretName = configuration["SecretName"].GetString(), Values = values });
        using var plugin = new VoxtralPlugin(new HttpClient(new Handler((_, _) => throw new InvalidOperationException())));
        await plugin.ActivateAsync(host);
        Assert.Equal(model, plugin.SelectedModelId);
        Assert.Equal(textModel, plugin.TextSettings.Single(s => s.Id == "llmModel").Value);
    }

    [Fact]
    public async Task CatalogSavedByEarlierVersionsFallsBackToCurrentModels()
    {
        var host = new Host();
        using (var plugin = new VoxtralPlugin()) { await plugin.ActivateAsync(host); await Configure(plugin); }
        var configuration = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(host.Settings["configuration"])!;
        var old = """{"Transcription":[{"Id":"voxtral-mini-latest","DisplayName":"voxtral-mini-latest"}],"Text":[{"Id":"codestral-latest","DisplayName":"codestral-latest"}],"Realtime":[]}""";
        host.Settings["configuration"] = JsonSerializer.SerializeToElement(new
        {
            SecretName = configuration["SecretName"].GetString(),
            Values = new Dictionary<string, string> { ["modelCatalog"] = old, ["model"] = "voxtral-mini-latest", ["llmModel"] = "mistral-small-latest" }
        });
        using var restarted = new VoxtralPlugin(); await restarted.ActivateAsync(host);
        Assert.Equal(new[] { MiniTranscribe, "voxtral-small-transcribe-3-0", VoxtralPlugin.RealtimeModel }, restarted.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(DefaultTextModels, restarted.SupportedModels.Select(m => m.Id));
        Assert.Equal(MiniTranscribe, restarted.SelectedModelId);
        Assert.Equal("mistral-small-2603", restarted.TextSettings.Single(s => s.Id == "llmModel").Value);
        Assert.Equal(58, restarted.SupportedLanguages.Count);
    }

    [Fact]
    public async Task SettingsAcceptAliasesAndSaveTheListedModel()
    {
        var host = new Host();
        using var plugin = new VoxtralPlugin(); await plugin.ActivateAsync(host); await Configure(plugin);
        await plugin.SaveTextSettingAsync("model", "voxtral-mini-transcribe-realtime-3", default);
        await plugin.SaveTextSettingAsync("llmModel", "mistral-medium-latest", default);
        var values = host.Settings["configuration"].GetProperty("Values");
        Assert.Equal(VoxtralPlugin.RealtimeModel, values.GetProperty("model").GetString());
        Assert.Equal("mistral-medium-2604", values.GetProperty("llmModel").GetString());
        await plugin.SelectModelAsync("voxtral-mini-latest", default);
        Assert.Equal(MiniTranscribe, plugin.SelectedModelId);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("llmModel", "codestral-latest", default));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("de", "de", false)]
    [InlineData("de-DE", "de", false)]
    [InlineData("auto", null, true)]
    [InlineData("he", null, true)]
    public async Task UploadPinsSupportedLanguagesAndOtherwiseAsksForTheDetectedOne(string? language, string? sent, bool returnLanguage)
    {
        var fields = new List<(string Name, string Value)>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            fields.AddRange(FormFields(request));
            return Json(returnLanguage ? """{"text":" Hallo ","language":"de"}""" : """{"text":"Hallo","language":null}""");
        }));
        using var plugin = new VoxtralPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var result = await plugin.TranscribeAsync(Audio(), language, false, null, default);
        Assert.Equal("Hallo", result.Text); Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(sent, fields.Where(f => f.Name == "language").Select(f => f.Value).SingleOrDefault());
        Assert.Equal(returnLanguage, fields.Contains(("return_language", "true")));
        Assert.DoesNotContain(fields, f => f.Name == "context_bias");
    }

    [Fact]
    public async Task DictionaryTermsKeepPhrasesAndRespectTheBudget()
    {
        var fields = new List<(string Name, string Value)>();
        using var http = new HttpClient(new Handler((request, _) => { fields.AddRange(FormFields(request)); return Json("""{"text":"Hallo"}"""); }));
        using var plugin = new VoxtralPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        Assert.True(plugin.SupportsDictionaryTerms); Assert.True(plugin.SupportsStructuredDictionaryTerms);
        Assert.Equal(200, plugin.DictionaryTermsBudget.MaxTerms);
        var terms = new[] { "Saoirse Ronan", "Berlin, Friedrichshain", "TypeWhisper" }.Concat(Enumerable.Range(0, 300).Select(i => $"Term{i}"));
        await plugin.TranscribeAsync(Audio(), "de", false, PluginDictionaryTerms.CreateStructuredPrompt(terms, plugin.DictionaryTermsBudget), default);
        var sent = fields.Where(f => f.Name == "context_bias").Select(f => f.Value).ToArray();
        Assert.Equal(200, sent.Length);
        Assert.Equal(new[] { "Saoirse Ronan", "Berlin, Friedrichshain", "TypeWhisper" }, sent.Take(3));
    }

    [Fact]
    public async Task ModelWithoutContextBiasNeverSendsDictionaryTerms()
    {
        var fields = new List<(string Name, string Value)>();
        using var plugin = await RefreshedPlugin(new Host(), (request, _) => { fields.AddRange(FormFields(request)); return Json("""{"text":"Hallo"}"""); },
            """
            {"data":[{"id":"voxtral-mini-2602","capabilities":{"audio_transcription":true},"audio_transcription_capabilities":{"context_bias":false}},
                     {"id":"voxtral-mini-transcribe-realtime-2602","capabilities":{"audio_transcription_realtime":true}}]}
            """);
        Assert.False(plugin.SupportsDictionaryTerms);
        await plugin.TranscribeAsync(Audio(), null, false, "TypeWhisper", default);
        Assert.DoesNotContain(fields, f => f.Name == "context_bias");
        plugin.SelectModel("voxtral-mini-transcribe-realtime-2602");
        Assert.False(plugin.SupportsStreamingForPrompt("TypeWhisper")); Assert.False(plugin.SupportsStreamingForPrompt(null));
        Assert.True(plugin.SupportsStreamingPreviewForPrompt("TypeWhisper")); Assert.True(plugin.SupportsStreamingPreviewForPrompt(null));
    }

    [Theory]
    [InlineData("mistral-large-4", "low")]
    [InlineData("mistral-small-2603", "none")]
    [InlineData("mistral-medium-latest", "none")]
    [InlineData("ministral-14b-2512", null)]
    public async Task ChatSendsTheFastestReasoningLevelEachModelAccepts(string model, string? effort)
    {
        string? sentModel = null, sentEffort = null;
        using var http = new HttpClient(new Handler((_, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            sentModel = json.RootElement.GetProperty("model").GetString();
            sentEffort = json.RootElement.TryGetProperty("reasoning_effort", out var value) ? value.GetString() : null;
            return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""");
        }));
        using var plugin = new VoxtralPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        Assert.Equal("ok", await plugin.ProcessAsync("Correct", "text", model, default));
        Assert.Equal(model == "mistral-medium-latest" ? "mistral-medium-2604" : model, sentModel);
        Assert.Equal(effort, sentEffort);
    }

    [Fact]
    public async Task ReasoningLevelIsSavedPerModelAndShownOnlyForThatModel()
    {
        var efforts = new List<string?>();
        using var http = new HttpClient(new Handler((_, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            efforts.Add(json.RootElement.TryGetProperty("reasoning_effort", out var value) ? value.GetString() : null);
            return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""");
        }));
        using var plugin = new VoxtralPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var fields = plugin.TextSettings.Where(s => s.Id.StartsWith("reasoningEffort.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "reasoningEffort.mistral-large-4", "reasoningEffort.mistral-medium-2604", "reasoningEffort.mistral-small-2603" },
            fields.Select(f => f.Id));
        var large = fields[0];
        Assert.Equal(new[] { "low", "medium", "high" }, large.Choices.Select(c => c.Value));
        Assert.Equal("Niedrig (am schnellsten)", large.Choices[0].Title);
        Assert.Equal(new[] { "mistral-large-4" }, large.VisibleWhen!.Values); Assert.Equal("llmModel", large.VisibleWhen.SettingId);
        Assert.Equal(new[] { "none", "high" }, fields[2].Choices.Select(c => c.Value));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("reasoningEffort.mistral-large-4", "none", default));
        await plugin.SaveTextSettingAsync("reasoningEffort.mistral-large-4", "high", default);
        await plugin.SaveTextSettingAsync("llmModel", "mistral-large-4", default);
        await plugin.ProcessAsync("Correct", "text", "", default);
        await plugin.ProcessAsync("Correct", "text", "mistral-small-latest", default);
        Assert.Equal(new[] { "high", "none" }, efforts);
        Assert.Equal("high", plugin.TextSettings.Single(s => s.Id == "reasoningEffort.mistral-large-4").Value);
    }

    [Theory]
    [InlineData("""{"object":"error","message":"Context bias item 'Saoirse Ronan' must not contain commas or whitespace","type":"invalid_request_invalid_args","code":"3051"}""",
        "Context bias item 'Saoirse Ronan' must not contain commas or whitespace")]
    [InlineData("""{"object":"error","message":{"detail":[{"type":"assertion_error","msg":"Assertion failed, Cannot do language prediction if language is already set","input":{"audio":"--audio-bytes--","language":"de"}}]}}""",
        "Cannot do language prediction if language is already set")]
    [InlineData("""{"detail":[{"msg":"Invalid language alpha2 code"},{"msg":"Second"}]}""", "Invalid language alpha2 code; Second")]
    [InlineData("""{"error":{"message":"Already readable"}}""", null)]
    [InlineData("<html>Bad gateway</html>", null)]
    public void MistralErrorBodiesBecomeTheirMessage(string body, string? expected) =>
        Assert.Equal(expected, MistralErrorHandler.Message(body));

    [Fact]
    public async Task RejectedUploadShowsMistralsMessageWithoutRequestEcho()
    {
        using var http = new HttpClient(new MistralErrorHandler(new Handler((_, _) => new(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"object":"error","message":{"detail":[{"msg":"Assertion failed, Cannot do language prediction if language is already set","input":{"secret":"private transcript"}}]}}""",
                Encoding.UTF8, "application/json")
        })));
        using var plugin = new VoxtralPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(Audio(), "de", false, null, default));
        Assert.Equal(PluginRequestFailureKind.InvalidRequest, error.FailureKind); Assert.Equal(422, error.HttpStatusCode);
        Assert.Contains("Cannot do language prediction if language is already set", error.Message);
        Assert.DoesNotContain("private", error.Message);
    }
}
