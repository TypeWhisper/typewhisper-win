using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public sealed class ElevenLabsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elevenlabs-v2-" + Guid.NewGuid().ToString("N"));
    private readonly Secrets _secrets = new();
    private VocabularyHostServices Host => new(_root, secrets: _secrets);
    private static HttpResponseMessage Ok(string body = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static readonly string Transcript = """
        {"text":"Guten Tag.","language_code":"deu","words":[
          {"type":"word","text":"Guten","start":0.1,"end":0.4},
          {"type":"spacing","text":" ","start":0.4,"end":0.5},
          {"type":"word","text":"Tag.","start":0.5,"end":0.9}]}
        """;

    [Fact]
    public async Task GermanSettingsUseUtf8AndOlderHostsRejectThePackage()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("de-DE");
            using var plugin = new ElevenLabsPlugin();
            await plugin.ActivateAsync(Host);
            var text = string.Join(" ", plugin.TextSettings.Select(s => s.Title + " " + s.Description));
            Assert.Contains("Wörterbuchbegriffe", text);
            Assert.Contains("vollständige", text);
            Assert.Contains("Füllwörter und Satzabbrüche", text);
            Assert.DoesNotContain("Ã", text);
            Assert.DoesNotContain("\uFFFD", text);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
        var directory = Path.Combine(_root, "older-host-package");
        Directory.CreateDirectory(directory);
        File.Copy(typeof(ElevenLabsPlugin).Assembly.Location, Path.Combine(directory, "TypeWhisper.Plugin.ElevenLabs.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "manifest.json"), Path.Combine(directory, "manifest.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(directory, Host, new(1, 1, 4)));
    }

    [Fact]
    public async Task RestPreservesAudioLanguageOptionsKeytermsAndTimings()
    {
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.GetValues("xi-api-key").Single());
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            async Task<string> Value(string key) => await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == key).ReadAsStringAsync(ct);
            Assert.Equal("scribe_v2", await Value("model_id"));
            Assert.Equal("de", await Value("language_code"));
            Assert.Equal("false", await Value("no_verbatim"));
            Assert.Equal("true", await Value("tag_audio_events"));
            Assert.Equal("word", await Value("timestamps_granularity"));
            Assert.DoesNotContain(parts, p => p.Headers.ContentDisposition!.Name!.Trim('"') == "num_speakers");
            Assert.Equal(new byte[] { 1, 2, 3 }, await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "file").ReadAsByteArrayAsync(ct));
            var terms = await Task.WhenAll(parts.Where(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "keyterms").Select(p => p.ReadAsStringAsync(ct)));
            Assert.Equal(new[] { "TypeWhisper", "Grüße" }, terms);
            return Ok(Transcript);
        })));
        await plugin.ActivateAsync(Host);
        await plugin.SetApiKeyAsync(" test-key ");
        await plugin.SaveTextSettingAsync("noVerbatim", "false", default);
        await plugin.SaveTextSettingAsync("tagAudioEvents", "true", default);
        await plugin.SaveTextSettingAsync("numSpeakers", "0", default);
        var result = await LanguageHintTranscription.DecodeAsync(plugin, ReadOnlyMemory<float>.Empty, () => [1, 2, 3], "de", [], false, default, ["TypeWhisper", "Grüße", "typewhisper", "<invalid>"]);
        Assert.Equal("Guten Tag.", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(0.9, result.DurationSeconds);
        var segment = Assert.Single(result.Segments);
        Assert.Equal("Guten Tag.", segment.Text);
        Assert.Equal(0.1, segment.Start);
        Assert.Equal(0.9, segment.End);
    }

    [Theory]
    [InlineData(null)] [InlineData("auto")] [InlineData(" ")]
    public async Task AutomaticLanguageIsOmitted(string? language)
    {
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler((request, _) =>
        {
            Assert.DoesNotContain(Assert.IsType<MultipartFormDataContent>(request.Content), p => p.Headers.ContentDisposition!.Name!.Trim('"') == "language_code");
            return Task.FromResult(Ok(Transcript));
        })));
        await plugin.ActivateAsync(Host); await plugin.SetApiKeyAsync("test-key");
        await plugin.TranscribeAsync([1], language, false, null, default);
    }

    [Fact]
    public async Task SettingsAndCredentialsSurviveReloadAndInvalidWritesPreserveValues()
    {
        using var plugin = new ElevenLabsPlugin();
        await plugin.ActivateAsync(Host);
        Assert.Equal("scribe_v2", plugin.SelectedModelId);
        Assert.Contains("de", plugin.SupportedLanguages);
        Assert.Contains("jv", plugin.SupportedLanguages);
        Assert.DoesNotContain("jw", plugin.SupportedLanguages);
        Assert.DoesNotContain("haw", plugin.SupportedLanguages);
        Assert.DoesNotContain("vo", plugin.SupportedLanguages);
        Assert.Equal(new[] { "scribe_v2", "scribe_v2_medical" }, plugin.TranscriptionModels.Select(m => m.Id));
        Assert.All(plugin.TranscriptionModels, m => Assert.Equal(plugin.SupportedLanguages, m.LanguageCodes));
        Assert.True(plugin.TranscriptionModels.Single(m => m.Id == "scribe_v2").IsRecommended);
        Assert.True(plugin.SupportsStreaming);
        Assert.False(plugin.SupportsStreamingForPrompt("TypeWhisper"));
        Assert.True(plugin.SupportsStreamingPreviewForPrompt("TypeWhisper"));
        Assert.False(plugin.SupportsStreamingPreviewForPrompt(null));
        await plugin.SetApiKeyAsync("test-key");
        await plugin.SaveTextSettingAsync("transcriptionMode", "restOnly", default);
        await plugin.SaveTextSettingAsync("noVerbatim", "false", default);
        await plugin.SaveTextSettingAsync("useDictionaryTerms", "false", default);
        _secrets.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync("replacement"));
        Assert.Equal("test-key", plugin.ApiKey);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("numSpeakers", "33", default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("unknown", "true", default));
        await plugin.DeactivateAsync(); Assert.False(plugin.IsConfigured);
        await plugin.ActivateAsync(Host);
        Assert.True(plugin.IsConfigured); Assert.False(plugin.SupportsStreaming); Assert.False(plugin.SupportsDictionaryTerms);
        Assert.Equal("false", plugin.TextSettings.Single(s => s.Id == "noVerbatim").Value);
        _secrets.FailWrites = false;
        await plugin.SetApiKeyAsync(""); Assert.False(plugin.IsConfigured);
    }

    [Theory]
    [InlineData("tagAudioEvents", "true")]
    [InlineData("numSpeakers", "2")]
    [InlineData("numSpeakers", "0")]
    public async Task BatchOnlyOptionsDisableLivePath(string key, string value)
    {
        using var plugin = new ElevenLabsPlugin(); await plugin.ActivateAsync(Host);
        await plugin.SaveTextSettingAsync(key, value, default);
        Assert.False(plugin.SupportsStreaming);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(500, PluginRequestFailureKind.ServerError)]
    [InlineData(422, PluginRequestFailureKind.InvalidRequest)]
    public async Task ProviderErrorsAreClassifiedWithoutLeakingPayload(int status, PluginRequestFailureKind expected)
    {
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private audio test-key") }))));
        await plugin.ActivateAsync(Host); await plugin.SetApiKeyAsync("test-key");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync([1], null, false, null, default));
        Assert.Equal(expected, ex.FailureKind); Assert.Equal(status, ex.HttpStatusCode);
        Assert.DoesNotContain("private", ex.ToString()); Assert.DoesNotContain("test-key", ex.ToString());
    }

    [Theory]
    [InlineData(200, null, null, null)]
    [InlineData(401, "missing_permissions", "The API key you used is missing the permission user_read to execute this operation.", PluginRequestFailureKind.Configuration)]
    [InlineData(401, "invalid_api_key", "Invalid key", PluginRequestFailureKind.Authentication)]
    [InlineData(401, "missing_permissions", "Missing speech_to_text permission", PluginRequestFailureKind.Permission)]
    [InlineData(401, "quota_exceeded", "This request exceeds your API key quota.", PluginRequestFailureKind.Permission)]
    public async Task ValidationDistinguishesSuccessIncompleteChecksAndRejectedKeys(int httpStatus, string? status, string? message, PluginRequestFailureKind? expected)
    {
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/v1/user", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)httpStatus) { Content = new StringContent(JsonSerializer.Serialize(new { detail = new { status, message } })) });
        })));
        await plugin.ActivateAsync(Host); await plugin.SetApiKeyAsync("test-key");
        if (expected is null) await plugin.ValidateConfigurationAsync(default);
        else
        {
            var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ValidateConfigurationAsync(default));
            Assert.Equal(expected.Value, error.FailureKind);
            if (expected == PluginRequestFailureKind.Configuration)
                Assert.Contains("speech access could not be verified", error.Message);
        }
        Assert.Equal("test-key", plugin.ApiKey);
    }

    [Fact]
    public async Task CancellationAndUnsupportedTranslationDoNotUpload()
    {
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler((_, _) => throw new Exception("Unexpected upload"))));
        await plugin.ActivateAsync(Host); await plugin.SetApiKeyAsync("test-key");
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribeAsync([1], null, true, null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribeAsync([1], null, false, null, new(true)));
    }

    [Fact]
    public void KeytermsAreBoundedAndInvalidTimingsAreDropped()
    {
        var terms = ElevenLabsPlugin.ExtractKeyterms(string.Join(',', Enumerable.Range(0, 1005).Select(n => "word" + n)));
        Assert.Equal(1000, terms.Count);
        Assert.Empty(ElevenLabsPlugin.ExtractKeyterms(new string('x', 50) + ",one two three four five six,{bad}"));
        var result = ElevenLabsPlugin.ParseRestResponse("""{"text":"Test","words":[{"text":"Test","start":2,"end":1}]}""", "de");
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task MedicalModelUsesBatchRequestsWithKeytermsAndNoLiveText()
    {
        var calls = 0;
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("scribe_v2_medical", await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "model_id").ReadAsStringAsync(ct));
            Assert.Equal("Metoprolol", await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "keyterms").ReadAsStringAsync(ct));
            return Ok(Transcript);
        })));
        var host = Host;
        await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("test-key");
        Assert.True(plugin.SupportsStreaming);
        plugin.SelectModel("scribe_v2_medical");
        Assert.False(plugin.SupportsStreaming);
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.StartStreamingAsync(null, default));
        Assert.False(plugin.SupportsStreamingPreviewForPrompt("Metoprolol"));
        await plugin.TranscribeAsync([1], null, false, "Metoprolol", default);
        Assert.Equal(1, calls);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("scribe_v2_medical", plugin.SelectedModelId);
        Assert.False(plugin.SupportsStreaming);
    }

    [Fact]
    public async Task RetiredModelSelectionMovesToScribeV2()
    {
        var host = Host;
        host.SetSetting("selectedModel", "scribe_v1");
        using var plugin = new ElevenLabsPlugin();
        await plugin.ActivateAsync(host);
        Assert.Equal("scribe_v2", plugin.SelectedModelId);
    }

    [Theory]
    [InlineData("quota_exceeded", PluginRequestFailureKind.Permission, "credit quota")]
    [InlineData("missing_permissions", PluginRequestFailureKind.Permission, "Speech to Text permission")]
    [InlineData("invalid_api_key", PluginRequestFailureKind.Authentication, "rejected the API key")]
    public async Task UnauthorizedTranscriptionsNameTheCauseWithoutProviderText(string status, PluginRequestFailureKind expected, string message)
    {
        using var plugin = new ElevenLabsPlugin(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent(JsonSerializer.Serialize(new { detail = new { status, message = "private provider text" } })) }))));
        await plugin.ActivateAsync(Host); await plugin.SetApiKeyAsync("test-key");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync([1], null, false, null, default));
        Assert.Equal(expected, ex.FailureKind);
        Assert.Equal(401, ex.HttpStatusCode);
        Assert.False(ex.IsTransient);
        Assert.Contains(message, ex.Message);
        Assert.DoesNotContain("private", ex.ToString());
    }

    [Fact]
    public void SegmentsFollowSentencesAndPausesAndKeepAudioEvents()
    {
        var result = ElevenLabsPlugin.ParseRestResponse("""
            {"text":"Ja. Das ist gut. (lacht) Weiter geht es","language_code":"cmn","words":[
              {"type":"word","text":"Ja.","start":0.0,"end":0.3},
              {"type":"spacing","text":" ","start":0.3,"end":0.4},
              {"type":"word","text":"Das","start":0.4,"end":0.6},
              {"type":"spacing","text":" ","start":0.6,"end":0.6},
              {"type":"word","text":"ist","start":0.6,"end":0.8},
              {"type":"spacing","text":" ","start":0.8,"end":0.8},
              {"type":"word","text":"gut.","start":0.8,"end":1.1},
              {"type":"spacing","text":" ","start":1.1,"end":1.2},
              {"type":"audio_event","text":"(lacht)","start":1.2,"end":1.8},
              {"type":"spacing","text":" ","start":1.8,"end":3.5},
              {"type":"word","text":"Weiter","start":3.5,"end":3.8},
              {"type":"spacing","text":" ","start":3.8,"end":3.8},
              {"type":"word","text":"geht","start":3.8,"end":4.0},
              {"type":"spacing","text":" ","start":4.0,"end":4.0},
              {"type":"word","text":"es","start":4.0,"end":4.2}]}
            """, null);
        Assert.Equal("zh", result.DetectedLanguage);
        Assert.Equal(new[] { "Ja. Das ist gut.", "(lacht)", "Weiter geht es" }, result.Segments.Select(s => s.Text));
        Assert.Equal((0.0, 1.1), (result.Segments[0].Start, result.Segments[0].End));
        Assert.Equal((1.2, 1.8), (result.Segments[1].Start, result.Segments[1].End));
        Assert.Equal((3.5, 4.2), (result.Segments[2].Start, result.Segments[2].End));
        Assert.Equal(4.2, result.DurationSeconds);
    }

    [Fact]
    public void LongSpeechWithoutSentenceEndOrPauseSplitsAfterThirtySeconds()
    {
        var words = Enumerable.Range(0, 70).Select(n => new { type = "word", text = "wort", start = n * 0.5, end = n * 0.5 + 0.4 });
        var result = ElevenLabsPlugin.ParseRestResponse(JsonSerializer.Serialize(new { text = "x", words }), "de");
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(30.0, result.Segments[1].Start);
    }

    [Theory]
    [InlineData("deu", "de")] [InlineData("eng", "en")] [InlineData("cmn", "zh")] [InlineData("de", "de")] [InlineData(" YUE ", "yue")]
    public void LanguageCodesUseTwoLettersWhereTheyExist(string provider, string expected) =>
        Assert.Equal(expected, ElevenLabsLanguageCode.AppCode(provider));

    [Fact]
    public async Task PackageLoadsThroughPortableContractWithoutWpf()
    {
        var directory = Path.Combine(_root, "package");
        Directory.CreateDirectory(directory);
        File.Copy(typeof(ElevenLabsPlugin).Assembly.Location, Path.Combine(directory, "TypeWhisper.Plugin.ElevenLabs.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "manifest.json"), Path.Combine(directory, "manifest.json"));
        await using var package = await PortablePluginPackage.LoadAsync(directory, Host, new(1, 1, 5));
        Assert.IsAssignableFrom<IApiKeyPlugin>(package.Plugin);
        var engine = Assert.IsAssignableFrom<ITranscriptionEnginePlugin>(package.Plugin);
        Assert.Equal("scribe_v2", engine.SelectedModelId);
        Assert.Contains("en", engine.SupportedLanguages);
        Assert.Contains("de", engine.SupportedLanguages);
        Assert.False(engine.SupportsLocalLivePreview);
        Assert.DoesNotContain(package.Plugin.GetType().Assembly.GetReferencedAssemblies(), reference => reference.Name == "PresentationFramework");
    }

    [Fact]
    public async Task RealPackageInstallsConfiguresRestartsAndReinstallsThroughGenericRegistry()
    {
        const string id = "com.typewhisper.elevenlabs";
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var file in new[] { typeof(ElevenLabsPlugin).Assembly.Location, Path.Combine(AppContext.BaseDirectory, "manifest.json") })
            {
                using var output = zip.CreateEntry(Path.GetFileName(file)).Open();
                using var input = File.OpenRead(file); input.CopyTo(output);
            }
        }
        var payload = bytes.ToArray();
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(payload), RequestMessage = request })));
        var entry = new PortableCatalogEntry
        {
            Id = id, Name = "ElevenLabs", Version = "1.2.0", MinHostVersion = "1.1.5",
            DownloadUrl = "https://github.com/TypeWhisper/typewhisper-win/releases/download/plugin-fixture-v1.0.0/elevenlabs.zip", Size = payload.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            SupportedArchitectures = [PortablePluginCatalog.Architecture]
        };
        PortablePluginStore Store() => new(Path.Combine(_root, "store"), new(1, 1, 5), http, _ => Host);
        var store = Store(); await store.InitializeAsync();
        await store.InstallAsync(entry);
        await using (var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 5), _ => Host))
        {
            await registry.InitializeAsync();
            Assert.Empty(registry.TranscriptionProviders);
            Assert.Null(await registry.SetEnabledAsync(id, true));
            Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
            await registry.UseConfigurationAsync(id, async (plugin, _) =>
            { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("saved-key"); return true; });
            await registry.RefreshCapabilitiesAsync();
            var provider = Assert.Single(registry.TranscriptionProviders);
            Assert.True(provider.Ready);
            Assert.True(provider.SupportsStreaming);
            Assert.Contains("ar", provider.SupportedLanguages!);
            Assert.True(Assert.Single(registry.Snapshot()).ApiKeyConfigured);
            await registry.SelectModelAsync(provider.ModelStates.Single(model => model.ModelId == "scribe_v2"));
            await registry.RefreshCapabilitiesAsync();
            Assert.Contains("en", Assert.Single(registry.TranscriptionProviders).SupportedLanguages!);
            Assert.Contains("ar", Assert.Single(registry.TranscriptionProviders).SupportedLanguages!);
        }
        var restarted = Store(); await restarted.InitializeAsync();
        await using (var registry = new PortablePluginRuntimeRegistry(restarted, new(1, 1, 5), _ => Host))
        {
            await registry.InitializeAsync();
            Assert.Equal("scribe_v2", Assert.Single(registry.TranscriptionProviders).SelectedModelId);
            Assert.Null(await registry.SetEnabledAsync(id, false));
            Assert.Empty(registry.TranscriptionProviders);
            await restarted.UninstallAsync(id);
        }
        Assert.Equal("saved-key", _secrets.Value);
        var reinstall = Store(); await reinstall.InitializeAsync();
        await reinstall.InstallAsync(entry);
        await using var finalRegistry = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 5), _ => Host);
        await finalRegistry.InitializeAsync();
        Assert.Empty(finalRegistry.TranscriptionProviders);
        Assert.Null(await finalRegistry.SetEnabledAsync(id, true));
        var restored = Assert.Single(finalRegistry.TranscriptionProviders);
        Assert.True(restored.Ready);
        Assert.Equal("scribe_v2", restored.SelectedModelId);
    }

    public void Dispose()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Secrets : IPluginSecretStore
    {
        public string? Value;
        public bool FailWrites;
        public Task StoreAsync(string key, string value) { if (FailWrites) throw new IOException(); Value = value; return Task.CompletedTask; }
        public Task<string?> LoadAsync(string key) => Task.FromResult(Value);
        public Task DeleteAsync(string key) { if (FailWrites) throw new IOException(); Value = null; return Task.CompletedTask; }
    }
}
