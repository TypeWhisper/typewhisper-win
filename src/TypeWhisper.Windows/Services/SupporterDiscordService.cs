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

namespace TypeWhisper.Windows.Services;

public enum SupporterDiscordClaimState
{
    Unavailable,
    Unlinked,
    Pending,
    Linked,
    Failed,
}

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

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _statusPath;

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
    private string? _errorMessage;

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private string? _claimActivationId;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _isHelperUnavailable;

    public SupporterDiscordService()
    {
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TypeWhisper", GetAppVersion()));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(Windows)"));
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _statusPath = Path.Combine(TypeWhisperEnvironment.DataPath, "supporter-discord.json");
        LoadPersistedStatus();
    }

    public bool HasLinkedRoles => LinkedRoles.Length > 0;
    public string LinkedRolesText => string.Join(", ", LinkedRoles);
    public string GitHubSponsorsUrl => $"{BaseUrl}/claims/github";
    public string CallbackUri => $"{CallbackScheme}://{CallbackHost}{CallbackPath}";

    private string BaseUrl =>
        Environment.GetEnvironmentVariable("TYPEWHISPER_DISCORD_CLAIM_BASE_URL")
        ?? DefaultBaseUrl;

    public static bool CanHandleCallbackUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        return CanHandleCallbackUri(uri);
    }

    public static bool CanHandleCallbackUri(Uri uri) =>
        uri.Scheme.Equals(CallbackScheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, CallbackHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, CallbackPath, StringComparison.OrdinalIgnoreCase);

    public async Task<Uri?> CreateClaimSessionAsync(LicenseService license, CancellationToken ct = default)
    {
        IsWorking = true;
        ErrorMessage = null;

        try
        {
            var candidates = license.GetDiscordClaimProofCandidates();
            if (candidates.Count == 0)
            {
                HandleSupporterEntitlementRemoved();
                ClaimState = SupporterDiscordClaimState.Failed;
                ErrorMessage = "An active supporter or commercial license is required before you can claim Discord status.";
                PersistStatus();
                return null;
            }

            string? lastRecoverableMessage = null;
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
                    var message = ParseDiscordError(json, $"Discord claim start failed (HTTP {(int)response.StatusCode})");
                    if (ShouldRetryWithNextClaimProof(message))
                    {
                        lastRecoverableMessage = message;
                        continue;
                    }

                    throw new InvalidOperationException(message);
                }

                var payload = JsonSerializer.Deserialize<SupporterDiscordStartResponse>(json)
                    ?? throw new InvalidOperationException("Discord claim start failed: empty response.");

                SessionId = payload.SessionId;
                ClaimActivationId = proof.ActivationId;
                ClaimState = SupporterDiscordClaimState.Pending;
                IsHelperUnavailable = false;
                DiscordUsername = null;
                LinkedRoles = Array.Empty<string>();
                ErrorMessage = null;
                PersistStatus();
                if (Uri.TryCreate(payload.ClaimUrl, UriKind.Absolute, out var claimUrl))
                    return claimUrl;

                ClaimState = SupporterDiscordClaimState.Failed;
                ErrorMessage = "Discord claim start failed: no claim URL was returned.";
                PersistStatus();
                return null;
            }

            if (!string.IsNullOrWhiteSpace(lastRecoverableMessage))
            {
                ClaimState = SupporterDiscordClaimState.Failed;
                ErrorMessage = lastRecoverableMessage;
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

    public async Task<Uri?> ReconnectAsync(LicenseService license, CancellationToken ct = default)
    {
        ClaimState = SupporterDiscordClaimState.Unlinked;
        DiscordUsername = null;
        LinkedRoles = Array.Empty<string>();
        ErrorMessage = null;
        SessionId = null;
        ClaimActivationId = null;
        PersistStatus();
        return await CreateClaimSessionAsync(license, ct);
    }

    public async Task RefreshStatusIfNeededAsync(LicenseService license, CancellationToken ct = default)
    {
        if (license.SupporterClaimProof is null)
        {
            HandleSupporterEntitlementRemoved();
            return;
        }

        if (ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked || !string.IsNullOrWhiteSpace(SessionId))
            await RefreshClaimStatusAsync(license, ct);
    }

    public async Task RefreshClaimStatusAsync(LicenseService license, CancellationToken ct = default)
    {
        var activationId = ClaimActivationId ?? license.GetDiscordClaimProofCandidates().FirstOrDefault()?.ActivationId;
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
                throw new InvalidOperationException(ParseDiscordError(json, $"Discord status refresh failed (HTTP {(int)response.StatusCode})"));

            var payload = JsonSerializer.Deserialize<SupporterDiscordStatusResponse>(json)
                ?? throw new InvalidOperationException("Discord status refresh failed: empty response.");

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
            ErrorMessage = payload.ErrorMessage;
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

    public void HandleSupporterEntitlementRemoved()
    {
        ClaimState = SupporterDiscordClaimState.Unavailable;
        IsHelperUnavailable = false;
        DiscordUsername = null;
        LinkedRoles = Array.Empty<string>();
        ErrorMessage = null;
        SessionId = null;
        ClaimActivationId = null;
        PersistStatus();
    }

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
        ErrorMessage = payload.ErrorMessage;
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
                ErrorMessage = ErrorMessage,
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
            ErrorMessage = payload.ErrorMessage;
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
            ErrorMessage = "Local helper unavailable";
            return;
        }

        IsHelperUnavailable = false;
        if (ClaimState == SupporterDiscordClaimState.Linked)
        {
            ErrorMessage = ex.Message;
        }
        else
        {
            ClaimState = SupporterDiscordClaimState.Failed;
            ErrorMessage = ex.Message;
        }
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

    private static string ParseDiscordError(string? json, string fallback)
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

    private static string NormalizeDiscordError(string message)
    {
        if (string.Equals(message.Trim(), "Not found", StringComparison.OrdinalIgnoreCase))
        {
            return "The supporter entitlement could not be found. Try refreshing the supporter license and reconnecting Discord.";
        }

        return message;
    }

    private static bool ShouldRetryWithNextClaimProof(string message) =>
        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("supporter entitlement", StringComparison.OrdinalIgnoreCase);

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
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
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
