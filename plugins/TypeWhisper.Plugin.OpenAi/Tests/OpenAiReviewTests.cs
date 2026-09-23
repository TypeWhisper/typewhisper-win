using System.Net;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"AccessToken\":\"partial\"}")]
    public async Task CorruptBundledSessionKeepsApiUsableWithoutRevivingSplitCredentials(string json)
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        host.Secrets["chatgpt-session"] = json;
        host.Secrets["oauth-access-token"] = "obsolete-access";
        host.Secrets["oauth-refresh-token"] = "obsolete-refresh";
        using var plugin = new OpenAiPlugin();
        await plugin.ActivateAsync(host);
        Assert.True(plugin.IsConfigured);
        Assert.False(plugin.HasChatGptCredentials);
        Assert.NotEmpty(plugin.TextSettings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tokens\":null}")]
    public async Task ImportWithoutTokenObjectReportsParseError(string json)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, json);
            using var plugin = new OpenAiPlugin();
            await plugin.ActivateAsync(new TestPluginHostServices());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ImportExistingLoginAsync(file));
            Assert.Equal("Existing login file could not be parsed.", error.Message);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"refresh_token\":\"refresh\"}")]
    [InlineData("{\"access_token\":\" \",\"refresh_token\":\"refresh\"}")]
    public async Task IncompleteTokenResponseIsAnAuthenticationFailure(string json)
    {
        using var client = new HttpClient(new CapturingHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StringContent(json) })));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => OpenAiOAuthClient.RefreshTokenAsync(client, "fixture", default));
        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
    }

    [Fact]
    public void ResponsesPreserveTextBlockBoundaries()
    {
        Assert.Equal("First paragraph\nSecond paragraph", OpenAiResponsesClient.ParseResponse(
            """{"output":[{"content":[{"type":"output_text","text":"First paragraph"},{"type":"output_text","text":"Second paragraph"}]}]}"""));
    }
}

