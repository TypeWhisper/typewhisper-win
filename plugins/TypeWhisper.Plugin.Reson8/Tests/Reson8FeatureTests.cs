using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    // GET /v1/model as Reson8 answered it on 2026-11-03.
    private const string ModelListJson = """
        [
          {"id":"resonant-1","displayName":"Resonant-1","endpoints":["prerecorded","realtime","turns"],"languages":["de","en","es","fr","fy","it","nl","pl","pt","sv"],"isDefault":true},
          {"id":"rumble-2-flash","displayName":"Rumble-2-Flash","endpoints":["prerecorded"],"languages":["de","en","ja","nb","zh"],"isDefault":false},
          {"id":"../bad","displayName":"Bad"},
          {"id":"no-name"}
        ]
        """;

    [Fact]
    public void Catalog_ParsesModelsAndTheParametersEachRejects()
    {
        var models = Reson8Catalog.ParseModels(ModelListJson);

        Assert.Equal(["resonant-1", "rumble-2-flash"], models.Select(model => model.Id));
        var rumble = models.Single(model => model.Id == "rumble-2-flash");
        Assert.False(Reson8Catalog.TakesParameter(rumble, "phrases"));
        Assert.False(Reson8Catalog.TakesParameter(rumble, "include_words"));
        Assert.True(Reson8Catalog.TakesParameter(rumble, "language"));
        Assert.True(Reson8Catalog.TakesParameter(models[0], "phrases"));
        Assert.True(Reson8Catalog.TakesParameter(null, "patterns"));
        Assert.Throws<JsonException>(() => Reson8Catalog.ParseModels("""{ "models": [] }"""));
    }

    [Fact]
    public async Task Models_ListDefaultListedCustomAndPatternModelsWithoutTheDictionaryModel()
    {
        var host = CatalogHost();
        host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel("custom-1", "Legal", null, 12), new Reson8CustomModel("dict-1", "TypeWhisper Dictionary", null, 3) });
        host.SetSetting("dictionaryModel", new Reson8DictionaryModelState("dict-1", ["Reson8"]));
        host.SetSetting("patternSets", new[] { new Reson8PatternSet("orders", "Order codes", ["AMZ[0-9]{6}"]) });
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);

        Assert.Equal(["__default__", "rumble-2-flash", "custom-1", "patterns:orders"], sut.TranscriptionModels.Select(model => model.Id));
        Assert.Equal("Resonant-1", sut.TranscriptionModels[0].DisplayName);
        Assert.Equal("Order codes (patterns)", sut.TranscriptionModels[3].DisplayName);
        Assert.Equal(10, sut.TranscriptionModels[0].LanguageCount);
        Assert.Equal(5, sut.TranscriptionModels[1].LanguageCount);
    }

    [Fact]
    public async Task SelectingRumble_TurnsOffStreamingAndDictionaryAndNotifiesTheHost()
    {
        var host = CatalogHost();
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);
        Assert.True(sut.SupportsStreaming);
        Assert.True(sut.SupportsDictionaryTerms);
        Assert.True(sut.SupportsLanguageHints);
        Assert.Contains("fy", sut.SupportedLanguages);

        sut.SelectModel("rumble-2-flash");

        Assert.False(sut.SupportsStreaming);
        Assert.False(sut.SupportsDictionaryTerms);
        Assert.Equal(["de", "en", "ja", "nb", "zh"], sut.SupportedLanguages);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        await Assert.ThrowsAsync<NotSupportedException>(() => sut.StartStreamingWithLanguageHintsAndPromptAsync(["de"], null, default));
    }

    [Fact]
    public async Task Query_SendsLanguageListTimingsFillerModeAndBiasWithPhrases()
    {
        var host = CatalogHost();
        host.SetSetting("fillerMode", "clean");
        host.SetSetting("biasStrength", 0.6);
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(["C++", "Soniox, Inc.", "TypeWhisper"]);

        var query = Parse(sut.BuildQuery(Reson8Plugin.ResolveLanguage(["nl", "en"]), prompt, null, realtime: false).ToString());

        Assert.Equal("nl,en", query["language"]);
        Assert.Equal("true", query["include_language"]);
        Assert.Equal("true", query["include_words"]);
        Assert.Equal("true", query["include_timestamps"]);
        Assert.Equal("clean", query["filler_mode"]);
        Assert.Equal("C++,TypeWhisper", query["phrases"]);
        Assert.Equal("0.60", query["bias_strength"]);
        Assert.Null(query["model"]);
        Assert.Null(query["custom_model_id"]);
        Assert.Null(query["include_interim"]);
        Assert.Contains("phrases=C%2B%2B,TypeWhisper", sut.BuildQuery(null, prompt, null, realtime: true).ToString());
    }

    [Fact]
    public async Task Query_ForRumbleSendsTheModelAndLeavesOutWhatItRejects()
    {
        var host = CatalogHost();
        host.SetSetting("selectedModel", "rumble-2-flash");
        host.SetSetting("fillerMode", "verbatim");
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);

        var query = Parse(sut.BuildQuery("de", PluginDictionaryTerms.CreateStructuredPrompt(["TypeWhisper"]), null, realtime: false).ToString());

        Assert.Equal("rumble-2-flash", query["model"]);
        Assert.Null(query["phrases"]);
        Assert.Null(query["filler_mode"]);
        Assert.Null(query["include_words"]);
        Assert.Null(query["bias_strength"]);
    }

    [Fact]
    public async Task Query_ForAPatternSetSendsPatternsInsteadOfPhrases()
    {
        var host = CatalogHost();
        host.SetSetting("patternSets", new[] { new Reson8PatternSet("orders", "Order codes", ["AMZ[0-9]{6}", "ORD-[0-9]{4,6}"]) });
        host.SetSetting("selectedModel", "patterns:orders");
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);

        var query = Parse(sut.BuildQuery(null, PluginDictionaryTerms.CreateStructuredPrompt(["TypeWhisper"]), null, realtime: true).ToString());

        Assert.Equal("AMZ[0-9]{6},ORD-[0-9]{4,6}", query["patterns"]);
        Assert.Null(query["phrases"]);
        Assert.Null(query["custom_model_id"]);
        Assert.Null(query["model"]);
        Assert.Equal("true", query["include_interim"]);
        Assert.False(sut.SupportsDictionaryTerms);
    }

    [Fact]
    public async Task Query_KeepsPhrasesWithinTheUrlBudget()
    {
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(CatalogHost());
        var terms = Enumerable.Range(0, 250).Select(index => $"Begriff-{index:000}-äöü").ToArray();

        var phrases = Parse(sut.BuildQuery(null, PluginDictionaryTerms.CreateStructuredPrompt(terms, new(MaxTotalChars: 100_000)), null, false).ToString())["phrases"]!;

        Assert.True(Reson8Query.Escape(phrases).Length <= Reson8Plugin.MaximumEncodedPhrasesLength);
        Assert.StartsWith("Begriff-000-äöü,Begriff-001-äöü", phrases);
        Assert.True(phrases.Split(',').Length < 250);
    }

    [Fact]
    public void Transcript_ReadsWordsIntoTimingsAndSentenceSegments()
    {
        const string json = """
            {"text":"Die Rechnung beträgt 1234 Euro. Danke dir sehr.","language":"de","words":[
              {"text":"Die","start_ms":1200,"duration_ms":160},{"text":"Rechnung","start_ms":1360,"duration_ms":400},
              {"text":"beträgt","start_ms":1840,"duration_ms":400},{"text":"1234","start_ms":2800,"duration_ms":1040},
              {"text":"Euro.","start_ms":4240,"duration_ms":160},{"text":"Danke","start_ms":4600,"duration_ms":200},
              {"text":"dir","start_ms":4800,"duration_ms":100},{"text":"sehr.","start_ms":4900,"duration_ms":300}]}
            """;

        var result = Reson8Plugin.ParseTranscriptionResponse(json, "de,en", 32_000 * 6);

        Assert.Equal("Die Rechnung beträgt 1234 Euro. Danke dir sehr.", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(8, result.TokenTimings.Count);
        Assert.Equal((1.2, 1.36), (result.TokenTimings[0].StartSeconds, result.TokenTimings[0].EndSeconds));
        Assert.Equal(["Die Rechnung beträgt 1234 Euro.", "Danke dir sehr."], result.Segments.Select(segment => segment.Text));
        Assert.Equal((1.2, 4.4), (result.Segments[0].Start, result.Segments[0].End), new ToleranceComparer());
    }

    [Theory]
    [InlineData("", "de,en", null)]
    [InlineData("", "de", "de")]
    [InlineData("en", "de,en", "en")]
    [InlineData(null, null, null)]
    public void Transcript_ReportsTheDetectedOrTheSinglePinnedLanguage(string? detected, string? requested, string? expected) =>
        Assert.Equal(expected, Reson8Transcript.DetectedLanguage(detected, requested));

    [Fact]
    public async Task Transcribe_SendsLanguageHintsAndSplitsLongRecordings()
    {
        string? query = null;
        var handler = new CapturingHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return JsonResponse("""{"text":"Hallo","language":"nl"}""");
        });
        using var sut = new Reson8Plugin(new HttpClient(handler));
        await sut.ActivateAsync(CatalogHost());

        var result = await sut.TranscribeWithLanguageHintsAsync(BuildPcm16Wav([0, 0]), ["nl", "en", "auto"], false, null, default);

        Assert.Contains("language=nl,en", query);
        Assert.Equal("nl", result.DetectedLanguage);
        Assert.Equal(300 * 16_000 * 2 + 44, sut.MaximumAudioUploadBytes);
    }

    [Fact]
    public async Task Errors_NameTheCreditLimitAndTheRejectedParameter()
    {
        var responses = new Queue<HttpResponseMessage>([
            JsonResponse("""{"status":402,"detail":"No credits left"}""", HttpStatusCode.PaymentRequired),
            JsonResponse("""{"status":400,"title":"Invalid Query Parameter","detail":"include_words is not supported by rumble-2-flash yet","code":"invalid_query_parameter"}""", HttpStatusCode.BadRequest),
        ]);
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((_, _) => responses.Dequeue())));
        await sut.ActivateAsync(CatalogHost());

        var credit = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, null, default));
        var rejected = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, null, default));

        Assert.Contains("credit limit reached: No credits left", credit.Message);
        Assert.Contains("rejected the request: include_words is not supported", rejected.Message);
    }

    [Fact]
    public async Task DictionaryModel_IsCreatedOnceAndOnlyChangesAreSent()
    {
        var api = new FakeCustomModelApi();
        var queries = new List<string>();
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            queries.Add(request.RequestUri!.Query);
            return JsonResponse("""{"text":"ok"}""");
        })), customModelApi: api);
        var host = CatalogHost();
        host.SetSetting("dictionaryModelEnabled", true);
        await sut.ActivateAsync(host);
        Assert.Equal(50_000, sut.DictionaryTermsBudget.MaxTerms);

        await sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, PluginDictionaryTerms.CreateStructuredPrompt(["Reson8", "TypeWhisper"]), default);
        await sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, PluginDictionaryTerms.CreateStructuredPrompt(["TypeWhisper", "Reson8"]), default);
        await sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, PluginDictionaryTerms.CreateStructuredPrompt(["TypeWhisper", "Soniox"]), default);

        Assert.Equal(["create Reson8,TypeWhisper", "delete model-1 Reson8", "add model-1 Soniox"], api.Calls);
        Assert.All(queries, query => Assert.Contains("custom_model_id=model-1", query));
        Assert.All(queries, query => Assert.DoesNotContain("phrases=", query));
        Assert.Equal(new Reson8DictionaryModelState("model-1", ["TypeWhisper", "Soniox"]).Terms, host.GetSetting<Reson8DictionaryModelState>("dictionaryModel")!.Terms);
    }

    [Fact]
    public async Task DictionaryModel_SendsTheTermsOfAFailedAddAgain()
    {
        var api = new FakeCustomModelApi { FailAdds = true };
        var saved = new List<Reson8DictionaryModelState>();
        using var sync = new Reson8DictionaryModelSync(new Reson8DictionaryModelState("model-1", ["A"]), api, saved.Add);

        // A is gone from the model, and Reson8 may or may not hold B.
        await Assert.ThrowsAsync<TimeoutException>(() => sync.ModelIdForAsync(["B"], default));
        Assert.Empty(saved[^1].Terms);
        Assert.Equal(["B"], saved[^1].Unconfirmed);

        api.FailAdds = false;
        Assert.Equal("model-1", await sync.ModelIdForAsync(["A"], default));

        Assert.Equal(["delete model-1 A", "add model-1 B", "delete model-1 B", "add model-1 A"], api.Calls);
        Assert.Equal(["A"], saved[^1].Terms);
        Assert.True(saved[^1].Unconfirmed is null or { Count: 0 });
    }

    [Fact]
    public async Task DictionaryModel_SendsTheTermsOfAFailedDeleteAgainForTheSameDictionary()
    {
        var api = new FakeCustomModelApi { FailDeletes = true };
        var saved = new List<Reson8DictionaryModelState>();
        using var sync = new Reson8DictionaryModelSync(new Reson8DictionaryModelState("model-1", ["A", "B"]), api, saved.Add);

        await Assert.ThrowsAsync<TimeoutException>(() => sync.ModelIdForAsync(["A"], default));
        Assert.Equal(["A"], saved[^1].Terms);
        Assert.Equal(["B"], saved[^1].Unconfirmed);

        api.FailDeletes = false;
        Assert.Equal("model-1", await sync.ModelIdForAsync(["A"], default));
        Assert.Equal("model-1", await sync.ModelIdForAsync(["A"], default));

        Assert.Equal(["delete model-1 B", "delete model-1 B"], api.Calls);
        Assert.True(sync.State.Unconfirmed is null or { Count: 0 });
    }

    [Fact]
    public async Task DictionaryModel_DeletedInTheConsoleIsCreatedAgainAndTheRequestRetried()
    {
        var api = new FakeCustomModelApi();
        var attempts = 0;
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((request, _) =>
            ++attempts == 1
                ? JsonResponse("""{"status":400,"detail":"Unknown vocabulary: model-1"}""", HttpStatusCode.BadRequest)
                : JsonResponse($$"""{"text":"{{HttpUtility.ParseQueryString(request.RequestUri!.Query)["custom_model_id"]}}"}"""))), customModelApi: api);
        var host = CatalogHost();
        host.SetSetting("dictionaryModelEnabled", true);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, PluginDictionaryTerms.CreateStructuredPrompt(["Reson8"]), default);

        Assert.Equal("model-2", result.Text);
        Assert.Equal(["create Reson8", "create Reson8"], api.Calls);
    }

    [Fact]
    public async Task DictionaryModel_ThatIsSlowSendsPhrasesAndFinishesInTheBackground()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeCustomModelApi { Gate = release.Task };
        string? query = null;
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return JsonResponse("""{"text":"ok"}""");
        })), customModelApi: api) { DictionaryModelWait = TimeSpan.FromMilliseconds(50) };
        var host = CatalogHost();
        host.SetSetting("dictionaryModelEnabled", true);
        await sut.ActivateAsync(host);

        await sut.TranscribeAsync(BuildPcm16Wav([0, 0]), null, false, PluginDictionaryTerms.CreateStructuredPrompt(["Reson8"]), default);
        Assert.Contains("phrases=Reson8", query);
        release.SetResult();
        await sut.BackgroundWork;

        Assert.Equal("model-1", sut.DictionaryModelState.ModelId);
    }

    [Fact]
    public async Task DictionaryModel_IsForgottenWhenTheKeyChanges()
    {
        var host = CatalogHost();
        host.SetSetting("dictionaryModel", new Reson8DictionaryModelState("dict-1", ["Reson8"]));
        using var sut = new Reson8Plugin(new HttpClient(), customModelApi: new FakeCustomModelApi());
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("another-account");
        await sut.BackgroundWork;

        Assert.Null(sut.DictionaryModelState.ModelId);
        Assert.Null(host.GetSetting<Reson8DictionaryModelState>("dictionaryModel")!.ModelId);
    }

    [Fact]
    public void PatternSets_ParseKeepIdsByNameAndRejectMistakes()
    {
        var previous = new[] { new Reson8PatternSet("orders", "Order codes", ["OLD[0-9]"]) };

        var sets = Reson8Catalog.ParsePatternSets("Order codes\nAMZ[0-9]{6}\n ORD-[0-9]{4,6} \n\n\nTickets\nTCK[A-Z]{2}\n\nTickets 2\nT2", previous);

        Assert.Equal(["orders", "tickets", "tickets-2"], sets.Select(set => set.Id));
        Assert.Equal(["AMZ[0-9]{6}", "ORD-[0-9]{4,6}"], sets[0].Patterns);
        Assert.Equal("Order codes\nAMZ[0-9]{6}\nORD-[0-9]{4,6}\n\nTickets\nTCK[A-Z]{2}\n\nTickets 2\nT2", Reson8Catalog.FormatPatternSets(sets));
        Assert.Throws<ArgumentException>(() => Reson8Catalog.ParsePatternSets("Only a name", []));
        Assert.Throws<ArgumentException>(() => Reson8Catalog.ParsePatternSets("Same\nA\n\nsame\nB", []));
        Assert.Empty(Reson8Catalog.ParsePatternSets("  \n\n ", []));
    }

    [Fact]
    public void PatternSets_ThatWouldNotFitIntoARequestAreRejected()
    {
        // 50 patterns of about 100 characters are within the per-pattern limits but take about 10 KB once encoded.
        var pattern = string.Concat(Enumerable.Repeat("[A-Z]{2}", 12)) + "[0-9]";
        var text = "Long\n" + string.Join("\n", Enumerable.Range(0, Reson8Catalog.MaximumPatternsPerSet).Select(index => pattern + index % 10));

        var error = Assert.Throws<ArgumentException>(() => Reson8Catalog.ParsePatternSets(text, []));

        Assert.Contains("“Long” is too long for a Reson8 request", error.Message);
        Assert.Empty(Reson8Catalog.SanitizePatternSets([new Reson8PatternSet("long", "Long", Enumerable.Repeat(pattern, 50).ToArray())]));
        Assert.Single(Reson8Catalog.ParsePatternSets("Short\n" + pattern, []));
    }

    [Fact]
    public void PatternSets_ANewSetNeverTakesTheIdOfAKeptOne()
    {
        var previous = new[] { new Reson8PatternSet("orders", "Orders", ["OLD[0-9]"]) };

        // "Orders!" gives the slug "orders" too, and comes first.
        var sets = Reson8Catalog.ParsePatternSets("Orders!\nNEW[0-9]\n\nOrders\nOLD[0-9]", previous);

        Assert.Equal(["orders-2", "orders"], sets.Select(set => set.Id));
        Assert.Equal(["OLD[0-9]"], sets.Single(set => set.Id == "orders").Patterns);
    }

    [Fact]
    public async Task Settings_SavePatternSetsFillerBiasAndDictionarySwitch()
    {
        var host = CatalogHost();
        using var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);
        var settings = sut.TextSettings.ToDictionary(setting => setting.Id);
        Assert.Equal(PluginSettingsSection.Transcription, settings["fillerMode"].Section);
        Assert.Equal("default", settings["biasStrength"].Value);
        Assert.Contains(settings["biasStrength"].Choices, choice => choice.Value == "0.60");
        Assert.True(settings["patternSets"].IsMultiline);

        await sut.SaveTextSettingAsync("patternSets", "Order codes\nAMZ[0-9]{6}", default);
        await sut.SaveTextSettingAsync("fillerMode", "verbatim", default);
        await sut.SaveTextSettingAsync("biasStrength", "0.60", default);
        await sut.SaveTextSettingAsync("dictionaryModelEnabled", "true", default);
        sut.SelectModel("patterns:order-codes");
        await sut.SaveTextSettingAsync("patternSets", "", default);

        Assert.Equal("verbatim", host.GetSetting<string>("fillerMode"));
        Assert.Equal(0.6, host.GetSetting<double?>("biasStrength"));
        Assert.True(host.GetSetting<bool?>("dictionaryModelEnabled"));
        Assert.Equal("__default__", sut.SelectedModelId);
        Assert.Empty(sut.PatternSets);
        await sut.SaveTextSettingAsync("biasStrength", "default", default);
        Assert.Null(sut.BiasStrength);
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("fillerMode", "loud", default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("patternSets", "Name only", default));
    }

    [Fact]
    public async Task Refresh_LoadsBothListsAndReportsAListThatFailed()
    {
        var modelListFails = false;
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/model" => modelListFails ? JsonResponse("{}", HttpStatusCode.NotFound) : JsonResponse(ModelListJson),
            _ => JsonResponse("""[{"id":"custom-1","name":"Legal"}]"""),
        })));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        await sut.ActivateAsync(host);

        var message = await sut.ExecuteSettingsActionAsync("refresh", default);
        modelListFails = true;
        var partial = await sut.ExecuteSettingsActionAsync("refresh", default);

        Assert.Equal("2 models and 1 custom models loaded.", message);
        Assert.StartsWith("2 models and 1 custom models loaded. One list could not be loaded", partial);
        Assert.Equal(["resonant-1", "rumble-2-flash"], host.GetSetting<List<Reson8Model>>("fetchedModels")!.Select(model => model.Id));
    }

    [Fact]
    public async Task CheckPatterns_ReportsReson8sAnswerPerSet()
    {
        var queries = new List<string>();
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            queries.Add(request.RequestUri!.Query);
            Assert.Equal(16_000, body!.Length);
            return request.RequestUri.Query.Contains("BAD", StringComparison.Ordinal)
                ? JsonResponse("""{"status":400,"detail":"Patterns cannot be combined with the custom model configured for this API client."}""", HttpStatusCode.BadRequest)
                : JsonResponse("""{"text":""}""");
        })));
        var host = CatalogHost();
        host.SetSetting("patternSets", new[] { new Reson8PatternSet("good", "Good", ["AMZ[0-9]{6}"]), new Reson8PatternSet("bad", "Bad", ["BAD[0-9]"]) });
        await sut.ActivateAsync(host);

        var result = await sut.ExecuteSettingsActionAsync("check-patterns", default);

        Assert.Equal("Good: Reson8 accepts these patterns.\nBad: Patterns cannot be combined with the custom model configured for this API client.", result);
        Assert.All(queries, query => Assert.Contains("patterns=", query));
    }

    [Fact]
    public async Task CheckPatterns_ReportsASetWithoutAnswerAndChecksTheNextOne()
    {
        using var sut = new Reson8Plugin(new HttpClient(new CapturingHandler((request, _) =>
            request.RequestUri!.Query.Contains("SLOW", StringComparison.Ordinal)
                ? throw new TaskCanceledException("The request timed out.")
                : JsonResponse("""{"text":""}"""))));
        var host = CatalogHost();
        host.SetSetting("patternSets", new[] { new Reson8PatternSet("slow", "Slow", ["SLOW[0-9]"]), new Reson8PatternSet("good", "Good", ["AMZ[0-9]{6}"]) });
        await sut.ActivateAsync(host);

        var result = await sut.ExecuteSettingsActionAsync("check-patterns", default);

        Assert.Equal("Slow: Reson8 did not answer in time.\nGood: Reson8 accepts these patterns.", result);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExecuteSettingsActionAsync("check-patterns", cancelled.Token));
    }

    [Fact]
    public async Task Realtime_RejectionNamesReson8sReason()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 400;
            context.Response.Headers["X-Error-Message"] = "Unknown vocabulary: dict-1";
            context.Response.Close();
        });

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            Reson8StreamingSession.ConnectAsync("fixture", "Authorization", new Uri($"ws://127.0.0.1:{port}/"), null, default));
        await server;

        Assert.Equal(400, error.HttpStatusCode);
        Assert.Contains("Unknown vocabulary: dict-1", error.Message);
    }

    [Fact]
    public async Task Realtime_SplitsLargeAudioIntoFramesReson8Accepts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var frames = new ConcurrentQueue<int>();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[70_000];
            var received = 0;
            while (received < 20_000)
            {
                var frameLength = 0;
                System.Net.WebSockets.WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, frameLength, buffer.Length - frameLength), ct);
                    frameLength += result.Count;
                } while (!result.EndOfMessage);
                frames.Enqueue(frameLength);
                received += frameLength;
            }
        }, ct);

        await using (var session = await Reson8StreamingSession.ConnectAsync("fixture", "Authorization", new Uri($"ws://127.0.0.1:{port}/"), null, ct))
        {
            await session.SendAudioAsync(new byte[20_000], ct);
            await server.WaitAsync(ct);
        }

        Assert.Equal([8_192, 8_192, 3_616], frames);
    }

    [Fact]
    public void Collector_ReportsTheLanguageOfFinalTranscripts()
    {
        var collector = new Reson8TranscriptCollector("de,en");

        var interim = collector.ApplyEvent("""{"type":"transcript","text":"Hal","is_final":false,"language":"de"}""");
        var final = collector.ApplyEvent("""{"type":"transcript","text":"Hallo","is_final":true,"language":"de"}""");

        Assert.Null(interim!.DetectedLanguage);
        Assert.Equal("de", final!.DetectedLanguage);
        Assert.Equal("de", collector.DetectedLanguage);
    }

    private static TestPluginHostServices CatalogHost()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        host.SetSetting("fetchedModels", Reson8Catalog.ParseModels(ModelListJson));
        return host;
    }

    private static System.Collections.Specialized.NameValueCollection Parse(string query) => HttpUtility.ParseQueryString(query);

    private sealed class ToleranceComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-9 && Math.Abs(x.Item2 - y.Item2) < 1e-9;
        public int GetHashCode((double, double) obj) => 0;
    }

    private sealed class FakeCustomModelApi : IReson8CustomModelApi
    {
        private int _created;
        public List<string> Calls { get; } = [];
        public Task Gate { get; init; } = Task.CompletedTask;
        public bool FailAdds { get; set; }
        public bool FailDeletes { get; set; }

        public async Task<string> CreateModelAsync(string name, string description, IReadOnlyList<string> phrases, CancellationToken ct)
        {
            await Gate;
            Assert.Equal("TypeWhisper Dictionary", name);
            Calls.Add("create " + string.Join(",", phrases));
            return "model-" + Interlocked.Increment(ref _created);
        }

        public Task AddPhrasesAsync(string modelId, IReadOnlyList<string> phrases, CancellationToken ct)
        {
            Calls.Add($"add {modelId} {string.Join(",", phrases)}");
            return FailAdds ? Task.FromException(new TimeoutException("Reson8 did not answer.")) : Task.CompletedTask;
        }

        public Task DeletePhrasesAsync(string modelId, IReadOnlyList<string> phrases, CancellationToken ct)
        {
            Calls.Add($"delete {modelId} {string.Join(",", phrases)}");
            return FailDeletes ? Task.FromException(new TimeoutException("Reson8 did not answer.")) : Task.CompletedTask;
        }
    }
}
