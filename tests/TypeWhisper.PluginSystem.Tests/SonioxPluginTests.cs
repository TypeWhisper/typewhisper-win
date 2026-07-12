using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class SonioxPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new SonioxPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void PluginVersion_IsSonioxMarketplaceReleaseVersion()
    {
        var manifest = LoadManifest();
        var sut = new SonioxPlugin();

        Assert.Equal("1.0.4", manifest.GetProperty("version").GetString());
        Assert.Equal("1.0.4", sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesTranscriptionCapabilitiesAndApiKeyRequirement()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.soniox", manifest.GetProperty("id").GetString());
        Assert.Equal("Soniox", manifest.GetProperty("name").GetString());
        Assert.Equal("transcription", manifest.GetProperty("category").GetString());
        Assert.Equal(["transcription"], manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(manifest.GetProperty("isLocal").GetBoolean());
        Assert.True(manifest.GetProperty("requiresApiKey").GetBoolean());
    }

    [Fact]
    public async Task ActivateAsync_RestoresApiKeyAndExposesAsyncModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";

        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.soniox", sut.PluginId);
        Assert.Equal("Soniox", sut.PluginName);
        Assert.Equal("soniox", sut.ProviderId);
        Assert.Equal("Soniox", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("default", sut.SelectedModelId);
        Assert.Equal(["default"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" soniox-key ");
        await sut.SetApiKeyAsync("soniox-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(host.Secrets.ContainsKey("api-key"));
        Assert.False(sut.IsConfigured);
    }

    [Fact]
    public async Task SetApiKeyAsync_DoesNotConfigurePluginWhenStoreSecretFails()
    {
        var host = new TestPluginHostServices
        {
            StoreSecretException = new InvalidOperationException("store failed")
        };
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync("soniox-key"));

        Assert.False(sut.IsConfigured);
        Assert.Null(sut.ApiKey);
        Assert.False(host.Secrets.ContainsKey("api-key"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_KeepsExistingConfigurationWhenDeleteSecretFails()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);
        host.DeleteSecretException = new InvalidOperationException("delete failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync(""));

        Assert.True(sut.IsConfigured);
        Assert.Equal("soniox-key", sut.ApiKey);
        Assert.Equal("soniox-key", host.Secrets["api-key"]);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesModelsEndpointAndBearerHeader()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.soniox.com/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer probe-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""{ "models": [] }""");
        });

        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync(" probe-key "));
    }

    [Fact]
    public async Task ActivateAsync_UsesPersistedRegionEndpoint()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://api.jp.soniox.com/v1/models", request.RequestUri?.ToString());
            return JsonResponse("""{ "models": [] }""");
        });

        var host = new TestPluginHostServices();
        host.SetSetting("region", "jp");
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Equal("jp", sut.RegionId);
        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
    }

    [Fact]
    public async Task ActivateAsync_FallsBackToUsForUnknownRegion()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("region", "mars");
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("us", sut.RegionId);
    }

    [Fact]
    public async Task SetRegion_PersistsRegionAndSwitchesEndpoint()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://api.eu.soniox.com/v1/models", request.RequestUri?.ToString());
            return JsonResponse("""{ "models": [] }""");
        });

        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);
        await sut.ActivateAsync(host);

        sut.SetRegion("eu");

        Assert.Equal("eu", sut.RegionId);
        Assert.Equal("eu", host.GetSetting<string>("region"));
        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
    }

    [Fact]
    public async Task DetectRegionAsync_ReturnsRegionWhereKeyAuthenticates()
    {
        var handler = new CapturingHandler((request, _) =>
            request.RequestUri?.ToString() == "https://api.jp.soniox.com/v1/models"
                ? JsonResponse("""{ "models": [] }""")
                : JsonResponse("""{ "error": "unauthorized" }""", HttpStatusCode.Unauthorized));

        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Equal("jp", await sut.DetectRegionAsync("probe-key"));
    }

    [Fact]
    public async Task DetectRegionAsync_ReturnsNullWhenNoRegionAccepts()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "error": "unauthorized" }""", HttpStatusCode.Unauthorized));

        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Null(await sut.DetectRegionAsync("probe-key"));
    }

    [Fact]
    public async Task TranscribeWithLanguageHintsAsync_PreservesOrderAndCleansUp()
    {
        var seen = new List<string>();
        var handler = new CapturingHandler((request, body) =>
        {
            seen.Add($"{request.Method} {request.RequestUri}");
            Assert.Equal("Bearer soniox-key", request.Headers.Authorization?.ToString());

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                Assert.StartsWith("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);
                Assert.NotNull(body);
                var multipartBody = Encoding.UTF8.GetString(body);
                Assert.Contains("name=file", multipartBody);
                Assert.Contains("filename=audio.wav", multipartBody);
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753", "filename": "audio.wav", "size": 3 }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
            {
                using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
                var root = doc.RootElement;
                Assert.Equal("stt-async-v4", root.GetProperty("model").GetString());
                Assert.Equal("84c32fc6-4fb5-4e7a-b656-b5ec70493753", root.GetProperty("file_id").GetString());
                Assert.Equal(["de", "en"], root.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString()!).ToArray());
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
            {
                var pollCount = seen.Count(item => item == "GET https://api.soniox.com/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721");
                return pollCount == 1
                    ? JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "processing", "audio_duration_ms": 660000 }""")
                    : JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "completed", "audio_duration_ms": 660000 }""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721/transcript")
            {
                return JsonResponse("""
                    {
                      "id": "73d4357d-cad2-4338-a60d-ec6f2044f721",
                      "text": "Hallo Welt",
                      "tokens": [
                        { "text": "Hallo", "start_ms": 0, "end_ms": 500, "language": "de" },
                        { "text": " ", "start_ms": 500, "end_ms": 520, "language": "de" },
                        { "text": "Welt", "start_ms": 520, "end_ms": 1100, "language": "de" }
                      ]
                    }
                    """);
            }

            if (request.Method == HttpMethod.Delete)
                return JsonResponse("""{}""");

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 3);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeWithLanguageHintsAsync(
            [1, 2, 3], ["de", "en"], translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(660.0, result.DurationSeconds);
        Assert.Equal(["Hallo Welt"], result.Segments.Select(s => s.Text).ToArray());
        Assert.Contains("DELETE https://api.soniox.com/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721", seen);
        Assert.Contains("DELETE https://api.soniox.com/v1/files/84c32fc6-4fb5-4e7a-b656-b5ec70493753", seen);
    }

    [Fact]
    public void ParseTranscript_GroupsTokenTimestampsIntoReadableSubtitleSegments()
    {
        using var completedDetails = JsonDocument.Parse("""{ "audio_duration_ms": 5200 }""");

        var result = SonioxPlugin.ParseTranscript(
            """
            {
              "text": "Well, this is an illustration. It has another sentence.",
              "tokens": [
                { "text": "W", "start_ms": 900, "end_ms": 960, "language": "en" },
                { "text": "ell,", "start_ms": 960, "end_ms": 1020, "language": "en" },
                { "text": "this", "start_ms": 1139, "end_ms": 1200, "language": "en" },
                { "text": "is", "start_ms": 1260, "end_ms": 1320, "language": "en" },
                { "text": "an", "start_ms": 1379, "end_ms": 1440, "language": "en" },
                { "text": "ill", "start_ms": 1560, "end_ms": 1620, "language": "en" },
                { "text": "ustration.", "start_ms": 1680, "end_ms": 2100, "language": "en" },
                { "text": "It", "start_ms": 2600, "end_ms": 2700, "language": "en" },
                { "text": "has", "start_ms": 2760, "end_ms": 2860, "language": "en" },
                { "text": "another", "start_ms": 2920, "end_ms": 3160, "language": "en" },
                { "text": "sentence.", "start_ms": 3220, "end_ms": 3600, "language": "en" }
              ]
            }
            """,
            completedDetails.RootElement,
            fallbackLanguage: null);

        Assert.Equal("Well, this is an illustration. It has another sentence.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(5.2, result.DurationSeconds);
        Assert.Equal(["Well, this is an illustration.", "It has another sentence."], result.Segments.Select(s => s.Text).ToArray());
        Assert.Equal(0.9, result.Segments[0].Start);
        Assert.Equal(2.1, result.Segments[0].End);
        Assert.Equal(2.6, result.Segments[1].Start);
        Assert.Equal(3.6, result.Segments[1].End);
    }

    [Fact]
    public void ParseTranscript_SplitsSubtitleSegmentsOnLongPauses()
    {
        using var completedDetails = JsonDocument.Parse("""{ "audio_duration_ms": 4000 }""");

        var result = SonioxPlugin.ParseTranscript(
            """
            {
              "text": "First phrase continues after pause",
              "tokens": [
                { "text": "First", "start_ms": 0, "end_ms": 300, "language": "en" },
                { "text": "phrase", "start_ms": 350, "end_ms": 700, "language": "en" },
                { "text": "continues", "start_ms": 2000, "end_ms": 2400, "language": "en" },
                { "text": "after", "start_ms": 2450, "end_ms": 2700, "language": "en" },
                { "text": "pause", "start_ms": 2750, "end_ms": 3100, "language": "en" }
              ]
            }
            """,
            completedDetails.RootElement,
            fallbackLanguage: null);

        Assert.Equal(["First phrase", "continues after pause"], result.Segments.Select(s => s.Text).ToArray());
        Assert.Equal(0.0, result.Segments[0].Start);
        Assert.Equal(0.7, result.Segments[0].End);
        Assert.Equal(2.0, result.Segments[1].Start);
        Assert.Equal(3.1, result.Segments[1].End);
    }

    [Fact]
    public void ParseTranscript_SkipsInvalidTokenTimingsWithoutReusingSkippedText()
    {
        using var completedDetails = JsonDocument.Parse("""{ "audio_duration_ms": 2000 }""");

        var result = SonioxPlugin.ParseTranscript(
            """
            {
              "text": "Bad good",
              "tokens": [
                { "text": "Bad", "start_ms": 1000, "end_ms": 1000, "language": "en" },
                { "text": "good", "start_ms": 1200, "end_ms": 1500, "language": "en" }
              ]
            }
            """,
            completedDetails.RootElement,
            fallbackLanguage: null);

        var segment = Assert.Single(result.Segments);
        Assert.Equal("good", segment.Text);
        Assert.Equal(1.2, segment.Start);
        Assert.Equal(1.5, segment.End);
    }

    [Fact]
    public async Task TranscribeAsync_UsesInitialApiKeyForWholeAsyncFlow()
    {
        var seenAuthorizations = new List<string?>();
        SonioxPlugin? sut = null;
        var handler = new AsyncCapturingHandler(async (request, _, _) =>
        {
            seenAuthorizations.Add(request.Headers.Authorization?.ToString());

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                await sut!.SetApiKeyAsync("");
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
                return JsonResponse("""{ "status": "completed", "audio_duration_ms": 1000 }""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721/transcript")
                return JsonResponse("""{ "text": "Hello", "tokens": [] }""");

            if (request.Method == HttpMethod.Delete)
                return JsonResponse("""{}""");

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "initial-key";
        using var httpClient = new HttpClient(handler);
        sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
        Assert.False(sut.IsConfigured);
        Assert.DoesNotContain("api-key", host.Secrets.Keys);
        Assert.All(seenAuthorizations, authorization => Assert.Equal("Bearer initial-key", authorization));
    }

    [Fact]
    public async Task TranscribeAsync_OmitsLanguageHintsForAuto()
    {
        var handler = new SonioxFlowHandler((createBody) =>
        {
            using var doc = JsonDocument.Parse(createBody);
            Assert.False(doc.RootElement.TryGetProperty("language_hints", out _));
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "auto", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsLanguageHintsForWhitespacePaddedAuto()
    {
        var handler = new SonioxFlowHandler((createBody) =>
        {
            using var doc = JsonDocument.Parse(createBody);
            Assert.False(doc.RootElement.TryGetProperty("language_hints", out _));
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], " auto ", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
        Assert.Null(result.DetectedLanguage);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslation()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: true, prompt: null, CancellationToken.None));

        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_StatusErrorIncludesSonioxDetailsAndCleansUp()
    {
        var seen = new List<string>();
        var handler = new CapturingHandler((request, body) =>
        {
            seen.Add($"{request.Method} {request.RequestUri}");

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
            {
                return JsonResponse("""
                    {
                      "status": "error",
                      "error_type": "invalid_audio",
                      "error_message": "Cannot decode audio",
                      "request_id": "req-1"
                    }
                    """);
            }

            if (request.Method == HttpMethod.Delete)
                return JsonResponse("""{}""");

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}; body={Encoding.UTF8.GetString(body ?? [])}");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None));

        Assert.Contains("invalid_audio", ex.Message);
        Assert.Contains("Cannot decode audio", ex.Message);
        Assert.Contains("req-1", ex.Message);
        Assert.Contains("DELETE https://api.soniox.com/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721", seen);
        Assert.Contains("DELETE https://api.soniox.com/v1/files/84c32fc6-4fb5-4e7a-b656-b5ec70493753", seen);
    }

    [Fact]
    public async Task TranscribeAsync_HttpErrorIncludesSonioxDetails()
    {
        var handler = new CapturingHandler((request, _) =>
            JsonResponse("""
                {
                  "status_code": 401,
                  "error_type": "unauthenticated",
                  "message": "Incorrect API key",
                  "request_id": "req-unauth"
                }
                """, HttpStatusCode.Unauthorized));

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None));

        Assert.Contains("unauthenticated", ex.Message);
        Assert.Contains("Incorrect API key", ex.Message);
        Assert.Contains("req-unauth", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_PollTimeoutThrowsTimeoutException()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Get)
                return JsonResponse("""{ "status": "processing" }""");

            return JsonResponse("""{}""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "soniox-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None));

        Assert.Contains("did not complete", ex.Message);
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Soniox", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class SonioxFlowHandler(Action<string> inspectCreateBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                inspectCreateBody(body);
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
                return JsonResponse("""{ "status": "completed", "audio_duration_ms": 1000 }""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721/transcript")
                return JsonResponse("""{ "text": "Hello", "tokens": [] }""");

            if (request.Method == HttpMethod.Delete)
                return JsonResponse("""{}""");

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, byte[]?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class AsyncCapturingHandler(
        Func<HttpRequestMessage, byte[]?, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return await responder(request, body, cancellationToken);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public Exception? StoreSecretException { get; init; }
        public Exception? DeleteSecretException { get; set; }

        public Task StoreSecretAsync(string key, string value)
        {
            if (StoreSecretException is not null)
                throw StoreSecretException;

            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);

        public Task DeleteSecretAsync(string key)
        {
            if (DeleteSecretException is not null)
                throw DeleteSecretException;

            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
