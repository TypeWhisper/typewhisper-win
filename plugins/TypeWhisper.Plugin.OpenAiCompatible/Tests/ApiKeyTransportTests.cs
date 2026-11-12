using System.Net;
using System.Net.Http;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public partial class OpenAiCompatiblePluginTests
{
    [Theory]
    [InlineData("https://api.example.com/v1/models", true)]
    [InlineData("wss://api.example.com/v1/realtime", true)]
    [InlineData("http://localhost:11434/v1/models", true)]
    [InlineData("http://127.0.0.1:11434/v1/models", true)]
    [InlineData("http://[::1]:11434/v1/models", true)]
    [InlineData("ws://localhost:8080/v1/realtime", true)]
    [InlineData("http://192.168.1.5:11434/v1/models", false)]
    [InlineData("http://service.local:1234/v1/models", false)]
    [InlineData("ws://192.168.1.5:8080/v1/realtime", false)]
    public void ApiKeyIsSentOnlyOverTlsOrToThisComputer(string url, bool sendsKey)
    {
        var headers = OpenAiCompatiblePlugin.AuthenticationHeaders(new Uri(url), "secret-key");
        Assert.Equal(sendsKey, headers.ContainsKey("Authorization"));
        Assert.Equal(sendsKey, OpenAiCompatiblePlugin.SendsApiKeyTo(new Uri(url)));
        Assert.Equal(!sendsKey, OpenAiCompatiblePlugin.WithholdsApiKey(new Uri(url), "secret-key"));
        Assert.False(OpenAiCompatiblePlugin.WithholdsApiKey(new Uri(url), null));
    }

    [Fact]
    public void AzureKeyHeaderFollowsTheSameRule()
    {
        Assert.Equal("secret-key", OpenAiCompatiblePlugin.AuthenticationHeaders(new Uri("https://demo.openai.azure.com/v1/models"), "secret-key")["api-key"]);
        Assert.Empty(OpenAiCompatiblePlugin.AuthenticationHeaders(new Uri("http://demo.openai.azure.com/v1/models"), "secret-key"));
    }

    [Fact]
    public async Task HttpServerOnAnotherComputerIsReachedWithoutTheSavedKey()
    {
        var requests = 0;
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            requests++;
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("api-key"));
            return JsonResponse("""{"data":[{"id":"llama3"}]}""");
        })));
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("saved-key");

        var result = await plugin.ExecuteProfileActionAsync(Default, Default + "/check",
            new Dictionary<string, string> { [Default + "/url"] = "http://192.168.1.5:11434" }, null, default);

        Assert.Equal(1, requests);
        Assert.Contains("without the API key", result.Message);
        Assert.Contains("HTTPS", plugin.TextSettings.Single(f => f.Id == Default + "/url").Description);
    }

    [Fact]
    public async Task RejectedRequestWithoutTheWithheldKeyNamesTheReason()
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"missing key"}}""") })));
        await plugin.ActivateAsync(new TestPluginHostServices());

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ExecuteProfileActionAsync(Default, Default + "/check",
            new Dictionary<string, string> { [Default + "/url"] = "http://192.168.1.5:11434" }, "draft-key", default));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.Contains("HTTPS", error.Message);
    }

    [Fact]
    public async Task LocalAndTlsServersStillReceiveTheKey()
    {
        var seen = new List<string?>();
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.Parameter);
            return JsonResponse("""{"data":[]}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.SetApiKeyAsync("saved-key");

        foreach (var url in new[] { "http://localhost:11434", "https://api.example.com" })
        {
            var result = await plugin.ExecuteProfileActionAsync(Default, Default + "/check",
                new Dictionary<string, string> { [Default + "/url"] = url }, null, default);
            Assert.Equal("Connection verified.", result.Message);
        }

        Assert.Equal(["saved-key", "saved-key"], seen);
    }
}
