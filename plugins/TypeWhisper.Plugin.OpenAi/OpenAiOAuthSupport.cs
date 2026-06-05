using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.OpenAi;

internal enum OpenAiAuthMode
{
    ApiKey,
    ChatGpt
}

internal static class OpenAiAuthModeExtensions
{
    /// <summary>
    /// Converts to storage value.
    /// </summary>
    public static string ToStorageValue(this OpenAiAuthMode mode) =>
        mode == OpenAiAuthMode.ChatGpt ? "chatgpt" : "api-key";

    /// <summary>
    /// Parses the supplied value into the expected representation.
    /// </summary>
    public static OpenAiAuthMode Parse(string? value) =>
        string.Equals(value, "chatgpt", StringComparison.OrdinalIgnoreCase)
            ? OpenAiAuthMode.ChatGpt
            : OpenAiAuthMode.ApiKey;
}

internal sealed record OpenAiPkceCodes(string Verifier, string Challenge);

internal sealed record OpenAiOAuthTokenResponse(
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn);

internal sealed record OpenAiOAuthMetadata(string? AccountId, string? PlanType, DateTimeOffset? ExpiresAt);

internal static class OpenAiOAuthClient
{
    /// <summary>
    /// Defines the client id constant.
    /// </summary>
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    /// <summary>
    /// Defines the issuer constant.
    /// </summary>
    public const string Issuer = "https://auth.openai.com";
    /// <summary>
    /// Defines the redirect uri constant.
    /// </summary>
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    /// <summary>
    /// Defines the callback port constant.
    /// </summary>
    public const int CallbackPort = 1455;

    private const string AuthorizeOriginator = "opencode";

    /// <summary>
    /// Generates pkce codes.
    /// </summary>
    public static OpenAiPkceCodes GeneratePkceCodes()
    {
        var verifier = RandomOAuthString(64);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new OpenAiPkceCodes(verifier, challenge);
    }

    /// <summary>
    /// Generates random state.
    /// </summary>
    public static string RandomState() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Builds authorize uri.
    /// </summary>
    public static Uri BuildAuthorizeUri(string state, OpenAiPkceCodes pkce)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = AuthorizeOriginator,
        };

        return new Uri($"{Issuer}/oauth/authorize?{BuildQuery(query)}");
    }

    /// <summary>
    /// Performs exchange authorization code asynchronously.
    /// </summary>
    public static async Task<OpenAiOAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        HttpClient httpClient,
        string code,
        OpenAiPkceCodes pkce,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = ClientId,
                ["code_verifier"] = pkce.Verifier,
            })
        };

        return await SendTokenRequestAsync(httpClient, request, ct);
    }

    /// <summary>
    /// Refreshes token asynchronously.
    /// </summary>
    public static async Task<OpenAiOAuthTokenResponse> RefreshTokenAsync(
        HttpClient httpClient,
        string refreshToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
            })
        };

        return await SendTokenRequestAsync(httpClient, request, ct);
    }

    /// <summary>
    /// Performs extract metadata.
    /// </summary>
    public static OpenAiOAuthMetadata ExtractMetadata(OpenAiOAuthTokenResponse tokens, string? preferredAccountId = null)
    {
        var idClaims = ParseJwtPayload(tokens.IdToken);
        var accessClaims = ParseJwtPayload(tokens.AccessToken);
        var claims = idClaims ?? accessClaims;

        var accountId = preferredAccountId
            ?? GetString(claims, "chatgpt_account_id")
            ?? GetNestedString(claims, "https://api.openai.com/auth", "chatgpt_account_id")
            ?? GetFirstOrganizationId(claims);
        var planType = GetString(claims, "chatgpt_plan_type")
            ?? GetNestedString(claims, "https://api.openai.com/auth", "chatgpt_plan_type");
        var expiresAt = GetDouble(accessClaims, "exp") is { } exp
            ? DateTimeOffset.FromUnixTimeSeconds((long)exp)
            : DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600);

        return new OpenAiOAuthMetadata(accountId, planType, expiresAt);
    }

    private static async Task<OpenAiOAuthTokenResponse> SendTokenRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI token request failed with status {(int)response.StatusCode}: {json}");

        return JsonSerializer.Deserialize<OpenAiOAuthTokenResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("OpenAI token response could not be parsed.");
    }

    private static string RandomOAuthString(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static Dictionary<string, JsonElement>? ParseJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var bytes = Base64UrlDecode(parts[1]);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(Dictionary<string, JsonElement>? claims, string key) =>
        claims is not null
        && claims.TryGetValue(key, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? GetNestedString(Dictionary<string, JsonElement>? claims, string parent, string key)
    {
        if (claims is null
            || !claims.TryGetValue(parent, out var parentElement)
            || parentElement.ValueKind != JsonValueKind.Object
            || !parentElement.TryGetProperty(key, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static double? GetDouble(Dictionary<string, JsonElement>? claims, string key) =>
        claims is not null
        && claims.TryGetValue(key, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var value)
            ? value
            : null;

    private static string? GetFirstOrganizationId(Dictionary<string, JsonElement>? claims)
    {
        if (claims is null
            || !claims.TryGetValue("organizations", out var organizations)
            || organizations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var organization in organizations.EnumerateArray())
        {
            if (organization.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }

        return null;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

internal sealed class OpenAiLoopbackOAuthServer : IAsyncDisposable
{
    private readonly string _expectedState;
    private TcpListener? _listener;

    /// <summary>
    /// Initializes a loopback server that waits for the OpenAI OAuth callback.
    /// </summary>
    public OpenAiLoopbackOAuthServer(string expectedState)
    {
        _expectedState = expectedState;
    }

    /// <summary>
    /// Starts the service or session.
    /// </summary>
    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, OpenAiOAuthClient.CallbackPort);
        _listener.Server.DualMode = true;
        _listener.Start();
    }

    /// <summary>
    /// Performs wait for code asynchronously.
    /// </summary>
    public async Task<string> WaitForCodeAsync(CancellationToken ct)
    {
        var listener = _listener ?? throw new InvalidOperationException("OAuth callback server was not started.");
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct)
            ?? throw new InvalidOperationException("OAuth callback request was empty.");

        string html;
        try
        {
            var code = ParseAuthorizationCode(requestLine, _expectedState);
            html = SuccessHtml;
            await SendHtmlAsync(stream, html, ct);
            return code;
        }
        catch (Exception ex)
        {
            html = ErrorHtml(WebUtility.HtmlEncode(ex.Message));
            await SendHtmlAsync(stream, html, ct);
            throw;
        }
        finally
        {
            _listener?.Stop();
            _listener = null;
        }
    }

    internal static string ParseAuthorizationCode(string requestLine, string expectedState)
    {
        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException("The OAuth callback was invalid.");

        var target = parts[1];
        var uri = new Uri("http://localhost" + target);
        if (!string.Equals(uri.AbsolutePath, "/auth/callback", StringComparison.Ordinal))
            throw new InvalidOperationException("The OAuth callback path was invalid.");

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : "");

        if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("The OAuth callback returned an error.");
        if (!query.TryGetValue("state", out var state) || state != expectedState)
            throw new InvalidOperationException("The OAuth callback state did not match.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("The OAuth callback did not include an authorization code.");

        return code;
    }

    private static async Task SendHtmlAsync(Stream stream, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }

    private const string SuccessHtml = """
        <!doctype html>
        <html>
          <head><meta charset="utf-8"><title>TypeWhisper Login</title></head>
          <body style="font-family:Segoe UI,sans-serif;background:#111827;color:#f9fafb;display:grid;min-height:100vh;place-items:center;margin:0">
            <main style="max-width:460px;padding:28px;border-radius:12px;background:#1f2937">
              <h1 style="margin-top:0">Login complete</h1>
              <p>You can close this window and return to TypeWhisper.</p>
            </main>
            <script>setTimeout(() => window.close(), 1800)</script>
          </body>
        </html>
        """;

    private static string ErrorHtml(string message) =>
        $$"""
        <!doctype html>
        <html>
          <head><meta charset="utf-8"><title>TypeWhisper Login</title></head>
          <body style="font-family:Segoe UI,sans-serif;background:#111827;color:#f9fafb;display:grid;min-height:100vh;place-items:center;margin:0">
            <main style="max-width:520px;padding:28px;border-radius:12px;background:#1f2937">
              <h1 style="margin-top:0;color:#fca5a5">Login failed</h1>
              <p>{{message}}</p>
            </main>
          </body>
        </html>
        """;

    /// <summary>
    /// Releases asynchronous resources owned by this session.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        try { _listener?.Stop(); }
        catch { }
        _listener = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed record OpenAiExistingLoginStore(OpenAiExistingLoginTokens Tokens);

internal sealed record OpenAiExistingLoginTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("account_id")] string? AccountId);
