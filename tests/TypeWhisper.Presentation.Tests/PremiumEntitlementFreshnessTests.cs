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

    private static PremiumAccountEntitlement Signed(ECDsa key, DateTimeOffset verifiedAt)
    {
        var entry = new PremiumAccountEntitlement("active", "individual", "polar", true, null, 3, verifiedAt, null);
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
