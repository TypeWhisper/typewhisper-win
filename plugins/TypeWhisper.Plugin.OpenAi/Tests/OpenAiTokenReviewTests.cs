using System.Text;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Theory]
    [InlineData("{\"access_token\":\"new-access\"}", "existing-refresh")]
    [InlineData("{\"access_token\":\"new-access\",\"refresh_token\":\"rotated\"}", "rotated")]
    public async Task RefreshPreservesTheExistingTokenUnlessRotated(string json, string expectedRefresh)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse(json))));
        var tokens = await OpenAiOAuthClient.RefreshTokenAsync(client, "existing-refresh", default);
        Assert.Equal("new-access", tokens.AccessToken);
        Assert.Equal(expectedRefresh, tokens.RefreshToken);
    }

    [Theory]
    [InlineData("{\"access_token\":\"access\"}")]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\" \"}")]
    public async Task InitialAuthorizationStillRequiresARefreshToken(string json)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse(json))));
        await Assert.ThrowsAsync<PluginRequestException>(() => OpenAiOAuthClient.ExchangeAuthorizationCodeAsync(
            client, "code", new("verifier", "challenge"), default));
    }

    [Theory]
    [InlineData("{}", "access-account", "access-plan")]
    [InlineData("{\"chatgpt_account_id\":\"id-account\"}", "id-account", "access-plan")]
    [InlineData("{\"chatgpt_plan_type\":\"id-plan\"}", "access-account", "id-plan")]
    public void MissingIdTokenClaimsFallBackIndividually(string idClaims, string account, string plan)
    {
        var tokens = new OpenAiOAuthTokenResponse(Token(idClaims), Token(
            """{"https://api.openai.com/auth":{"chatgpt_account_id":"access-account","chatgpt_plan_type":"access-plan"}}"""), "refresh", 3600);
        var metadata = OpenAiOAuthClient.ExtractMetadata(tokens);
        Assert.Equal(account, metadata.AccountId);
        Assert.Equal(plan, metadata.PlanType);
        Assert.Equal("preferred", OpenAiOAuthClient.ExtractMetadata(tokens, "preferred").AccountId);
    }

    private static string Token(string claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
}
