using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Server-signed account access claims, identical to the Mac envelope.</summary>
/// <param name="Status">Access status.</param>
/// <param name="Tier">License tier.</param>
/// <param name="Source">Entitlement issuer.</param>
/// <param name="IsLifetime">Whether access is lifetime.</param>
/// <param name="ExpiresAt">Optional access expiry.</param>
/// <param name="DeviceLimit">Optional device limit.</param>
/// <param name="VerifiedAt">Server verification time.</param>
/// <param name="Signature">Signed payload and P256 signature in base64url.</param>
public sealed record PremiumAccountEntitlement(string Status, string Tier, string Source, bool IsLifetime,
    DateTimeOffset? ExpiresAt, int? DeviceLimit, DateTimeOffset VerifiedAt, string? Signature)
{
    /// <summary>Whether verified claims currently permit access.</summary>
    public bool IsActive => (Status is "active" or "granted") && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}

// The same browser/PKCE exchange and signed entitlement envelope used by macOS.
/// <summary>Apple web authentication and verified account sessions. Calls must be serialized by the host.</summary>
public sealed class PremiumAccountClient
{
    /// <summary>Pinned P256 public key shared with the Mac client.</summary>
    public const string ProductionKey = "8ZwFh+yrpkZZ1VsZgjpZcOz2h3jKpGG93MTdRaCPqXFn/Loqh8u36hB9FLho+ozwuHbaNeoN1MxM2/AJKyBNvQ==";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _deviceId;
    private readonly Action<string?> _save;
    private readonly byte[] _key;
    private string? _token;
    private Pending? _pending;
    /// <summary>The verified account entitlement.</summary>
    public PremiumAccountEntitlement? Entitlement { get; private set; }
    /// <summary>Whether an account token is loaded.</summary>
    public bool SignedIn => !string.IsNullOrEmpty(_token);
    /// <summary>Whether a browser callback is pending.</summary>
    public bool WaitingForBrowser => _pending is not null;
    private sealed record Pending(string State, string Verifier, DateTimeOffset ExpiresAt);
    private sealed record StartResponse(Uri AuthorizationURL, string State, DateTimeOffset ExpiresAt);
    private sealed record Session(string AccessToken, PremiumAccountEntitlement? Entitlement);
    private sealed record EntitlementResponse(PremiumAccountEntitlement? Entitlement);

    /// <summary>Creates a client with secure persistence supplied by the host.</summary>
    public PremiumAccountClient(HttpClient http, string deviceId, Action<string?> save, string? saved = null, string publicKey = ProductionKey)
    {
        _http = http; _deviceId = deviceId; _save = save; _key = Convert.FromBase64String(publicKey);
        if (saved is null) return;
        var session = JsonSerializer.Deserialize<Session>(saved, Json) ?? throw new JsonException("Invalid account session.");
        ValidateToken(session.AccessToken);
        Entitlement = Verify(session.Entitlement); _token = session.AccessToken;
    }

    /// <summary>Starts a fresh PKCE authorization request.</summary>
    public async Task<Uri> BeginAsync(CancellationToken ct)
    {
        Cancel();
        var verifier = Encode(RandomNumberGenerator.GetBytes(32));
        var nonce = Encode(RandomNumberGenerator.GetBytes(32));
        var start = await Request<StartResponse>("/v1/auth/apple/web/start", HttpMethod.Post,
            new { nonceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))),
                codeChallenge = Encode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))) }, false, ct);
        if (start.AuthorizationURL is null || start.AuthorizationURL.Scheme != "https" || start.AuthorizationURL.Host != "appleid.apple.com" ||
            !start.AuthorizationURL.IsDefaultPort || start.AuthorizationURL.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(start.State) || start.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The Apple sign-in address could not be verified.");
        _pending = new(start.State, verifier, start.ExpiresAt);
        return start.AuthorizationURL;
    }

    /// <summary>Validates the callback origin and pending state.</summary>
    public bool Accepts(Uri callback) => _pending is { } pending && IsCallback(callback) &&
        Query(callback).TryGetValue("state", out var state) && state == pending.State;
    /// <summary>Validates the supported callback route without exposing its query.</summary>
    public static bool IsCallback(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "typewhisper" && uri.Host == "premium-auth" &&
        uri.AbsolutePath == "/callback" && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0 && uri.OriginalString.Length <= 8192;
    private static Dictionary<string, string> Query(Uri uri)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (!values.TryAdd(Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : ""))
                throw new InvalidDataException("Duplicate Apple callback parameter.");
        }
        return values;
    }

    /// <summary>Consumes one matching callback, verifies access, and saves the completed session.</summary>
    public async Task CompleteAsync(Uri callback, string? licenseKey, string? activationId, CancellationToken ct)
    {
        if (!Accepts(callback)) throw new InvalidDataException("This Apple sign-in response does not match the pending request.");
        var pending = _pending!; _pending = null;
        if (pending.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Apple sign-in expired. Please try again.");
        var values = Query(callback);
        if (values.ContainsKey("error")) throw new OperationCanceledException("Apple sign-in was canceled.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("Apple sign-in returned no authorization code.");
        var session = await Request<Session>("/v1/auth/apple/web/exchange", HttpMethod.Post,
            new { state = pending.State, code, codeVerifier = pending.Verifier }, false, ct);
        ValidateToken(session.AccessToken);
        var verified = Verify(session.Entitlement);
        _token = session.AccessToken; Entitlement = verified;
        try
        {
            if (!string.IsNullOrWhiteSpace(licenseKey) && !string.IsNullOrWhiteSpace(activationId)) await LinkAsync(licenseKey, activationId, ct);
            else await RefreshAsync(ct);
        }
        catch { _token = null; Entitlement = null; _save(null); throw; }
    }

    /// <summary>Attaches the existing commercial activation to the signed-in account.</summary>
    public async Task LinkAsync(string licenseKey, string activationId, CancellationToken ct)
    {
        var response = await Request<EntitlementResponse>("/v1/entitlements/polar/device/attach", HttpMethod.Post, new { licenseKey, activationId }, true, ct);
        AcceptEntitlement(response.Entitlement);
    }
    /// <summary>Refreshes and verifies account access.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!SignedIn) return;
        var response = await Request<EntitlementResponse>("/v1/entitlements/current", HttpMethod.Get, null, true, ct);
        AcceptEntitlement(response.Entitlement);
    }
    private void AcceptEntitlement(PremiumAccountEntitlement? value)
    {
        try { Entitlement = Verify(value); Persist(); }
        catch { Entitlement = null; Persist(); throw; }
    }
    /// <summary>Detaches this account device before clearing the local session.</summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        if (SignedIn)
        {
            var result = await Request<JsonElement>("/v1/entitlements/polar/device/current", HttpMethod.Delete, null, true, ct);
            if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) throw new InvalidDataException("The device could not be signed out.");
        }
        Clear();
    }
    /// <summary>Discards the pending authorization verifier.</summary>
    public void Cancel() => _pending = null;
    private void Clear() { _token = null; Entitlement = null; Cancel(); _save(null); }
    private void Persist() => _save(JsonSerializer.Serialize(new Session(_token!, Entitlement), Json));
    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384 || token.Any(char.IsControl)) throw new InvalidDataException("Invalid account session.");
    }

    /// <summary>Verifies the P256 signature and exact signed claims.</summary>
    public PremiumAccountEntitlement? Verify(PremiumAccountEntitlement? entitlement)
    {
        if (entitlement is null) return null;
        try
        {
            var pieces = entitlement.Signature?.Split('.');
            if (pieces?.Length != 2 || _key.Length != 64) throw new CryptographicException();
            var payload = Decode(pieces[0]); var signature = Decode(pieces[1]);
            using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = _key[..32], Y = _key[32..] } });
            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException();
            var claims = JsonSerializer.Deserialize<PremiumAccountEntitlement>(payload, Json);
            if (claims is null || (claims with { Signature = entitlement.Signature }) != entitlement) throw new CryptographicException();
            return entitlement;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        { throw new InvalidDataException("The Premium entitlement signature could not be verified."); }
    }

    private async Task<T> Request<T>(string path, HttpMethod method, object? body, bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri("https://app.typewhisper.com" + path));
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Add("X-TypeWhisper-Device-ID", _deviceId);
        request.Headers.Add("X-TypeWhisper-Platform", "windows");
        request.Headers.Add("X-TypeWhisper-Entitlement-Version", "2");
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token ?? throw new InvalidOperationException("Sign in with Apple first."));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        if (authenticated && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) Clear();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Account request failed (HTTP {(int)response.StatusCode}).");
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new JsonException("Invalid account response.");
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));
}
