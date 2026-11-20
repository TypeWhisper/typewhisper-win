using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Zoom;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public partial class ZoomTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static ZoomPlugin Plugin(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        Func<string, ZoomRequest, CancellationToken, Task<IStreamingSession>>? connectLive = null) => new(new HttpClient(new Handler(handler)), connectLive);
    private static ZoomPlugin Offline() => Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network call"));
    private static TestPluginHostServices Host()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture-key"; return host;
    }
    private static byte[] Wav(double seconds)
    {
        var data = (int)(seconds * 32000);
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav); BitConverter.GetBytes(36 + data).CopyTo(wav, 4); "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(wav, 16); BitConverter.GetBytes((short)1).CopyTo(wav, 20); BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16000).CopyTo(wav, 24); BitConverter.GetBytes(32000).CopyTo(wav, 28); BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34); "data"u8.CopyTo(wav.AsSpan(36)); BitConverter.GetBytes(data).CopyTo(wav, 40);
        return wav;
    }

    private const string GermanResponse = """
        {"request_id":"r","duration_sec":5.64,"result":{"text_display":"Bitte schick mir den Entwurf.","segments":[
        {"id":"utt_0000","start":0.12,"end":2.5,"text":"Bitte schick mir","words":[],"language":"de"},
        {"id":"utt_0001","start":2.5,"end":5.64,"text":"den Entwurf.","words":[]}],"language":"de"},"model":"zoom-scribe-multilingual"}
        """;

    [Theory]
    [InlineData(null, "zoom-scribe-multilingual", 10, false)]
    [InlineData("stale", "zoom-scribe-multilingual", 10, false)]
    [InlineData("zoom-scribe-language", "zoom-scribe-language", 9, true)]
    [InlineData("zoom-scribe-en-pro", "zoom-scribe-en-pro", 1, true)]
    public async Task ModelsDecideLanguagesAndDictionarySupport(string? stored, string expected, int languages, bool dictionary)
    {
        using var plugin = Offline();
        var host = Host();
        if (stored is not null) host.SetSetting("configuration", new { Model = stored, Mode = "live", SecretName = "api-key" });
        await plugin.ActivateAsync(host);
        Assert.Equal(expected, plugin.SelectedModelId); Assert.Equal(languages, plugin.SupportedLanguages.Count);
        Assert.Equal(dictionary, plugin.SupportsDictionaryTerms);
        Assert.True(plugin.IsConfigured); Assert.True(plugin.SupportsStreaming); Assert.True(plugin.SupportsStreamingCompletion);
        Assert.True(plugin.SupportsStructuredDictionaryTerms); Assert.False(plugin.SupportsTranslation);
        Assert.Equal(new DictionaryTermsBudget(MaxTerms: 100, MaxCharsPerTerm: 100), plugin.DictionaryTermsBudget);
        Assert.Equal(3, plugin.TextSettings.Count); Assert.Single(plugin.SettingsActions);
        Assert.Equal(["zoom-scribe-multilingual", "zoom-scribe-language", "zoom-scribe-en-pro"], plugin.TranscriptionModels.Select(m => m.Id));
        Assert.True(Assert.Single(plugin.TranscriptionModels, m => m.IsRecommended).Id == "zoom-scribe-multilingual");
        await plugin.DeactivateAsync(); Assert.False(plugin.IsConfigured);
    }

    [Theory]
    [InlineData("zoom-scribe-multilingual", "de", "zoom-scribe-multilingual", "de-DE", false)]
    [InlineData("zoom-scribe-multilingual", "ko", "zoom-scribe-multilingual", "ko-KR", false)]
    [InlineData("zoom-scribe-multilingual", null, "zoom-scribe-multilingual", null, false)]
    [InlineData("zoom-scribe-multilingual", "auto", "zoom-scribe-multilingual", null, false)]
    [InlineData("zoom-scribe-multilingual", "nl", "zoom-scribe-multilingual", null, false)]
    [InlineData("zoom-scribe-language", "de", "zoom-scribe-de", "de-DE", true)]
    [InlineData("zoom-scribe-language", "pt-PT", "zoom-scribe-pt", "pt-BR", true)]
    [InlineData("zoom-scribe-language", "zh_Hans", "zoom-scribe-zh", "zh-CN", true)]
    [InlineData("zoom-scribe-language", "ko", "zoom-scribe-multilingual", "ko-KR", false)]
    [InlineData("zoom-scribe-language", null, "zoom-scribe-multilingual", null, false)]
    [InlineData("zoom-scribe-en-pro", "EN", "zoom-scribe-en-pro", "en-US", true)]
    [InlineData("zoom-scribe-en-pro", "de", "zoom-scribe-multilingual", "de-DE", false)]
    [InlineData("zoom-scribe-en-pro", null, "zoom-scribe-multilingual", null, false)]
    public void RequestsPickTheModelAndLocaleForTheLanguage(string model, string? language, string expectedModel, string? locale, bool vocabulary)
    {
        var request = ZoomRequest.For(ZoomModels.Require(model), language, "TypeWhisper, Hillger");
        Assert.Equal(expectedModel, request.Model); Assert.Equal(locale, request.Locale);
        Assert.Equal(vocabulary ? ["TypeWhisper", "Hillger"] : [], request.Vocabulary);
        var config = JsonSerializer.SerializeToElement(request.Config());
        Assert.Equal(expectedModel, config.GetProperty("model").GetString());
        if (locale is null)
        {
            Assert.False(config.TryGetProperty("language", out _));
            Assert.Equal("continuous", config.GetProperty("language_detection").GetProperty("mode").GetString());
        }
        else
        {
            Assert.Equal(locale, config.GetProperty("language").GetString()); Assert.False(config.TryGetProperty("language_detection", out _));
        }
        Assert.Equal(vocabulary, config.TryGetProperty("vocabulary", out _));
        Assert.False(config.TryGetProperty("interim_results", out _));
    }

    [Fact]
    public void DictionaryTermsKeepTheirPunctuationAndStayInZoomsBudget()
    {
        string[] terms = ["Washington, D.C.", "Äpfel; Öl"];
        var request = ZoomRequest.For(ZoomModels.Language, "de", PluginDictionaryTerms.CreateStructuredPrompt(terms));
        Assert.Equal(terms, request.Vocabulary);
        var many = string.Join(", ", Enumerable.Range(0, 150).Select(i => "Term" + i).Append(new string('x', 101)));
        Assert.Equal(100, ZoomRequest.For(ZoomModels.EnglishPro, "en", many).Vocabulary.Count);
    }

    [Fact]
    public async Task RecordingsGoToTheFastModeWithTheLanguageModelAndTerms()
    {
        HttpRequestMessage? sent = null; JsonElement body = default;
        using var plugin = Plugin(async (request, ct) =>
        {
            sent = request; body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(ct));
            return Json(GermanResponse.Replace(",\"language\":\"de\"}", "}"));
        });
        var host = Host(); await plugin.ActivateAsync(host);
        await plugin.SaveTextSettingAsync("selectedModel", "zoom-scribe-language", default);
        var wav = Wav(0.5);
        var result = await plugin.TranscribeAsync(wav, "de", false, "TypeWhisper", default);

        Assert.Equal(HttpMethod.Post, sent!.Method); Assert.Equal(ZoomPlugin.FastUrl, sent.RequestUri!.ToString());
        Assert.Equal("Bearer fixture-key", sent.Headers.Authorization!.ToString());
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("data:audio/wav;base64," + Convert.ToBase64String(wav), body.GetProperty("file").GetString());
        var config = body.GetProperty("config");
        Assert.Equal("zoom-scribe-de", config.GetProperty("model").GetString()); Assert.Equal("de-DE", config.GetProperty("language").GetString());
        Assert.Equal("TypeWhisper", Assert.Single(config.GetProperty("vocabulary").GetProperty("phrases").EnumerateArray()).GetString());

        Assert.Equal("Bitte schick mir den Entwurf.", result.Text);
        // The segment's language counts when the result has none.
        Assert.Equal("de", result.DetectedLanguage); Assert.Equal(5.64, result.DurationSeconds);
        Assert.Equal([("Bitte schick mir", 0.12, 2.5), ("den Entwurf.", 2.5, 5.64)], result.Segments.Select(s => (s.Text, s.Start, s.End)));
    }

    [Fact]
    public void FastResultFallsBackToTheSegmentsAndTheRequestLanguage()
    {
        using var doc = JsonDocument.Parse("""
            {"result":{"segments":[{"text":" Hola "},{"start":1,"end":2,"text":"mundo."},{"text":"Adiós."},{"start":3,"end":4,"text":" "}]}}
            """);
        var result = ZoomPlugin.ParseFastResult(doc.RootElement, ZoomRequest.For(ZoomModels.Multilingual, "es", null));
        Assert.Equal("Hola mundo. Adiós.", result.Text); Assert.Equal("es", result.DetectedLanguage); Assert.Equal(2, result.DurationSeconds);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(("Hola mundo. Adiós.", 1.0, 2.0), (segment.Text, segment.Start, segment.End));
        using var detected = JsonDocument.Parse("""{"result":{"text_display":"Olá.","language":"pt-BR","segments":[]}}""");
        Assert.Equal("pt", ZoomPlugin.ParseFastResult(detected.RootElement, ZoomRequest.For(ZoomModels.Multilingual, null, null)).DetectedLanguage);
        // Chinese and Japanese segments join without spaces, also in subtitle segments.
        using var chinese = JsonDocument.Parse("""
            {"result":{"segments":[{"text":"你好。"},{"start":0,"end":1,"text":"世界"},{"text":"再见"},{"start":2,"end":3,"text":"OK"}]}}
            """);
        var cjk = ZoomPlugin.ParseFastResult(chinese.RootElement, ZoomRequest.For(ZoomModels.Multilingual, "zh", null));
        Assert.Equal("你好。世界再见 OK", cjk.Text);
        Assert.Equal(["你好。世界再见", "OK"], cjk.Segments.Select(s => s.Text));
        using var broken = JsonDocument.Parse("""{"text":"no result"}""");
        Assert.Throws<PluginRequestException>(() => ZoomPlugin.ParseFastResult(broken.RootElement, ZoomRequest.For(ZoomModels.Multilingual, null, null)));
    }

    [Theory]
    [InlineData(401, """{"message":"Invalid API key"}""", PluginRequestFailureKind.Authentication, null)]
    [InlineData(400, """{"message":"private text","reason":"UNSUPPORTED_MODEL"}""", PluginRequestFailureKind.InvalidRequest, "UNSUPPORTED_MODEL")]
    [InlineData(400, """{"reason":"has spaces and private text"}""", PluginRequestFailureKind.InvalidRequest, null)]
    [InlineData(402, "not json", PluginRequestFailureKind.RateLimit, null)]
    [InlineData(429, """{"code":124}""", PluginRequestFailureKind.RateLimit, "124")]
    [InlineData(413, "", PluginRequestFailureKind.RequestTooLarge, null)]
    [InlineData(503, "<html>private</html>", PluginRequestFailureKind.ServerError, null)]
    public async Task RejectedRequestsReportTheKindAndZoomsReasonOnly(int status, string response, PluginRequestFailureKind kind, string? reason)
    {
        using var plugin = Plugin((_, _) => Task.FromResult(Json(response, (HttpStatusCode)status)));
        await plugin.ActivateAsync(Host());
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(Wav(0.1), "de", false, null, default));
        Assert.Equal(kind, ex.FailureKind); Assert.Equal(status, ex.HttpStatusCode);
        Assert.DoesNotContain("private", ex.Message);
        if (reason is null) Assert.DoesNotContain("(", ex.Message); else Assert.Contains($"({reason})", ex.Message);
    }

    [Fact]
    public async Task AudioOverTheBodyLimitAsksTheHostToSplitIt()
    {
        using var plugin = Offline(); await plugin.ActivateAsync(Host());
        Assert.Equal(44 + 45 * 32000, plugin.MaximumAudioUploadBytes);
        // The largest piece the host sends fits with 100 long dictionary terms.
        var terms = string.Join(", ", Enumerable.Range(0, 100).Select(i => i + new string('x', 97)));
        await plugin.SaveTextSettingAsync("selectedModel", "zoom-scribe-en-pro", default);
        Assert.True(ZoomPlugin.FastBody(Wav(45), plugin.RequestFor("en", terms)).Length <= ZoomPlugin.MaximumBodyBytes);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(Wav(50), "en", false, terms, default));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, ex.FailureKind); Assert.Equal(413, ex.HttpStatusCode);
    }

    [Theory]
    [InlineData(400, null)]
    [InlineData(200, null)]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    public async Task KeyCheckTellsAValidKeyFromAnInvalidOneWithoutAudio(int status, PluginRequestFailureKind? failure)
    {
        JsonElement body = default; string? authorization = null;
        using var plugin = Plugin(async (request, ct) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(ct));
            return Json("""{"message":"Unsupported model","reason":"UNSUPPORTED_MODEL"}""", (HttpStatusCode)status);
        });
        var host = Host(); await plugin.ActivateAsync(host);
        var check = plugin.ExecuteProfileActionAsync("zoom", "checkConnection", new Dictionary<string, string>(), " draft-key ", default);
        if (failure is null) Assert.Equal("Connection verified. No changes saved.", (await check).Message);
        else Assert.Equal(failure, (await Assert.ThrowsAsync<PluginRequestException>(() => check)).FailureKind);
        Assert.Equal("Bearer draft-key", authorization);
        Assert.Equal("data:audio/wav;base64,", body.GetProperty("file").GetString());
        Assert.Equal("typewhisper-key-check", body.GetProperty("config").GetProperty("model").GetString());
        // The check saves nothing.
        Assert.Equal("fixture-key", host.Secrets["api-key"]); Assert.Single(host.Secrets);
    }

    [Fact]
    public async Task SettingsSaveTheModelModeAndKeyTogether()
    {
        using var plugin = Offline(); var host = Host(); await plugin.ActivateAsync(host);
        await plugin.SaveProfileSettingsAsync("zoom", new Dictionary<string, string>
        { ["selectedModel"] = "zoom-scribe-en-pro", ["transcriptionMode"] = "batch" }, "new-key", default);
        Assert.Equal("zoom-scribe-en-pro", plugin.SelectedModelId); Assert.Equal(ZoomModes.Batch, plugin.Mode);
        Assert.False(plugin.SupportsStreaming);
        var secret = Assert.Single(host.Secrets); Assert.Equal("new-key", secret.Value); Assert.StartsWith("api-key-", secret.Key);

        using var restarted = Offline(); await restarted.ActivateAsync(host);
        Assert.Equal("zoom-scribe-en-pro", restarted.SelectedModelId); Assert.False(restarted.SupportsStreaming); Assert.True(restarted.IsConfigured);
        Assert.Equal("batch", restarted.TextSettings.Single(s => s.Id == "transcriptionMode").Value);

        await Assert.ThrowsAsync<ArgumentException>(() => restarted.SaveTextSettingAsync("transcriptionMode", "async", default));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.SaveTextSettingAsync("selectedModel", "zoom-scribe-de", default));
        Assert.Throws<ArgumentException>(() => restarted.SelectModel("whisper"));
        Assert.Equal("zoom-scribe-en-pro", restarted.SelectedModelId);

        await restarted.SetApiKeyAsync("");
        Assert.Empty(host.Secrets); Assert.False(restarted.IsConfigured);
        await Assert.ThrowsAsync<PluginRequestException>(() => restarted.TranscribeAsync(Wav(0.1), "en", false, null, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSettingsWriteKeepsThePreviousConfigurationAndKey(bool failSecretStore)
    {
        using var plugin = Offline(); var host = Host(); await plugin.ActivateAsync(host);
        if (failSecretStore) host.FailWrites = true; else host.FailSettings = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync("zoom",
            new Dictionary<string, string> { ["transcriptionMode"] = "batch" }, "new-key", default));
        Assert.Equal(ZoomModes.Live, plugin.Mode); Assert.Equal("fixture-key", Assert.Single(host.Secrets).Value);
    }

    [Fact]
    public async Task OnlyTheLiveModeStreamsDictations()
    {
        ZoomRequest? opened = null; string? key = null;
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network call"),
            (k, request, _) => { key = k; opened = request; return Task.FromException<IStreamingSession>(new IOException("fixture")); });
        await plugin.ActivateAsync(Host());
        await plugin.SaveTextSettingAsync("selectedModel", "zoom-scribe-language", default);
        await Assert.ThrowsAsync<IOException>(() => plugin.StartStreamingWithLanguageHintsAndPromptAsync([" ", "fr"], "Lyon", default));
        Assert.Equal("fixture-key", key);
        Assert.Equal(("zoom-scribe-fr", "fr-FR"), (opened!.Model, opened.Locale)); Assert.Equal(["Lyon"], opened.Vocabulary);
        Assert.True(((ITranscriptionEnginePlugin)plugin).SupportsStreamingForPrompt("Lyon"));

        await plugin.SaveTextSettingAsync("transcriptionMode", "batch", default);
        Assert.False(plugin.SupportsStreaming); Assert.False(((ITranscriptionEnginePlugin)plugin).SupportsStreamingForPrompt(null));
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.StartStreamingAsync("fr", default));
    }
}
