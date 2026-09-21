using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TypeWhisper.Plugin.CloudflareAsr;
using TypeWhisper.PluginSDK;

public sealed partial class ProviderTests
{
    private static Dictionary<string,string> Query(string value) => value.TrimStart('?').Split('&')
        .Select(x => x.Split('=',2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1].Replace('+',' ')));

    [Fact]
    public void BrowserRequestUsesPkceAndNoSecret()
    {
        var uri = CloudflareOAuth.AuthorizationUri("state", "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        var query = Query(uri.Query);
        Assert.Equal("https://dash.cloudflare.com/oauth2/auth",uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",query["code_challenge"]);
        Assert.Equal("S256",query["code_challenge_method"]);
        Assert.Equal("code",query["response_type"]);
        Assert.Equal("ai.read ai.write account-settings.read offline_access",query["scope"]);
        Assert.Equal(CloudflareOAuth.RedirectUri,query["redirect_uri"]);
        Assert.DoesNotContain("client_secret",query.Keys);
        Assert.DoesNotContain("code_verifier",query.Keys);
        Assert.NotEqual(CloudflareOAuth.RandomValue(),CloudflareOAuth.RandomValue());
    }

    [Theory]
    [InlineData("/callback/?state=wrong&code=secret")]
    [InlineData("/callback/?state=expected&state=expected&code=secret")]
    [InlineData("/callback/?state=expected&code=one&code=two")]
    [InlineData("/other/?state=expected&code=secret")]
    [InlineData("/callback/?state=expected&code=%0A")]
    [InlineData("/callback/?state=expected&code=secret#fragment")]
    public void InvalidCallbacksAreRejected(string target) => Assert.Null(CloudflareOAuth.ValidateCallback(target,"expected"));

    [Fact]
    public void DenialRequiresMatchingState()
    {
        Assert.Null(CloudflareOAuth.ValidateCallback("/callback/?state=wrong&error=access_denied","expected"));
        Assert.Throws<CloudflareSignInException>(() => CloudflareOAuth.ValidateCallback("/callback/?state=expected&error=access_denied","expected"));
    }

    [Fact]
    public async Task RealLoopbackIgnoresBadCallbackThenExchangesCode()
    {
        Dictionary<string,string>? authorization = null;
        using var http = new HttpClient(new Handler((request,body) =>
        {
            Assert.Equal("https://dash.cloudflare.com/oauth2/token",request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            var fields = Query(body!);
            Assert.Equal("fixture-code",fields["code"]);
            Assert.Equal("authorization_code",fields["grant_type"]);
            Assert.Equal(authorization!["redirect_uri"],fields["redirect_uri"]);
            Assert.Equal(authorization["code_challenge"],Query(CloudflareOAuth.AuthorizationUri("ignored",fields["code_verifier"]).Query)["code_challenge"]);
            Assert.False(fields.ContainsKey("client_secret"));
            return Json("""{"access_token":"access","refresh_token":"refresh","token_type":"Bearer","expires_in":3600}""");
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? callbacks = null;
        var tokens = await new CloudflareOAuth(http).SignInAsync(timeout.Token, uri =>
        {
            authorization = Query(uri.Query);
            callbacks = Task.Run(async () =>
            {
                using var client = new HttpClient();
                var redirect = authorization["redirect_uri"];
                using var bad = await client.GetAsync(redirect+"?state=bad&code=bad",timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode);
                using var good = await client.GetAsync(redirect+"?state="+authorization["state"]+"&code=fixture-code",timeout.Token);
                Assert.Equal(HttpStatusCode.OK,good.StatusCode);
                Assert.DoesNotContain("fixture-code",await good.Content.ReadAsStringAsync(timeout.Token));
            },timeout.Token);
            return Task.CompletedTask;
        },port:0);
        await callbacks!;
        Assert.Equal("access",tokens.AccessToken); Assert.Equal("refresh",tokens.RefreshToken);
    }

    [Fact]
    public async Task CancellationClosesLoopbackListener()
    {
        using var http = new HttpClient(new Handler((_,_) => throw new Exception("Unexpected HTTP")));
        using var cancellation = new CancellationTokenSource(); var port=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CloudflareOAuth(http).SignInAsync(cancellation.Token,uri =>
        {
            port = new Uri(Query(uri.Query)["redirect_uri"]).Port;
            cancellation.Cancel(); return Task.CompletedTask;
        },port:0));
        using var listener = new TcpListener(IPAddress.Loopback,port); listener.Start();
    }

    [Fact]
    public async Task OAuthPersistenceIsAtomicEncryptedAndRefreshIsSerialized()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_,body) =>
        {
            calls++;
            Assert.Equal("refresh",Query(body!)["refresh_token"]);
            return Json("""{"access_token":"renewed","refresh_token":"rotated","token_type":"Bearer","expires_in":3600}""");
        }));
        using var connection = new ProviderConnection(http); var host = new Host();
        await connection.ActivateAsync(host); await connection.SetKeyAsync("old-key");
        await connection.SaveAsync("model","whisper-large-v3-turbo",default);
        var tokens = new CloudflareTokens("oauth-access","refresh",DateTimeOffset.UtcNow.AddMinutes(-1));
        var accounts = new[] { new CloudflareAccount(new string('a',32),"Example") };
        host.FailSetting=true;
        await Assert.ThrowsAsync<IOException>(() => connection.SaveOAuthAsync(tokens,accounts,default));
        Assert.Equal("old-key",connection.Key); Assert.Single(host.Secrets);
        host.FailSetting=false;
        await connection.SaveOAuthAsync(tokens,accounts,default);
        Assert.Equal(new string('a',32),connection.Get("accountId"));
        Assert.Equal("whisper-large-v3-turbo",connection.Get("model"));
        var plain=JsonSerializer.Serialize(host.Settings);
        Assert.DoesNotContain("oauth-access",plain); Assert.DoesNotContain("refresh",plain);
        connection.Deactivate(); await connection.ActivateAsync(host);
        await Task.WhenAll(connection.EnsureAccessTokenAsync(default),connection.EnsureAccessTokenAsync(default));
        Assert.Equal(1,calls); Assert.Equal("renewed",connection.Key); Assert.Single(host.Secrets);
        await connection.SetKeyAsync("manual-key");
        Assert.False(connection.UsesOAuth); Assert.Empty(connection.Accounts); Assert.Single(host.Secrets);
    }

    [Fact]
    public async Task MultipleAccountsRequireSelectionAndFailedDiscoveryKeepsManualKey()
    {
        using var connection = new ProviderConnection(new HttpClient()); var host=new Host();
        await connection.ActivateAsync(host); await connection.SetKeyAsync("manual-key");
        var token = new CloudflareTokens("access",null,DateTimeOffset.UtcNow.AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SaveOAuthAsync(token,[],default));
        Assert.Equal("manual-key",connection.Key);
        await connection.SaveOAuthAsync(token,[new(new string('a',32),"One"),new(new string('b',32),"Two")],default);
        Assert.Empty(connection.Get("accountId")); Assert.Equal(2,connection.Accounts.Count);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("revoked-refresh")]
    public async Task ExpiredSignInIsReportedAsAuthenticationFailure(string? refresh)
    {
        using var connection = new ProviderConnection(new HttpClient(new Handler((_,_) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("private-provider-details") })));
        var host = new Host();
        await connection.ActivateAsync(host);
        await connection.SaveOAuthAsync(new("old-access",refresh,DateTimeOffset.UtcNow.AddMinutes(-1)),
            [new(new string('a',32),"Example")],default);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() => connection.EnsureAccessTokenAsync(default));
        Assert.Equal(PluginRequestFailureKind.Authentication,failure.FailureKind);
        Assert.IsType<CloudflareSignInException>(failure.InnerException);
        Assert.DoesNotContain("private-provider-details",failure.Message);
        Assert.Equal("old-access",connection.Key);
        Assert.Single(host.Secrets);
    }

    [Fact]
    public async Task TokenExchangeTimeoutDoesNotWaitForAnotherCallback()
    {
        using var http = new HttpClient(new Handler((_,_) => throw new TaskCanceledException("token exchange timed out")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task? callback = null;
        var failure = await Assert.ThrowsAsync<TaskCanceledException>(() => new CloudflareOAuth(http).SignInAsync(timeout.Token,uri =>
        {
            var query = Query(uri.Query);
            callback = Task.Run(async () =>
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(query["redirect_uri"]+"?state="+query["state"]+"&code=fixture",timeout.Token);
                Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            });
            return Task.CompletedTask;
        },port:0));
        await callback!;
        Assert.Equal("token exchange timed out",failure.Message);
        Assert.False(timeout.IsCancellationRequested);
    }

}
