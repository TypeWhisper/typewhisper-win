using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// A signed entitlement names no device or account, so a stale copy counts only until a refresh fails.
public sealed class PremiumEntitlementFreshnessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string PublicKey(ECDsa key) { var p = key.ExportParameters(false); return Convert.ToBase64String([.. p.Q.X!, .. p.Q.Y!]); }

    private static PremiumAccountEntitlement Signed(ECDsa key, DateTimeOffset verifiedAt, DateTimeOffset? issuedAt = null)
    {
        var entry = new PremiumAccountEntitlement("active", "individual", "polar", true, null, 3, verifiedAt, null, issuedAt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
        return entry with { Signature = Encode(payload) + "." + Encode(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }

    [Fact]
    public void StaleClaimsCountOnlyUntilARefreshFails()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var fresh = new PremiumAccountEntitlement("active", "individual", "polar", true, null, 3, now.AddDays(-29), null);
        var stale = fresh with { VerifiedAt = now.AddDays(-31) };
        Assert.True(fresh.IsActiveAt(now, refreshFailed: true));
        Assert.True(stale.IsActiveAt(now, refreshFailed: false));
        Assert.False(stale.IsActiveAt(now, refreshFailed: true));
        Assert.False((fresh with { ExpiresAt = now.AddMinutes(-1) }).IsActiveAt(now, refreshFailed: false));
        Assert.False((fresh with { Status = "revoked" }).IsActiveAt(now, refreshFailed: false));
    }

    [Fact]
    public void TheSigningTimeDecidesOnceTheServerSendsIt()
    {
        // A lifetime license confirmed long ago, in an envelope signed yesterday, stays active while offline.
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var lifetime = new PremiumAccountEntitlement("active", "individual", "polar", true, null, 3, now.AddYears(-1), null, now.AddDays(-1));
        Assert.True(lifetime.IsActiveAt(now, refreshFailed: true));
        Assert.False((lifetime with { IssuedAt = now.AddDays(-31) }).IsActiveAt(now, refreshFailed: true));
        Assert.True((lifetime with { IssuedAt = now.AddDays(-31) }).IsActiveAt(now, refreshFailed: false));
    }

    [Fact]
    public void TheServersEnvelopeWithItsSigningTimeVerifies()
    {
        // As the server writes it: the claims in this order, then the response adds the signature beside them.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claims = """{"status":"active","tier":"individual","source":"polar","isLifetime":true,"expiresAt":null,"deviceLimit":3,"verifiedAt":"2025-01-02T03:04:05.000Z","issuedAt":"2026-11-20T11:00:00.123Z"}""";
        var bytes = System.Text.Encoding.UTF8.GetBytes(claims);
        var signature = Encode(bytes) + "." + Encode(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var response = JsonSerializer.Deserialize<PremiumAccountEntitlement>(claims[..^1] + ",\"signature\":\"" + signature + "\"}", Json);
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline")));
        var client = new PremiumAccountClient(http, "device", _ => { }, null, PublicKey(key));

        var verified = client.Verify(response);

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 11, 0, 0, 123, TimeSpan.Zero), verified?.IssuedAt);
    }

    [Fact]
    public async Task AFailedRefreshKeepsALifetimeLicenseSignedRecently()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline")));
        var signed = Signed(key, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddDays(-2));
        var saved = JsonSerializer.Serialize(new { accessToken = "test-token", entitlement = signed }, Json);
        var client = new PremiumAccountClient(http, "device", _ => { }, saved, PublicKey(key));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshAsync(default));

        Assert.True(client.HasActiveEntitlement);
        Assert.Equal(signed.IssuedAt, client.Entitlement?.IssuedAt);
    }

    [Fact]
    public async Task AFailedRefreshEndsAStaleEntitlementAndASuccessfulOneRestoresIt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stale = Signed(key, DateTimeOffset.UtcNow.AddDays(-40));
        var online = false;
        using var http = new HttpClient(new Handler(_ => online
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { entitlement = Signed(key, DateTimeOffset.UtcNow) }) }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var saved = JsonSerializer.Serialize(new { accessToken = "test-token", entitlement = stale }, Json);
        var client = new PremiumAccountClient(http, "device", _ => { }, saved, PublicKey(key));
        Assert.True(client.HasActiveEntitlement);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshAsync(default));
        Assert.True(client.SignedIn);
        Assert.False(client.HasActiveEntitlement);

        online = true;
        await client.RefreshAsync(default);
        Assert.True(client.HasActiveEntitlement);
    }

    [Fact]
    public async Task AFailedRefreshKeepsARecentEntitlement()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline")));
        var saved = JsonSerializer.Serialize(new { accessToken = "test-token", entitlement = Signed(key, DateTimeOffset.UtcNow.AddDays(-3)) }, Json);
        var client = new PremiumAccountClient(http, "device", _ => { }, saved, PublicKey(key));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshAsync(default));

        Assert.True(client.HasActiveEntitlement);
    }
}
