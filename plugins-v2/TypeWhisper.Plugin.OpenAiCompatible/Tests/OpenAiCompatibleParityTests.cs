using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public partial class OpenAiCompatiblePluginTests
{
    [Theory]
    [InlineData("https://unit.openai.azure.com/openai", true)]
    [InlineData("https://unit.openai.azure.us/openai", true)]
    [InlineData("https://unit.services.ai.azure.com/openai", true)]
    [InlineData("https://openai.azure.com.attacker.invalid/openai", false)]
    [InlineData("http://localhost:1234", false)]
    public async Task VersionedDiscoveryPreservesBasePathAndScopesAzureHeaders(string baseUrl, bool azure)
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal(baseUrl + "/v1/models?api-version=2025-03-01-preview", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture", request.Headers.Authorization!.ToString());
            Assert.Equal(azure, request.Headers.Contains("api-key"));
            return JsonResponse("""{"data":[{"id":"a"}]}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.SaveProfileSettingsAsync(Default, new Dictionary<string, string>
        { [Default + "/url"] = baseUrl, [Default + "/api-version"] = "2025-03-01-preview" }, "fixture", default);
        Assert.True(await plugin.ValidateConnectionAsync());
        Assert.Single(await plugin.FetchModelsAsync());
    }

    [Fact]
    public async Task DeploymentBatchEncodesTheModelAndPreservesMultipartFields()
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            Assert.Equal("https://unit.openai.azure.com/openai/deployments/team%2Fmodel%20alias/audio/transcriptions?api-version=2025-03-01-preview", request.RequestUri!.AbsoluteUri);
            Assert.Contains("team/model alias", body!);
            Assert.Contains("fixture prompt", body!);
            Assert.True(request.Headers.Contains("api-key"));
            return JsonResponse("""{"text":"batch result"}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.SaveProfileSettingsAsync(Default, new Dictionary<string, string>
        {
            [Default + "/url"] = "https://unit.openai.azure.com/openai",
            [Default + "/api-version"] = "2025-03-01-preview",
            [Default + "/transcription"] = "team/model alias", [Default + "/transport"] = "batch",
            [Default + "/batch-endpoint"] = "deployment-scoped"
        }, "fixture", default);
        Assert.Equal("batch result", (await plugin.TranscribeAsync([0, 0], "en", false, "fixture prompt", default)).Text);
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData("high", true, false)]
    [InlineData("xhigh", true, false)]
    [InlineData("max", true, false)]
    public async Task ResponsesUsesItsOwnShapeAndReasoningOmitsTemperature(string effort, bool reasoning, bool temperature)
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            Assert.EndsWith("/v1/responses?api-version=preview", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(body!); var root = json.RootElement;
            Assert.Equal("Ändere nichts.", root.GetProperty("instructions").GetString());
            Assert.Equal("Grüße", root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("messages", out _));
            Assert.Equal(reasoning ? 27048 : 2048, root.GetProperty("max_output_tokens").GetInt32());
            Assert.False(root.TryGetProperty("thinking", out _));
            Assert.Equal(reasoning, root.TryGetProperty("reasoning", out _));
            Assert.Equal(temperature, root.TryGetProperty("temperature", out _));
            if (reasoning) Assert.Equal(effort, root.GetProperty("reasoning").GetProperty("effort").GetString());
            if (temperature) Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
            return JsonResponse("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Grüße"}]}]}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.SaveProfileSettingsAsync(new Dictionary<string, string>
        {
            [Default + "/url"] = "http://localhost:1234", [Default + "/api-version"] = "preview",
            [Default + "/text"] = "local", [Default + "/llm-api"] = "responses", [Default + "/reasoning"] = effort,
            [Default + "/temperature-mode"] = "custom", [Default + "/temperature"] = "0.7"
        }, default);
        Assert.Equal("Grüße", await plugin.ProcessAsync("Ändere nichts.", "Grüße", "local", default));
    }

    [Theory]
    [InlineData("{\"output_text\":\" answer \"}", "answer")]
    [InlineData("{\"output\":[{\"content\":[{\"type\":\"text\",\"text\":{\"value\":\"nested\"}}]}]}", "nested")]
    public void ResponsesParsesCompatibleTextShapes(string json, string expected) => Assert.Equal(expected, OpenAiCompatiblePlugin.ParseResponsesText(json));

    [Fact]
    public void IncompleteResponsesNeverReturnPartialSuccess()
    {
        var error = Assert.Throws<PluginRequestException>(() => OpenAiCompatiblePlugin.ParseResponsesText(
            """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output_text":"partial"}"""));
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }

    [Fact]
    public async Task ChatRetriesOnlyTheExplicitOutputTokenParameterMismatch()
    {
        var requests = 0;
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            if (++requests == 1)
            {
                Assert.True(json.RootElement.TryGetProperty("max_tokens", out _));
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Use max_completion_tokens instead of max_tokens"}}""") };
            }
            Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
            Assert.True(json.RootElement.TryGetProperty("max_completion_tokens", out _));
            return JsonResponse("""{"choices":[{"message":{"content":[{"type":"text","text":"OK"}]}}]}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices()); plugin.SetBaseUrl("http://localhost:1234");
        Assert.Equal("OK", await plugin.ProcessAsync("", "fixture", "model", default)); Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("auto", "gpt-live-transcribe", true)]
    [InlineData("auto", "gpt-realtime-whisper", true)]
    [InlineData("auto", "my-deployment", false)]
    [InlineData("batch", "gpt-live-transcribe", false)]
    [InlineData("realtime", "my-deployment", true)]
    public async Task RealtimeTransportIsResolvedPerProfile(string mode, string model, bool realtime)
    {
        using var plugin = new OpenAiCompatiblePlugin(); await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.ExecuteSettingsActionAsync("add", default); var id = plugin.ConnectionIdentity;
        await plugin.SaveProfileSettingsAsync(new Dictionary<string, string>
        { [id + "/transcription"] = model, [id + "/transport"] = mode }, default);
        var additional = Assert.Single(plugin.AdditionalTranscriptionEngines);
        Assert.Equal(realtime, additional.SupportsStreaming);
        Assert.False(plugin.SupportsStreaming);
        Assert.Equal(!realtime, additional.SupportsTranslation);
    }

    [Fact]
    public void RealtimeUrlAndPayloadFollowTheConfiguredServerAndModelFamily()
    {
        var profile = new OpenAiCompatibleProfile { BaseUrl = "https://unit.openai.azure.com/openai", ApiVersion = "preview" };
        Assert.Equal("wss://unit.openai.azure.com/openai/v1/realtime?api-version=preview&intent=transcription", OpenAiCompatiblePlugin.RealtimeUri(profile).AbsoluteUri);
        profile.BaseUrl = "http://localhost:1234"; Assert.Equal("ws", OpenAiCompatiblePlugin.RealtimeUri(profile).Scheme);
        using var modern = JsonDocument.Parse(CompatibleRealtimeStreamingSession.CreateSessionUpdatePayload("my-deployment", ["de", "en"], "Names"));
        var config = modern.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input");
        Assert.Equal(24000, config.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal(2, config.GetProperty("transcription").GetProperty("languages").GetArrayLength());
        Assert.False(config.GetProperty("transcription").TryGetProperty("delay", out _));
        using var legacy = JsonDocument.Parse(CompatibleRealtimeStreamingSession.CreateSessionUpdatePayload("custom-whisper", ["de", "en"], "Names", protocol: "whisper"));
        var legacyConfig = legacy.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.Equal("de", legacyConfig.GetProperty("language").GetString());
        Assert.False(legacyConfig.TryGetProperty("languages", out _));
    }
}
