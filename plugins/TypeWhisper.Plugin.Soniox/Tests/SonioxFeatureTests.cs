using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SonioxFeatureTests
{
    private const string ModelsJson = """
        {
          "models": [
            { "id": "stt-rt-v6", "aliased_model_id": null, "name": "Speech-to-Text Real-time v6", "transcription_mode": "real_time", "languages": [ { "code": "de", "name": "German" }, { "code": "en", "name": "English" } ] },
            { "id": "stt-async-v6", "aliased_model_id": null, "name": "Speech-to-Text Async v6", "transcription_mode": "async", "languages": [ { "code": "de", "name": "German" } ] },
            { "id": "stt-rt-v5", "aliased_model_id": null, "name": "Speech-to-Text Real-time v5", "transcription_mode": "real_time", "languages": [] },
            { "id": "stt-async-v5", "aliased_model_id": null, "name": "Speech-to-Text Async v5", "transcription_mode": "async", "languages": [] },
            { "id": "stt-rt-preview", "aliased_model_id": "stt-rt-v5", "name": "Preview", "transcription_mode": "real_time", "languages": [] },
            { "id": "stt-rt-v4", "aliased_model_id": "stt-rt-v5", "name": "v4", "transcription_mode": "real_time", "languages": [] },
            { "id": "stt-async-v4", "aliased_model_id": "stt-async-v5", "name": "v4", "transcription_mode": "async", "languages": [] },
            { "id": "../escape", "transcription_mode": "async" },
            { "id": "stt-other", "transcription_mode": "batch" }
          ]
        }
        """;

    private const string TtsModelsJson = """
        {
          "models": [
            { "id": "tts-rt-v2", "aliased_model_id": null, "name": "TTS RT v2", "languages": [ { "code": "de", "name": "German" }, { "code": "en", "name": "English" } ], "voices": [ { "id": "Daniel", "gender": "male" }, { "id": "Nina", "gender": "female" }, { "id": "bad\nvoice" } ] },
            { "id": "tts-rt-v1", "aliased_model_id": "tts-rt-v2", "name": "TTS RT v1", "languages": [], "voices": [ { "id": "Daniel" } ] }
          ]
        }
        """;

    // Shape of a real Soniox async transcript with one-way translation to English and without language
    // identification: "text" keeps the spoken words, and every spoken token is "none".
    private const string TranslatedTranscriptJson = """
        {
          "id": "t1",
          "text": "Bitte schick mir den Entwurf. Danke dir.",
          "tokens": [
            { "text": "Bitte", "start_ms": 1000, "end_ms": 1300, "translation_status": "none", "language": "de" },
            { "text": " schick mir den Entwurf.", "start_ms": 1300, "end_ms": 2600, "translation_status": "none", "language": "de" },
            { "text": "Please", "start_ms": 0, "end_ms": 0, "translation_status": "translation" },
            { "text": " send me the draft.", "start_ms": 0, "end_ms": 0, "translation_status": "translation" },
            { "text": " Danke dir.", "start_ms": 4000, "end_ms": 4800, "translation_status": "none", "language": "de" },
            { "text": " Thank you.", "start_ms": 0, "end_ms": 0, "translation_status": "translation" }
          ]
        }
        """;

    [Fact]
    public void Catalog_PrefersNewestNonAliasModelsAndOffersVersionsNewestFirst()
    {
        var models = SonioxModels.ParseSttModels(ModelsJson);

        Assert.DoesNotContain(models, model => model.Id is "../escape" or "stt-other");
        Assert.Equal("stt-rt-v6", SonioxModels.PreferredSttModelId(models, SonioxModels.RealtimeMode));
        Assert.Equal("stt-async-v6", SonioxModels.PreferredSttModelId(models, SonioxModels.AsyncMode));
        Assert.Equal(["v6", "v5"], SonioxModels.SelectableVersions(models));
        Assert.Equal(["de", "en"], models.Single(model => model.Id == "stt-rt-v6").Languages);
    }

    [Fact]
    public void Catalog_FallsBackToV6WithoutModelList()
    {
        Assert.Equal("stt-rt-v6", SonioxModels.PreferredSttModelId(null, SonioxModels.RealtimeMode));
        Assert.Equal("stt-async-v6", SonioxModels.PreferredSttModelId([], SonioxModels.AsyncMode));
        Assert.Equal("tts-rt-v2", SonioxModels.PreferredTtsModelId(null));
        Assert.Equal("stt-async-v5", SonioxModels.ResolveSttModelId("v5", null, SonioxModels.AsyncMode));
        Assert.Equal("stt-rt-v5", SonioxModels.ResolveSttModelId("v5", null, SonioxModels.RealtimeMode));
    }

    [Fact]
    public void Catalog_NeverMovesAutomaticToAPreviewButFollowsMovingAliases()
    {
        SonioxSttModel Model(string id, string? alias = null) => new(id, alias, null, SonioxModels.RealtimeMode, []);

        var withPreview = new[] { Model("stt-rt-v6"), Model("stt-rt-v7-preview") };
        Assert.Equal("stt-rt-v6", SonioxModels.PreferredSttModelId(withPreview, SonioxModels.RealtimeMode));
        Assert.Contains("v7-preview", SonioxModels.SelectableVersions(withPreview));

        var withLatest = new[] { Model("stt-rt-v6"), Model("stt-rt-latest", "stt-rt-v6") };
        Assert.Equal("stt-rt-latest", SonioxModels.PreferredSttModelId(withLatest, SonioxModels.RealtimeMode));

        var doubleDigits = new[] { Model("stt-rt-v9"), Model("stt-rt-v10") };
        Assert.Equal("stt-rt-v10", SonioxModels.PreferredSttModelId(doubleDigits, SonioxModels.RealtimeMode));
    }

    [Fact]
    public void Catalog_ParsesVoicesAndDropsInvalidIds()
    {
        var models = SonioxModels.ParseTtsModels(TtsModelsJson);

        Assert.Equal("tts-rt-v2", SonioxModels.PreferredTtsModelId(models));
        Assert.Equal(["Daniel", "Nina"], models.Single(model => model.Id == "tts-rt-v2").Voices);
        Assert.Empty(models.Single(model => model.Id == "tts-rt-v1").Voices);
        Assert.Throws<InvalidDataException>(() => SonioxModels.ParseSttModels("""{ "data": [] }"""));
    }

    [Fact]
    public async Task Activate_RestoresSettingsAndIgnoresCatalogOfOtherRegion()
    {
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("model", "v5");
        host.SetSetting("transcriptionContext", "  Meeting about invoices  ");
        host.SetSetting("catalog", Catalog("us"));
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));

        await sut.ActivateAsync(host);

        Assert.Equal("v5", sut.ModelSelection);
        Assert.Equal("stt-async-v5", sut.EffectiveAsyncModelId);
        Assert.Equal("stt-rt-v5", sut.EffectiveRealtimeModelId);
        Assert.Equal("Meeting about invoices", sut.TranscriptionContext);
        Assert.NotNull(sut.CurrentCatalog);
        Assert.Empty(Assert.Single(sut.TranscriptionModels).LanguageCodes);
        sut.SetModelSelection("automatic");
        Assert.Equal(["de", "en"], Assert.Single(sut.TranscriptionModels).LanguageCodes);

        sut.SetRegion("eu");

        Assert.Null(sut.CurrentCatalog);
        Assert.Equal("default", Assert.Single(sut.TranscriptionModels).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../v6")]
    [InlineData("V6 latest")]
    public async Task Activate_FallsBackToAutomaticForInvalidStoredModel(string? stored)
    {
        var host = new FeatureHost();
        host.SetSetting("model", stored);
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));

        await sut.ActivateAsync(host);

        Assert.Equal("automatic", sut.ModelSelection);
        Assert.Equal("stt-async-v6", sut.EffectiveAsyncModelId);
    }

    [Fact]
    public async Task Activate_IgnoresUnreadableCatalog()
    {
        var host = new FeatureHost();
        host.SetRaw("catalog", "\"not a catalog\"");
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));

        await sut.ActivateAsync(host);

        Assert.Null(sut.CurrentCatalog);
        Assert.Equal("stt-rt-v6", sut.EffectiveRealtimeModelId);
    }

    [Fact]
    public async Task Settings_OfferModelVersionsContextAndSpeechInTheirSections()
    {
        var host = new FeatureHost();
        host.SetSetting("catalog", Catalog("us"));
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));
        await sut.ActivateAsync(host);

        var settings = sut.TextSettings.ToDictionary(setting => setting.Id);

        Assert.Equal(PluginSettingsSection.Connection, settings["region"].Section);
        Assert.Equal(["us", "eu", "jp"], settings["region"].Choices.Select(choice => choice.Value));
        Assert.Equal(PluginSettingsSection.Transcription, settings["model"].Section);
        Assert.Equal(["automatic", "v6", "v5"], settings["model"].Choices.Select(choice => choice.Value));
        Assert.Equal("Automatic (currently v6)", settings["model"].Choices[0].Title);
        Assert.True(settings["transcriptionContext"].IsMultiline);
        Assert.Equal(6_000, settings["transcriptionContext"].MaxLength);
        Assert.Equal(PluginSettingsSection.Speech, settings["tts-model"].Section);
        Assert.Equal(["automatic", "tts-rt-v2"], settings["tts-model"].Choices.Select(choice => choice.Value));
        Assert.Equal(["Daniel", "Nina"], settings["speech-voice"].Choices.Select(choice => choice.Value));
        Assert.Equal("Daniel", settings["speech-voice"].Value);
        Assert.Equal("refresh-models", Assert.Single(sut.SettingsActions).Id);
    }

    [Fact]
    public async Task Settings_SaveValidatesAndPersistsValues()
    {
        var host = new FeatureHost();
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));
        await sut.ActivateAsync(host);

        await sut.SaveTextSettingAsync("model", "v5", default);
        await sut.SaveTextSettingAsync("transcriptionContext", "  Podcast about\nTypeWhisper  ", default);
        await sut.SaveTextSettingAsync("speech-voice", "Daniel", default);
        await sut.SaveTextSettingAsync("tts-model", "tts-rt-v2", default);

        Assert.Equal("v5", host.GetSetting<string>("model"));
        Assert.Equal("Podcast about\nTypeWhisper", host.GetSetting<string>("transcriptionContext"));
        Assert.Equal("Daniel", sut.SelectedVoiceId);
        Assert.Equal("tts-rt-v2", sut.EffectiveTtsModelId);
        Assert.Equal(10_000 - "Podcast about\nTypeWhisper".Length, sut.DictionaryTermsBudget.MaxTotalChars);
        Assert.Equal(4, host.NotifyCount);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("model", "v6 beta", default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("region", "mars", default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("speech-voice", "Nobody", default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("transcriptionContext", new string('x', 6_001), default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("unknown", "x", default));
        Assert.Equal("v5", sut.ModelSelection);
    }

    [Fact]
    public void RequestContext_CombinesDictionaryTermsAndTextWithinSonioxLimit()
    {
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(["TypeWhisper", "Soniox, Inc."]);

        var context = SonioxPlugin.CreateRequestContext(prompt, "  Product demo  ");

        Assert.NotNull(context);
        Assert.Equal(["TypeWhisper", "Soniox, Inc."], (string[])context["terms"]);
        Assert.Equal("Product demo", context["text"]);
        Assert.Null(SonioxPlugin.CreateRequestContext(null, "  "));
        Assert.Equal(["Alpha", "Beta"], (string[])SonioxPlugin.CreateRequestContext("Alpha, Beta", null)!["terms"]);

        var longText = new string('x', 6_000);
        var manyTerms = Enumerable.Range(0, 2_000).Select(index => $"term{index:0000}").ToArray();
        var clipped = SonioxPlugin.CreateRequestContext(PluginDictionaryTerms.CreateStructuredPrompt(manyTerms, new(MaxTotalChars: 100_000)), longText)!;
        var terms = (string[])clipped["terms"];
        Assert.True(string.Join(", ", terms).Length <= 4_000);
        Assert.NotEmpty(terms);
    }

    [Fact]
    public async Task Transcribe_SendsContextTranslationAndPinnedModel()
    {
        string? createBody = null;
        var handler = new FlowHandler(body => createBody = body, TranslatedTranscriptJson);
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("model", "v5");
        host.SetSetting("transcriptionContext", "Email to a colleague");
        using var sut = new SonioxPlugin(new HttpClient(handler), pollDelay: TimeSpan.Zero, maxPollAttempts: 2,
            compressedUploadFactory: _ => new SonioxTranscriptionUpload([1, 2], "audio.m4a", "audio/mp4", SonioxUploadFormat.M4a));
        await sut.ActivateAsync(host);

        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(["TypeWhisper"]);
        var result = await sut.TranscribeWithLanguageHintsAsync([1, 2, 3], ["de"], translate: true, prompt, default);
        await sut.LastCleanupTask;

        using var document = JsonDocument.Parse(createBody!);
        var root = document.RootElement;
        Assert.Equal("stt-async-v5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("enable_language_identification").GetBoolean());
        Assert.Equal("one_way", root.GetProperty("translation").GetProperty("type").GetString());
        Assert.Equal("en", root.GetProperty("translation").GetProperty("target_language").GetString());
        Assert.Equal(["TypeWhisper"], root.GetProperty("context").GetProperty("terms").EnumerateArray().Select(term => term.GetString()));
        Assert.Equal("Email to a colleague", root.GetProperty("context").GetProperty("text").GetString());
        Assert.Equal("Please send me the draft. Thank you.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
    }

    [Fact]
    public void ParseTranscript_TranslationUsesEnglishTokensTimedByTheirSpokenChunk()
    {
        using var details = JsonDocument.Parse("""{ "audio_duration_ms": 5000 }""");

        var result = SonioxPlugin.ParseTranscript(TranslatedTranscriptJson, details.RootElement, "de", translated: true);

        Assert.Equal("Please send me the draft. Thank you.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(5.0, result.DurationSeconds);
        // The pause between the spoken chunks splits the subtitles like it does for the transcript.
        Assert.Equal(["Please send me the draft.", "Thank you."], result.Segments.Select(segment => segment.Text));
        Assert.Equal((1.0, 2.6), (result.Segments[0].Start, result.Segments[0].End));
        Assert.Equal((4.0, 4.8), (result.Segments[1].Start, result.Segments[1].End));
    }

    [Fact]
    public void ParseTranscript_WithoutTranslationIgnoresTranslationTokens()
    {
        using var details = JsonDocument.Parse("""{ "audio_duration_ms": 5000 }""");

        var result = SonioxPlugin.ParseTranscript(TranslatedTranscriptJson, details.RootElement, null);

        Assert.Equal("Bitte schick mir den Entwurf. Danke dir.", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.DoesNotContain(result.Segments, segment => segment.Text.Contains("Please", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseTranscript_TranslationOfEnglishSpeechKeepsTheTranscript()
    {
        using var details = JsonDocument.Parse("""{ "audio_duration_ms": 1000 }""");
        const string english = """
            { "text": "Hello there.", "tokens": [ { "text": "Hello there.", "start_ms": 0, "end_ms": 900, "translation_status": "none", "language": "en" } ] }
            """;

        var result = SonioxPlugin.ParseTranscript(english, details.RootElement, "en", translated: true);

        Assert.Equal("Hello there.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
    }

    [Fact]
    public void ParseTranscript_TranslationKeepsEnglishPassagesOfMixedSpeech()
    {
        // Shape of a real Soniox transcript of German, then English, then German speech with language identification.
        const string mixed = """
            {
              "text": "Die Rechnung beträgt 1.234 Euro 56. Honestly, the result is better than I expected. Bitte schick mir den Entwurf.",
              "tokens": [
                { "text": "Die Rechnung beträgt 1.234 Euro 56.", "start_ms": 1350, "end_ms": 5430, "translation_status": "original", "language": "de" },
                { "text": "The bill comes to 1,234 euros and 56 cents.", "start_ms": 0, "end_ms": 0, "translation_status": "translation", "language": "en" },
                { "text": " Honestly,", "start_ms": 7710, "end_ms": 8400, "translation_status": "none", "language": "en" },
                { "text": " the result is better than I expected.", "start_ms": 8400, "end_ms": 9990, "translation_status": "none", "language": "en" },
                { "text": " Bitte schick mir den Entwurf.", "start_ms": 12030, "end_ms": 13830, "translation_status": "original", "language": "de" },
                { "text": "Please send me the draft.", "start_ms": 0, "end_ms": 0, "translation_status": "translation", "language": "en" }
              ]
            }
            """;
        using var details = JsonDocument.Parse("""{ "audio_duration_ms": 14000 }""");

        var result = SonioxPlugin.ParseTranscript(mixed, details.RootElement, "de", translated: true);

        Assert.Equal("The bill comes to 1,234 euros and 56 cents. Honestly, the result is better than I expected. Please send me the draft.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(
            ["The bill comes to 1,234 euros and 56 cents.", "Honestly, the result is better than I expected.", "Please send me the draft."],
            result.Segments.Select(segment => segment.Text));
        Assert.Equal((1.35, 5.43), (result.Segments[0].Start, result.Segments[0].End));
        Assert.Equal((7.71, 9.99), (result.Segments[1].Start, result.Segments[1].End));
        Assert.Equal((12.03, 13.83), (result.Segments[2].Start, result.Segments[2].End));
    }

    [Fact]
    public void StreamingConfiguration_IncludesContextOnlyWhenPresent()
    {
        var context = SonioxPlugin.CreateRequestContext(PluginDictionaryTerms.CreateStructuredPrompt(["Soniox"]), "Standup");

        using var document = JsonDocument.Parse(SonioxStreamingSession.Configuration(new("stt-rt-v6", ["de"], context)));

        var root = document.RootElement;
        Assert.Equal("stt-rt-v6", root.GetProperty("model").GetString());
        Assert.Equal(["Soniox"], root.GetProperty("context").GetProperty("terms").EnumerateArray().Select(term => term.GetString()));
        Assert.Equal("Standup", root.GetProperty("context").GetProperty("text").GetString());
    }

    [Fact]
    public void Engine_AdvertisesTranslationDictionaryAndLongRecordingSplit()
    {
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));

        Assert.True(sut.SupportsTranslation);
        Assert.True(sut.SupportsDictionaryTerms);
        Assert.True(sut.SupportsStructuredDictionaryTerms);
        Assert.True(((ITranscriptionEnginePlugin)sut).SupportsStreamingForPrompt(PluginDictionaryTerms.CreateStructuredPrompt(["x"])));
        Assert.Equal(270 * 60 * 16_000 * 2 + 44, sut.MaximumAudioUploadBytes);
        Assert.Equal(10_000, sut.DictionaryTermsBudget.MaxTotalChars);
    }

    [Fact]
    public async Task RefreshModels_CachesCatalogAndReportsAutomaticModels()
    {
        var requests = new List<string>();
        var handler = new RoutingHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            Assert.Equal("Bearer key", request.Headers.Authorization?.ToString());
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => Json(ModelsJson),
                "/v1/tts-models" => Json(TtsModelsJson),
                _ => throw new InvalidOperationException(request.RequestUri.ToString())
            };
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("region", "eu");
        var time = new ManualTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        using var sut = new SonioxPlugin(new HttpClient(handler), time: time);
        await sut.ActivateAsync(host);

        var message = await sut.ExecuteSettingsActionAsync("refresh-models", default);

        Assert.Equal("Models refreshed. Automatic uses stt-rt-v6 for live text and stt-async-v6 for recordings.", message);
        Assert.Equal(["https://api.eu.soniox.com/v1/models", "https://api.eu.soniox.com/v1/tts-models"], requests);
        var stored = host.GetSetting<SonioxCatalog>("catalog")!;
        Assert.Equal("eu", stored.Region);
        Assert.Equal(time.GetUtcNow(), stored.FetchedAt);
        Assert.Equal(1, host.NotifyCount);
        Assert.Equal(["Daniel", "Nina"], sut.AvailableVoices.Select(voice => voice.Id));

        // A restarted plugin reads the cached list back.
        using var restarted = new SonioxPlugin(new HttpClient(new FailingHandler()), time: time);
        await restarted.ActivateAsync(host);
        Assert.Equal(["automatic", "v6", "v5"], restarted.TextSettings.Single(setting => setting.Id == "model").Choices.Select(choice => choice.Value));
    }

    [Fact]
    public async Task RefreshModels_KeepsCachedVoicesWhenTheVoiceListFails()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? Json(ModelsJson)
            : Json("""{ "error_message": "busy" }""", HttpStatusCode.ServiceUnavailable));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us"));
        using var sut = new SonioxPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        var catalog = await sut.RefreshCatalogAsync(default);

        Assert.Equal(["Daniel", "Nina"], catalog.TtsModels.Single(model => model.Id == "tts-rt-v2").Voices);
        Assert.Equal(4, catalog.SttModels.Count(model => model.AliasedModelId is null));
    }

    [Fact]
    public async Task RefreshModels_RequiresKeyAndReportsSonioxErrors()
    {
        var host = new FeatureHost();
        using var sut = new SonioxPlugin(new HttpClient(new RoutingHandler(_ => Json("""{ "error_message": "Invalid API key" }""", HttpStatusCode.Unauthorized))));
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteSettingsActionAsync("refresh-models", default));
        await sut.SetApiKeyAsync("bad");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sut.ExecuteSettingsActionAsync("refresh-models", default));
        Assert.Contains("Invalid API key", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => sut.ExecuteSettingsActionAsync("other", default));
        Assert.Null(host.GetSetting<SonioxCatalog>("catalog"));
    }

    [Fact]
    public async Task BackgroundRefresh_LoadsMissingOrDayOldListsAndBacksOffAfterFailures()
    {
        var modelRequests = 0;
        var fail = false;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
            {
                modelRequests++;
                return fail ? Json("""{ "error_message": "down" }""", HttpStatusCode.BadGateway) : Json(ModelsJson);
            }

            return Json(TtsModelsJson);
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        var time = new ManualTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        using var sut = new SonioxPlugin(new HttpClient(handler), time: time, backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);

        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        Assert.Equal(1, modelRequests);
        Assert.NotNull(sut.CurrentCatalog);

        time.Advance(TimeSpan.FromHours(23));
        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        Assert.Equal(1, modelRequests);

        time.Advance(TimeSpan.FromHours(2));
        fail = true;
        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        Assert.Equal(2, modelRequests);

        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        Assert.Equal(2, modelRequests);

        time.Advance(TimeSpan.FromMinutes(11));
        fail = false;
        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        Assert.Equal(3, modelRequests);
        Assert.Equal(time.GetUtcNow(), sut.CurrentCatalog!.FetchedAt);
    }

    [Fact]
    public async Task BackgroundRefresh_IsSkippedWithoutKeyOrWhenDisabled()
    {
        var handler = new RoutingHandler(_ => throw new InvalidOperationException("No request expected."));
        var host = new FeatureHost();
        using var withoutKey = new SonioxPlugin(new HttpClient(handler), backgroundCatalogRefresh: true);
        await withoutKey.ActivateAsync(host);
        withoutKey.RefreshCatalogInBackgroundIfStale();
        await withoutKey.LastCleanupTask;

        host.Secrets["api-key"] = "key";
        using var disabled = new SonioxPlugin(new HttpClient(handler));
        await disabled.ActivateAsync(host);
        disabled.RefreshCatalogInBackgroundIfStale();
        await disabled.LastCleanupTask;

        Assert.Null(host.GetSetting<SonioxCatalog>("catalog"));
    }

    [Fact]
    public async Task Speak_PostsPcmRequestToRegionAndPlaysOnSelectedDevice()
    {
        string? body = null;
        Uri? uri = null;
        var handler = new RoutingHandler(request =>
        {
            uri = request.RequestUri;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 0, 2, 0]) };
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("region", "jp");
        using var sut = new SonioxPlugin(new HttpClient(handler));
        byte[]? played = null;
        string? device = null;
        sut.SpeechPlaybackFactory = (pcm, output) => { played = pcm; device = output; return new FakePlayback(); };
        await sut.ActivateAsync(host);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("  Hallo Welt  ", "de-DE") { VoiceId = "Nina", OutputDeviceId = "speakers" }, default);

        Assert.IsType<FakePlayback>(session);
        Assert.Equal(new Uri("https://tts-rt.jp.soniox.com/tts"), uri);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("tts-rt-v2", root.GetProperty("model").GetString());
        Assert.Equal("de", root.GetProperty("language").GetString());
        Assert.Equal("Nina", root.GetProperty("voice").GetString());
        Assert.Equal("pcm_s16le", root.GetProperty("audio_format").GetString());
        Assert.Equal(24_000, root.GetProperty("sample_rate").GetInt32());
        Assert.Equal("Hallo Welt", root.GetProperty("text").GetString());
        Assert.Equal([1, 0, 2, 0], played);
        Assert.Equal("speakers", device);
    }

    [Fact]
    public async Task Speak_RejectsUnknownVoicesAndReportsSonioxErrors()
    {
        var handler = new RoutingHandler(_ => Json("""{ "error_type": "invalid_request", "error_message": "Missing required field: language" }""", HttpStatusCode.BadRequest));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        using var sut = new SonioxPlugin(new HttpClient(handler));
        sut.SpeechPlaybackFactory = (_, _) => new FakePlayback();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.SpeakAsync(new TtsSpeakRequest("Hi") { VoiceId = "Nobody" }, default));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sut.SpeakAsync(new TtsSpeakRequest("Hi"), default));
        Assert.Contains("Missing required field: language", error.Message);
        Assert.False((await sut.SpeakAsync(new TtsSpeakRequest("   "), default)).IsActive);
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SpeakAsync(new TtsSpeakRequest(new string('a', 5_001)), default));
    }

    [Fact]
    public async Task Speak_RefreshesAStaleModelListInTheBackground()
    {
        var modelRequests = 0;
        var handler = new RoutingHandler(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/tts": return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 0]) };
                case "/v1/models": modelRequests++; return Json(ModelsJson);
                case "/v1/tts-models": return Json(TtsModelsJson);
                default: throw new InvalidOperationException(request.RequestUri.ToString());
            }
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        var time = new ManualTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = time.GetUtcNow() - TimeSpan.FromDays(2) });
        using var sut = new SonioxPlugin(new HttpClient(handler), time: time, backgroundCatalogRefresh: true);
        sut.SpeechPlaybackFactory = (_, _) => new FakePlayback();
        await sut.ActivateAsync(host);

        await sut.SpeakAsync(new TtsSpeakRequest("Hallo") { VoiceId = "Nina" }, default);
        await sut.LastCleanupTask;

        Assert.Equal(1, modelRequests);
        Assert.Equal(time.GetUtcNow(), sut.CurrentCatalog!.FetchedAt);
    }

    [Fact]
    public async Task Speak_RejectsOddLengthAudio()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        using var sut = new SonioxPlugin(new HttpClient(handler));
        sut.SpeechPlaybackFactory = (_, _) => new FakePlayback();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidDataException>(() => sut.SpeakAsync(new TtsSpeakRequest("Hi"), default));
    }

    [Fact]
    public async Task SpeechLanguage_FallsBackToAppLanguageThenEnglish()
    {
        var host = new FeatureHost { Language = "de" };
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));
        await sut.ActivateAsync(host);

        Assert.Equal("fr", sut.ResolveSpeechLanguage("fr-CA"));
        Assert.Equal("zh", sut.ResolveSpeechLanguage("zh-Hans"));
        Assert.Equal("no", sut.ResolveSpeechLanguage("nb-NO"));
        Assert.Equal("no", sut.ResolveSpeechLanguage("nn"));
        Assert.Equal("tl", sut.ResolveSpeechLanguage("fil-PH"));
        Assert.Equal("de", sut.ResolveSpeechLanguage(null));
        Assert.Equal("de", sut.ResolveSpeechLanguage("xx"));
    }

    [Fact]
    public async Task Voices_OfAnAliasModelComeFromItsTarget()
    {
        var host = new FeatureHost();
        host.SetSetting("catalog", Catalog("us"));
        host.SetSetting("tts-model", "tts-rt-v1");
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));
        await sut.ActivateAsync(host);

        Assert.Equal("tts-rt-v1", sut.EffectiveTtsModelId);
        Assert.Equal(["Daniel", "Nina"], sut.AvailableVoices.Select(voice => voice.Id));
        Assert.Equal("Daniel", sut.SelectedVoiceId);
    }

    [Fact]
    public async Task Voices_DefaultToAdrianAndPersistSelection()
    {
        var host = new FeatureHost();
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()));
        await sut.ActivateAsync(host);

        Assert.True(sut.SupportsPlaybackSelection);
        Assert.Equal("Adrian", sut.SelectedVoiceId);
        sut.SelectVoice("Grace");
        Assert.Equal("Grace", host.GetSetting<string>("speech-voice"));
        Assert.Throws<ArgumentException>(() => sut.SelectVoice("Nobody"));
        sut.SelectVoice(null);
        Assert.Equal("Adrian", sut.SelectedVoiceId);
    }

    private static SonioxCatalog Catalog(string region) => new(
        region,
        DateTimeOffset.UtcNow,
        SonioxModels.ParseSttModels(ModelsJson),
        SonioxModels.ParseTtsModels(TtsModelsJson));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FlowHandler(Action<string> inspectCreate, string transcript) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/files")
                return Json("""{ "id": "file-1" }""", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Post && path == "/v1/transcriptions")
            {
                inspectCreate(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json("""{ "id": "job-1" }""", HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && path == "/v1/transcriptions/job-1")
                return Json("""{ "status": "completed", "audio_duration_ms": 5000 }""");
            if (request.Method == HttpMethod.Get && path == "/v1/transcriptions/job-1/transcript")
                return Json(transcript);
            if (request.Method == HttpMethod.Delete)
                return Json("{}");
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakePlayback : ITtsPlaybackSession
    {
        public bool IsActive => true;
        public event EventHandler? Completed { add { } remove { } }
        public void Stop() { }
    }

    private sealed class FeatureHost : IPluginHostServices
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCount { get; private set; }
        public string Language { get; init; } = "en";

        public Task StoreSecretAsync(string key, string value) { Secrets[key] = value; return Task.CompletedTask; }
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult(Secrets.GetValueOrDefault(key));
        public Task DeleteSecretAsync(string key) { Secrets.Remove(key); return Task.CompletedTask; }
        public T? GetSetting<T>(string key) => _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(Options) : default;
        public void SetSetting<T>(string key, T value) => _settings[key] = JsonSerializer.SerializeToElement(value, Options);
        public void SetRaw(string key, string json) => _settings[key] = JsonDocument.Parse(json).RootElement.Clone();
        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoEvents();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCount++;
        public IPluginLocalization Localization => new FixedLocalization(Language);
    }

    private sealed class FixedLocalization(string language) : IPluginLocalization
    {
        public string CurrentLanguage => language;
        public IReadOnlyList<string> AvailableLanguages => ["en", "de"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class NoEvents : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
