using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Lists the supported supporter discord claim state values.
/// </summary>
public enum SupporterDiscordClaimState
{
    /// <summary>
    /// Represents the unavailable option.
    /// </summary>
    Unavailable,
    /// <summary>
    /// Represents the unlinked option.
    /// </summary>
    Unlinked,
    /// <summary>
    /// Represents the pending option.
    /// </summary>
    Pending,
    /// <summary>
    /// Represents the linked option.
    /// </summary>
    Linked,
    /// <summary>
    /// Represents the failed option.
    /// </summary>
    Failed,
}

/// <summary>
/// Lists stable Discord error values used for persistence and retry decisions.
/// </summary>
internal enum SupporterDiscordErrorKind
{
    None,
    EntitlementRequired,
    EntitlementNotFound,
    ClaimStartFailed,
    ClaimEmptyResponse,
    ClaimNoUrl,
    StatusRefreshFailed,
    StatusEmptyResponse,
    HelperUnavailable,
    OperationFailed,
    Raw,
}

/// <summary>
/// Represents a stable Discord error and its non-localized detail.
/// </summary>
/// <param name="Kind">Stable error kind.</param>
/// <param name="Detail">Optional non-localized error detail.</param>
internal readonly record struct SupporterDiscordError(
    SupporterDiscordErrorKind Kind,
    string? Detail = null);

/// <summary>
/// Lightweight Windows port of the supporter Discord claim flow.
/// Uses the same local claim service endpoints as macOS, but relies on manual refresh instead of callback handling.
/// </summary>
public sealed partial class SupporterDiscordService : ObservableObject
{
    private const string DefaultBaseUrl = "https://community.typewhisper.com";
    private const string CallbackScheme = "typewhisper";
    private const string CallbackHost = "community";
    private const string CallbackPath = "/claim-result";

    private readonly HttpClient _http;
    private readonly string _statusPath;
    private SupporterDiscordError _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedRoles))]
    [NotifyPropertyChangedFor(nameof(LinkedRolesText))]
    private SupporterDiscordClaimState _claimState = SupporterDiscordClaimState.Unavailable;

    [ObservableProperty]
    private string? _discordUsername;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedRoles))]
    [NotifyPropertyChangedFor(nameof(LinkedRolesText))]
    private string[] _linkedRoles = Array.Empty<string>();

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private string? _claimActivationId;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _isHelperUnavailable;

    /// <summary>
    /// Initializes a new instance of the SupporterDiscordService class.
    /// </summary>
    public SupporterDiscordService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, TypeWhisperEnvironment.DataPath)
    {
    }

    internal SupporterDiscordService(HttpClient http, string dataPath)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TypeWhisper", GetAppVersion()));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(Windows)"));
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _statusPath = ResolveDataFilePath(dataPath, "supporter-discord.json");
        LoadPersistedStatus();
    }

    /// <summary>
    /// Gets whether has linked roles.
    /// </summary>
    public bool HasLinkedRoles => LinkedRoles.Length > 0;
    /// <summary>
    /// Performs linked roles text.
    /// </summary>
    public string LinkedRolesText => string.Join(", ", LinkedRoles);
    /// <summary>
    /// Gets the localized Discord error message.
    /// </summary>
    public string? ErrorMessage => FormatDiscordError(_error);
    /// <summary>
    /// Gets the git hub sponsors url.
    /// </summary>
    public string GitHubSponsorsUrl => $"{BaseUrl}/claims/github";
    /// <summary>
    /// Gets the callback uri.
    /// </summary>
    public string CallbackUri => $"{CallbackScheme}://{CallbackHost}{CallbackPath}";

    private string BaseUrl =>
        Environment.GetEnvironmentVariable("TYPEWHISPER_DISCORD_CLAIM_BASE_URL")
        ?? DefaultBaseUrl;

    /// <summary>
    /// Returns whether handle callback uri.
    /// </summary>
    public static bool CanHandleCallbackUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        return CanHandleCallbackUri(uri);
    }

    /// <summary>
    /// Returns whether handle callback uri.
    /// </summary>
    public static bool CanHandleCallbackUri(Uri uri) =>
        uri.Scheme.Equals(CallbackScheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, CallbackHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, CallbackPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates claim session asynchronously.
    /// </summary>
    public async Task<Uri?> CreateClaimSessionAsync(LicenseService license, CancellationToken ct = default)
    {
        IsWorking = true;
        SetError(default);

        try
        {
            var candidates = license.GetDiscordClaimProofCandidates();
            if (candidates.Count == 0)
            {
                HandleSupporterEntitlementRemoved();
                ClaimState = SupporterDiscordClaimState.Failed;
                SetError(new(SupporterDiscordErrorKind.EntitlementRequired));
                PersistStatus();
                return null;
            }

            SupporterDiscordError? lastRecoverableError = null;
            foreach (var proof in candidates)
            {
                using var response = await _http.PostAsJsonAsync(
                    $"{BaseUrl}/claims/polar/start",
                    new SupporterDiscordStartRequest(
                        proof.Key,
                        proof.ActivationId,
                        proof.Tier.ToString().ToLowerInvariant(),
                        GetAppVersion()),
                    ct);

                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = ParseDiscordError(
                        json,
                        new(
                            SupporterDiscordErrorKind.ClaimStartFailed,
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));
                    if (ShouldRetryWithNextClaimProof(error))
                    {
                        lastRecoverableError = error;
                        continue;
                    }

                    ApplyServiceFailure(error);
                    PersistStatus();
                    return null;
                }

                var payload = JsonSerializer.Deserialize<SupporterDiscordStartResponse>(json);
                if (payload is null)
                {
                    ApplyServiceFailure(new(SupporterDiscordErrorKind.ClaimEmptyResponse));
                    PersistStatus();
                    return null;
                }

                SessionId = payload.SessionId;
                ClaimActivationId = proof.ActivationId;
                ClaimState = SupporterDiscordClaimState.Pending;
                IsHelperUnavailable = false;
                DiscordUsername = null;
                LinkedRoles = Array.Empty<string>();
                SetError(default);
                PersistStatus();
                if (Uri.TryCreate(payload.ClaimUrl, UriKind.Absolute, out var claimUrl))
                    return claimUrl;

                ClaimState = SupporterDiscordClaimState.Failed;
                SetError(new(SupporterDiscordErrorKind.ClaimNoUrl));
                PersistStatus();
                return null;
            }

            if (lastRecoverableError is { } recoverableError)
            {
                ApplyServiceFailure(recoverableError);
                PersistStatus();
            }

            return null;
        }
        catch (Exception ex)
        {
            ApplyTransportFailure(ex);
            PersistStatus();
            return null;
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>
    /// Performs reconnect asynchronously.
    /// </summary>
    public async Task<Uri?> ReconnectAsync(LicenseService license, CancellationToken ct = default)
    {
        ClaimState = SupporterDiscordClaimState.Unlinked;
        DiscordUsername = null;
        LinkedRoles = Array.Empty<string>();
        SetError(default);
        SessionId = null;
        ClaimActivationId = null;
        PersistStatus();
        return await CreateClaimSessionAsync(license, ct);
    }

    /// <summary>
    /// Refreshes status if needed asynchronously.
    /// </summary>
    public async Task RefreshStatusIfNeededAsync(LicenseService license, CancellationToken ct = default)
    {
        var candidates = license.GetDiscordClaimProofCandidates();
        if (candidates.Count == 0)
        {
            HandleSupporterEntitlementRemoved();
            return;
        }

        if (ClearStaleClaimActivation(candidates))
            return;

        if (ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked || !string.IsNullOrWhiteSpace(SessionId))
            await RefreshClaimStatusAsync(license, ct);
    }

    /// <summary>
    /// Refreshes claim status asynchronously.
    /// </summary>
    public async Task RefreshClaimStatusAsync(LicenseService license, CancellationToken ct = default)
    {
        var candidates = license.GetDiscordClaimProofCandidates();
        if (candidates.Count == 0 || ClearStaleClaimActivation(candidates))
            return;

        var activationId = string.IsNullOrWhiteSpace(ClaimActivationId)
            ? candidates.FirstOrDefault()?.ActivationId
            : ClaimActivationId;
        if (string.IsNullOrWhiteSpace(activationId))
        {
            HandleSupporterEntitlementRemoved();
            return;
        }

        IsWorking = true;

        try
        {
            var url = $"{BaseUrl}/claims/polar/status?activation_id={Uri.EscapeDataString(activationId)}";
            if (!string.IsNullOrWhiteSpace(SessionId))
                url += $"&session_id={Uri.EscapeDataString(SessionId)}";

            using var response = await _http.GetAsync(url, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                ApplyServiceFailure(ParseDiscordError(
                    json,
                    new(
                        SupporterDiscordErrorKind.StatusRefreshFailed,
                        ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture))));
                PersistStatus();
                return;
            }

            var payload = JsonSerializer.Deserialize<SupporterDiscordStatusResponse>(json);
            if (payload is null)
            {
                ApplyServiceFailure(new(SupporterDiscordErrorKind.StatusEmptyResponse));
                PersistStatus();
                return;
            }

            IsHelperUnavailable = false;
            ClaimState = payload.Status switch
            {
                "unlinked" => SupporterDiscordClaimState.Unlinked,
                "pending" => SupporterDiscordClaimState.Pending,
                "linked" => SupporterDiscordClaimState.Linked,
                "failed" => SupporterDiscordClaimState.Failed,
                _ => SupporterDiscordClaimState.Failed,
            };
            DiscordUsername = payload.DiscordUsername;
            LinkedRoles = payload.LinkedRoles ?? Array.Empty<string>();
            SetError(NormalizeOptionalDiscordError(payload.ErrorMessage) ?? default);
            SessionId = string.IsNullOrWhiteSpace(payload.SessionId) ? SessionId : payload.SessionId;
            PersistStatus();
        }
        catch (Exception ex)
        {
            ApplyTransportFailure(ex);
            PersistStatus();
            Debug.WriteLine($"Supporter Discord refresh failed: {ex.Message}");
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>
    /// Performs handle supporter entitlement removed.
    /// </summary>
    public void HandleSupporterEntitlementRemoved()
    {
        ClaimState = SupporterDiscordClaimState.Unavailable;
        IsHelperUnavailable = false;
        DiscordUsername = null;
        LinkedRoles = Array.Empty<string>();
        SetError(default);
        SessionId = null;
        ClaimActivationId = null;
        PersistStatus();
    }

    private bool ClearStaleClaimActivation(IReadOnlyList<SupporterClaimProof> candidates)
    {
        if (string.IsNullOrWhiteSpace(ClaimActivationId))
            return false;

        if (candidates.Any(candidate =>
                string.Equals(candidate.ActivationId, ClaimActivationId, StringComparison.Ordinal)))
        {
            return false;
        }

        HandleSupporterEntitlementRemoved();
        return true;
    }

    /// <summary>
    /// Performs handle callback uri asynchronously.
    /// </summary>
    public async Task<bool> HandleCallbackUriAsync(Uri uri, LicenseService license, CancellationToken ct = default)
    {
        if (!CanHandleCallbackUri(uri))
            return false;

        var payload = ParseCallbackUri(uri);
        if (payload is null)
            return false;

        if (!string.IsNullOrWhiteSpace(payload.Flow) && !string.Equals(payload.Flow, "polar", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(payload.SessionId))
            SessionId = payload.SessionId;

        IsHelperUnavailable = false;
        SetError(NormalizeOptionalDiscordError(payload.ErrorMessage) ?? default);
        ClaimState = payload.Status?.ToLowerInvariant() switch
        {
            "linked" or "pending" => SupporterDiscordClaimState.Pending,
            "unlinked" => SupporterDiscordClaimState.Unlinked,
            "failed" or "expired" => SupporterDiscordClaimState.Failed,
            _ => ClaimState
        };

        PersistStatus();
        await RefreshClaimStatusAsync(license, ct);
        return true;
    }

    private void PersistStatus()
    {
        try
        {
            var payload = new SupporterDiscordPersistedState
            {
                ClaimState = ClaimState.ToString(),
                DiscordUsername = DiscordUsername,
                LinkedRoles = LinkedRoles,
                ErrorKind = _error.Kind == SupporterDiscordErrorKind.None
                    ? null
                    : _error.Kind.ToString(),
                ErrorDetail = _error.Detail,
                SessionId = SessionId,
                ClaimActivationId = ClaimActivationId,
                IsHelperUnavailable = IsHelperUnavailable,
            };

            File.WriteAllText(_statusPath, JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Persisting supporter Discord state failed: {ex.Message}");
        }
    }

    private void LoadPersistedStatus()
    {
        try
        {
            if (!File.Exists(_statusPath))
                return;

            var json = File.ReadAllText(_statusPath, System.Text.Encoding.UTF8);
            var payload = JsonSerializer.Deserialize<SupporterDiscordPersistedState>(json);
            if (payload is null)
                return;

            ClaimState = Enum.TryParse<SupporterDiscordClaimState>(payload.ClaimState, out var state)
                ? state
                : SupporterDiscordClaimState.Unavailable;
            DiscordUsername = payload.DiscordUsername;
            LinkedRoles = payload.LinkedRoles ?? Array.Empty<string>();
            if (Enum.TryParse<SupporterDiscordErrorKind>(payload.ErrorKind, ignoreCase: true, out var errorKind))
            {
                SetError(new(errorKind, payload.ErrorDetail));
            }
            else if (payload.IsHelperUnavailable)
            {
                SetError(new(SupporterDiscordErrorKind.HelperUnavailable));
            }
            else
            {
                SetError(default);
            }
            SessionId = payload.SessionId;
            ClaimActivationId = payload.ClaimActivationId;
            IsHelperUnavailable = payload.IsHelperUnavailable;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading supporter Discord state failed: {ex.Message}");
        }
    }

    private void ApplyTransportFailure(Exception ex)
    {
        if (IsHelperUnavailableError(ex))
        {
            ClaimState = SupporterDiscordClaimState.Unavailable;
            IsHelperUnavailable = true;
            SetError(new(SupporterDiscordErrorKind.HelperUnavailable));
            return;
        }

        IsHelperUnavailable = false;
        if (ClaimState != SupporterDiscordClaimState.Linked)
            ClaimState = SupporterDiscordClaimState.Failed;

        SetError(new(SupporterDiscordErrorKind.OperationFailed, ex.Message));
    }

    private static bool IsHelperUnavailableError(Exception ex)
    {
        if (ex is HttpRequestException)
            return true;

        var text = ex.ToString();
        return text.Contains("127.0.0.1:8787", StringComparison.OrdinalIgnoreCase)
            || text.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static SupporterDiscordError ParseDiscordError(
        string? json,
        SupporterDiscordError fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            var payload = JsonSerializer.Deserialize<SupporterDiscordErrorResponse>(json);
            if (!string.IsNullOrWhiteSpace(payload?.Error))
                return NormalizeDiscordError(payload.Error);
        }
        catch
        {
            // Ignore malformed error payloads.
        }

        return fallback;
    }

    internal static SupporterDiscordError NormalizeDiscordError(string message)
    {
        var normalized = message.Trim();
        if (normalized.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("could not be found", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("supporter entitlement", StringComparison.OrdinalIgnoreCase))
        {
            return new(SupporterDiscordErrorKind.EntitlementNotFound);
        }

        return new(SupporterDiscordErrorKind.Raw, message);
    }

    private static SupporterDiscordError? NormalizeOptionalDiscordError(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : NormalizeDiscordError(message);

    internal static bool ShouldRetryWithNextClaimProof(SupporterDiscordError error) =>
        error.Kind == SupporterDiscordErrorKind.EntitlementNotFound;

    private void ApplyServiceFailure(SupporterDiscordError error)
    {
        IsHelperUnavailable = false;
        if (ClaimState != SupporterDiscordClaimState.Linked)
            ClaimState = SupporterDiscordClaimState.Failed;

        SetError(error);
    }

    private void SetError(SupporterDiscordError error)
    {
        if (_error == error)
            return;

        _error = error;
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private static string? FormatDiscordError(SupporterDiscordError error) => error.Kind switch
    {
        SupporterDiscordErrorKind.None => null,
        SupporterDiscordErrorKind.EntitlementRequired => Loc.Instance["License.DiscordEntitlementRequired"],
        SupporterDiscordErrorKind.EntitlementNotFound => Loc.Instance["License.DiscordEntitlementNotFound"],
        SupporterDiscordErrorKind.ClaimStartFailed =>
            Loc.Instance.GetString("License.DiscordClaimStartFailedFormat", error.Detail ?? string.Empty),
        SupporterDiscordErrorKind.ClaimEmptyResponse => Loc.Instance["License.DiscordClaimEmptyResponse"],
        SupporterDiscordErrorKind.ClaimNoUrl => Loc.Instance["License.DiscordClaimNoUrl"],
        SupporterDiscordErrorKind.StatusRefreshFailed =>
            Loc.Instance.GetString("License.DiscordStatusRefreshFailedFormat", error.Detail ?? string.Empty),
        SupporterDiscordErrorKind.StatusEmptyResponse => Loc.Instance["License.DiscordStatusEmptyResponse"],
        SupporterDiscordErrorKind.HelperUnavailable => Loc.Instance["License.DiscordHelperUnavailable"],
        SupporterDiscordErrorKind.OperationFailed =>
            Loc.Instance.GetString("License.DiscordOperationFailedFormat", error.Detail ?? string.Empty),
        SupporterDiscordErrorKind.Raw => error.Detail,
        _ => error.Detail,
    };

    private static string ResolveDataFilePath(string dataPath, string fileName)
    {
        var root = Path.GetFullPath(dataPath);
        var path = Path.GetFullPath(fileName, root);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." ||
            (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)))
        {
            return path;
        }

        throw new InvalidOperationException("Supporter Discord data path must stay inside the configured data directory.");
    }

    private static SupporterDiscordCallbackPayload? ParseCallbackUri(Uri uri)
    {
        if (!CanHandleCallbackUri(uri))
            return null;

        var values = ParseQueryString(uri.Query);
        values.TryGetValue("flow", out var flow);
        values.TryGetValue("status", out var status);
        values.TryGetValue("session_id", out var sessionId);
        values.TryGetValue("error", out var error);
        return new SupporterDiscordCallbackPayload(flow, status, sessionId, error);
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var span = query.AsSpan();
        if (!span.IsEmpty && span[0] == '?')
            span = span[1..];

        foreach (var rawPart in span.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawPart.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private sealed record SupporterDiscordPersistedState
    {
        [JsonPropertyName("claimState")] public string? ClaimState { get; init; }
        [JsonPropertyName("discordUsername")] public string? DiscordUsername { get; init; }
        [JsonPropertyName("linkedRoles")] public string[]? LinkedRoles { get; init; }
        [JsonPropertyName("errorKind")] public string? ErrorKind { get; init; }
        [JsonPropertyName("errorDetail")] public string? ErrorDetail { get; init; }
        [JsonPropertyName("errorMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyErrorMessage { get; init; }
        [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
        [JsonPropertyName("claimActivationId")] public string? ClaimActivationId { get; init; }
        [JsonPropertyName("isHelperUnavailable")] public bool IsHelperUnavailable { get; init; }
    }

    private sealed record SupporterDiscordStartRequest(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("activationId")] string ActivationId,
        [property: JsonPropertyName("tier")] string Tier,
        [property: JsonPropertyName("appVersion")] string AppVersion);

    private sealed record SupporterDiscordStartResponse
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [JsonPropertyName("claim_url")] public string? ClaimUrl { get; init; }
    }

    private sealed record SupporterDiscordStatusResponse
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("discord_username")] public string? DiscordUsername { get; init; }
        [JsonPropertyName("linked_roles")] public string[]? LinkedRoles { get; init; }
        [JsonPropertyName("error")] public string? ErrorMessage { get; init; }
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    }

    private sealed record SupporterDiscordErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
    }

    private sealed record SupporterDiscordCallbackPayload(
        string? Flow,
        string? Status,
        string? SessionId,
        string? ErrorMessage);
}
