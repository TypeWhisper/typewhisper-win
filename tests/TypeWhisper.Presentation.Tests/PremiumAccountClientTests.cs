using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class PremiumAccountClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static PremiumAccountEntitlement Signed(ECDsa key)
    {
        var entry = new PremiumAccountEntitlement("active", "individual", "polar", true, null, 3, DateTimeOffset.UtcNow, null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
        return entry with { Signature = Encode(payload) + "." + Encode(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }
    private static string PublicKey(ECDsa key) { var p = key.ExportParameters(false); return Convert.ToBase64String([.. p.Q.X!, .. p.Q.Y!]); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request);
    }
    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    [Fact]
    public async Task FullExchangeUsesPkceLinksLicenseAndReloadsVerifiedSession()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var entitlement = Signed(key); string? persisted = null, challenge = null;
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath; paths.Add(path);
            Assert.Equal("windows", Assert.Single(request.Headers.GetValues("X-TypeWhisper-Platform")));
            Assert.Equal("2", Assert.Single(request.Headers.GetValues("X-TypeWhisper-Entitlement-Version")));
            if (path.EndsWith("/start"))
            {
                Assert.Null(request.Headers.Authorization);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
                challenge = body.GetProperty("codeChallenge").GetString();
                Assert.Equal(64, body.GetProperty("nonceHash").GetString()!.Length);
                return Ok(new { authorizationURL = "https://appleid.apple.com/auth/authorize", state = "state", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
            }
            if (path.EndsWith("/exchange"))
            {
                Assert.Null(request.Headers.Authorization);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(challenge, Encode(SHA256.HashData(Encoding.UTF8.GetBytes(body.GetProperty("codeVerifier").GetString()!))));
                return Ok(new { accessToken = "test-session", entitlement });
            }
            Assert.Equal("test-session", request.Headers.Authorization!.Parameter);
            var proof = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("test-license", proof.GetProperty("licenseKey").GetString());
            return Ok(new { entitlement });
        }));
        var client = new PremiumAccountClient(http, "device", s => persisted = s, publicKey: PublicKey(key));
        await client.BeginAsync(default);
        Assert.Null(persisted);
        await client.CompleteAsync(new("typewhisper://premium-auth/callback?state=state&code=code"), "test-license", "test-activation", default);
        Assert.True(client.SignedIn); Assert.True(client.Entitlement!.IsActive);
        var reload = new PremiumAccountClient(http, "device", _ => { }, persisted, PublicKey(key));
        Assert.True(reload.SignedIn); Assert.Equal(entitlement, reload.Entitlement);
        Assert.Equal(new[] { "/v1/auth/apple/web/start", "/v1/auth/apple/web/exchange", "/v1/entitlements/polar/device/attach" }, paths);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CompleteAsync(new("typewhisper://premium-auth/callback?state=state&code=code"), null, null, default));
    }

    [Fact]
    public void TamperedOrUnsignedEntitlementNeverGrantsAccess()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var http = new HttpClient();
        var client = new PremiumAccountClient(http, "device", _ => { }, publicKey: PublicKey(key));
        var signed = Signed(key);
        Assert.Equal(signed, client.Verify(signed));
        Assert.Throws<InvalidDataException>(() => client.Verify(signed with { Tier = "team" }));
        Assert.Throws<InvalidDataException>(() => client.Verify(signed with { Signature = null }));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => client.Verify(Signed(other)));
    }

    [Theory]
    [InlineData("http://appleid.apple.com/auth/authorize")]
    [InlineData("https://appleid.apple.com.evil.invalid/auth/authorize")]
    [InlineData("https://appleid.apple.com:8443/auth/authorize")]
    public async Task RejectsUntrustedBrowserAddress(string address)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Ok(new { authorizationURL = address, state = "state", expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) }))));
        var client = new PremiumAccountClient(http, "device", _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BeginAsync(default));
        Assert.False(client.WaitingForBrowser);
    }

    [Theory]
    [InlineData("typewhisper://premium-auth/callback?state=wrong&code=secret")]
    [InlineData("typewhisper://premium-auth/callback?state=state&state=state&code=secret")]
    [InlineData("typewhisper://other/callback?state=state&code=secret")]
    public async Task RejectsMismatchedOrDuplicateCallbackWithoutExchanging(string callback)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Ok(new { authorizationURL = "https://appleid.apple.com/auth/authorize", state = "state", expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) })); }));
        var client = new PremiumAccountClient(http, "device", _ => { }); await client.BeginAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CompleteAsync(new(callback), null, null, default));
        Assert.Equal(1, calls); Assert.False(client.SignedIn);
    }

    [Fact]
    public async Task UnauthorizedRefreshClearsSavedSession()
    {
        string? saved = "present";
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var client = new PremiumAccountClient(http, "device", value => saved = value, "{\"accessToken\":\"test-token\",\"entitlement\":null}");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshAsync(default));
        Assert.Null(saved); Assert.False(client.SignedIn); Assert.Null(client.Entitlement);
    }

    [Fact]
    public void ActivationRoutesCallbackWithoutExposingCodeInDiagnostics()
    {
        var request = ApplicationActivationRequest.Parse(["typewhisper://premium-auth/callback?state=state&code=secret"]);
        Assert.NotNull(request.AccountCallback); Assert.Null(request.Error);
        Assert.DoesNotContain("secret", request.ToString());
        var commandLine = ApplicationActivationRequest.ParseCommandLine("\"C:\\App\\TypeWhisper.exe\" \"typewhisper://premium-auth/callback?state=state&code=secret\"");
        Assert.NotNull(commandLine.AccountCallback);
    }

    [Fact]
    public async Task ExpiredStartCannotCreatePendingAuthorization()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Ok(new { authorizationURL = "https://appleid.apple.com/auth/authorize", state = "state", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }))));
        var client = new PremiumAccountClient(http, "device", _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BeginAsync(default));
        Assert.False(client.WaitingForBrowser);
    }

    [Fact]
    public async Task CancelDiscardsPendingCallback()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Ok(new { authorizationURL = "https://appleid.apple.com/auth/authorize", state = "state", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }))));
        var client = new PremiumAccountClient(http, "device", _ => { });
        await client.BeginAsync(default);
        client.Cancel();
        Assert.False(client.Accepts(new("typewhisper://premium-auth/callback?state=state&code=code")));
        Assert.False(client.WaitingForBrowser);
    }

    [Fact]
    public async Task SignOutDetachesDeviceAndClearsSessionWithoutDeletingAccount()
    {
        string? saved = "present";
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/v1/entitlements/polar/device/current", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Ok(new { ok = true }));
        }));
        var client = new PremiumAccountClient(http, "device", value => saved = value, "{\"accessToken\":\"test-token\",\"entitlement\":null}");
        await client.SignOutAsync(default);
        Assert.Null(saved);
        Assert.False(client.SignedIn);
    }
}
