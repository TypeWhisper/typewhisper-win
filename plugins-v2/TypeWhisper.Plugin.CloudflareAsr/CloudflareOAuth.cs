using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.CloudflareAsr;

internal sealed class CloudflareSignInException(string message) : InvalidOperationException(message);

internal sealed record CloudflareTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
internal sealed record CloudflareAccount(string Id, string Name);

internal sealed class CloudflareOAuth(HttpClient http)
{
    internal const string ClientId = "f3792faca0b8263152f5581e9ff28e2c";
    internal const string RedirectUri = "http://127.0.0.1:47831/callback/";
    internal const string Scopes = "ai.read ai.write account-settings.read offline_access";
    internal static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static Uri AuthorizationUri(string state, string verifier, string redirect = RedirectUri) => new(
        "https://dash.cloudflare.com/oauth2/auth?" + string.Join("&", new Dictionary<string,string> {
            ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = Scopes, ["state"] = state, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));

    internal async Task<CloudflareTokens> SignInAsync(CancellationToken ct, Func<Uri,Task>? openBrowser = null, int port = 47831)
    {
        ct.ThrowIfCancellationRequested();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(); }
        catch (SocketException) { throw new CloudflareSignInException("The Cloudflare sign-in port is busy. Close another sign-in attempt and retry."); }
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback/";
        var state = RandomValue(); var verifier = RandomValue();
        var browser = openBrowser ?? (uri => { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; });
        await browser(AuthorizationUri(state, verifier, redirect));
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            string? acceptedCode = null;
            try
            {
                using var buffer = new MemoryStream(); var one = new byte[1];
                var complete = false;
                while (buffer.Length < 16_384)
                {
                    if (await stream.ReadAsync(one, requestTimeout.Token) == 0) break;
                    buffer.WriteByte(one[0]);
                    if (buffer.Length >= 4 && Encoding.ASCII.GetString(buffer.GetBuffer(), (int)buffer.Length - 4, 4) == "\r\n\r\n") { complete = true; break; }
                }
                var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                string? target = null;
                if (complete && first.Length == 3 && first[0] == "GET"
                    && lines.Count(l => l.StartsWith("Host:",StringComparison.OrdinalIgnoreCase)) == 1
                    && lines.Any(l => l.Equals("Host: " + new Uri(redirect).Authority, StringComparison.OrdinalIgnoreCase))) target = first[1];
                string? code;
                try { code = ValidateCallback(target, state); acceptedCode = code; }
                catch (CloudflareSignInException ex) { await RespondAsync(stream, false, requestTimeout.Token, ex.Message); throw; }
                await RespondAsync(stream, code is not null, requestTimeout.Token);
                if (code is null) continue;
                return await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code,
                    ["redirect_uri"] = redirect, ["code_verifier"] = verifier }, null, ct);
            }
            catch (OperationCanceledException) when (acceptedCode is null && !ct.IsCancellationRequested)
            {
                // A stalled, unvalidated local request must not prevent the real browser callback.
                continue;
            }
            catch (IOException) when (acceptedCode is null && !ct.IsCancellationRequested)
            {
                // Ignore disconnected probes only; failures after accepting a code must reach the caller.
                continue;
            }
        }
    }

    internal static string? ValidateCallback(string? target, string state)
    {
        if (target is null || !target.StartsWith("/callback/?", StringComparison.Ordinal) || target.Contains('#')) return null;
        var pairs = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var item in target[(target.IndexOf('?')+1)..].Split('&'))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2) return null;
            string key, value;
            try { key = Uri.UnescapeDataString(pair[0].Replace('+',' ')); value = Uri.UnescapeDataString(pair[1].Replace('+',' ')); }
            catch (UriFormatException) { return null; }
            if (!pairs.TryAdd(key, value)) return null;
        }
        if (!pairs.TryGetValue("state", out var actual) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(state))) return null;
        if (pairs.TryGetValue("error",out var error)) throw new CloudflareSignInException(error switch
        {
            "invalid_scope" => "Cloudflare rejected the requested permissions. Check Workers AI Read, Workers AI Write, Account Settings Read and the refresh_token grant in the OAuth client.",
            "access_denied" => "Cloudflare sign-in was declined. Your existing connection was kept.",
            _ => "Cloudflare could not authorize this sign-in. Check the OAuth client configuration and retry."
        });
        return pairs.TryGetValue("code", out var code) && code.Length is > 0 and <= 8192 && !code.Any(char.IsControl) ? code : null;
    }

    private static async Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken ct, string? error = null)
    {
        var body = Encoding.UTF8.GetBytes(error ?? (accepted ? "Cloudflare sign-in received. Return to TypeWhisper to finish connecting." : "This request does not match the active TypeWhisper sign-in."));
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
    }

    internal Task<CloudflareTokens> RefreshAsync(CloudflareTokens previous, CancellationToken ct) =>
        previous.RefreshToken is null ? throw new CloudflareSignInException("Cloudflare sign-in expired. Connect again.") :
        ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = previous.RefreshToken }, previous.RefreshToken, ct);

    private async Task<CloudflareTokens> ExchangeAsync(Dictionary<string,string> fields, string? previousRefresh, CancellationToken ct)
    {
        fields["client_id"] = ClientId;
        using var request = new HttpRequestMessage(HttpMethod.Post,"https://dash.cloudflare.com/oauth2/token") { Content = new FormUrlEncodedContent(fields) };
        using var response = await SendOAuthAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new CloudflareSignInException($"Cloudflare sign-in could not be completed (HTTP {(int)response.StatusCode}). Connect again.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = doc.RootElement;
        var access = ProviderConnection.RequiredText(root,"access_token");
        if (!string.Equals(ProviderConnection.Text(root,"token_type"),"Bearer",StringComparison.OrdinalIgnoreCase)
            || access.Length > 65536 || access.Any(char.IsControl)
            || !root.TryGetProperty("expires_in",out var expires) || !expires.TryGetInt32(out var seconds) || seconds <= 0 || seconds > 31536000)
            throw new InvalidDataException("Cloudflare returned an invalid sign-in response.");
        var refresh = ProviderConnection.Text(root,"refresh_token") ?? previousRefresh;
        if (refresh is not null && (refresh.Length is 0 or > 65536 || refresh.Any(char.IsControl))) throw new InvalidDataException("Cloudflare returned an invalid refresh token.");
        return new(access,refresh,DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private async Task<HttpResponseMessage> SendOAuthAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException("Cloudflare could not be reached. Please retry.", PluginRequestFailureKind.Network, innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException("Cloudflare sign-in request timed out. Please retry.", PluginRequestFailureKind.Timeout, innerException: ex);
        }
        var status = (int)response.StatusCode;
        if (status is 408 or 429 or >= 500 and <= 599)
        {
            var kind = status == 429 ? PluginRequestFailureKind.RateLimit
                : status == 408 ? PluginRequestFailureKind.Timeout : PluginRequestFailureKind.ServerError;
            var retry = response.Headers.RetryAfter?.Delta;
            if (retry is null && response.Headers.RetryAfter?.Date is { } retryAt)
                retry = retryAt > DateTimeOffset.UtcNow ? retryAt - DateTimeOffset.UtcNow : TimeSpan.Zero;
            response.Dispose();
            throw new PluginRequestException("Cloudflare sign-in is temporarily unavailable. Please retry.", kind, status, retry);
        }
        return response;
    }

    internal async Task<IReadOnlyList<CloudflareAccount>> AccountsAsync(string token, CancellationToken ct)
    {
        var accounts = new List<CloudflareAccount>();
        for (var page = 1; page <= 20; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,$"https://api.cloudflare.com/client/v4/accounts?per_page=50&page={page}");
            request.Headers.Authorization = new("Bearer",token);
            using var response = await SendOAuthAsync(request,ct);
            if (!response.IsSuccessStatusCode) throw new CloudflareSignInException("Cloudflare account discovery failed. Check the Account Settings Read permission.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _ = ProviderConnection.Required(doc.RootElement,"success",JsonValueKind.True);
            var rows = ProviderConnection.Required(doc.RootElement,"result",JsonValueKind.Array);
            foreach (var row in rows.EnumerateArray())
            {
                var id=ProviderConnection.RequiredText(row,"id");var name=ProviderConnection.RequiredText(row,"name");
                if(id.Length != 32 || !id.All(char.IsAsciiHexDigit)) throw ProviderConnection.InvalidResponse();
                accounts.Add(new(id,name));
            }
            if (rows.GetArrayLength() < 50) return accounts;
        }
        throw new CloudflareSignInException("Cloudflare returned too many accounts. Restrict the accounts granted to TypeWhisper.");
    }
}
