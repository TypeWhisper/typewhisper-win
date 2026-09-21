using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CloudflareAsr;

// Plugin-local connection and persistence support. No runtime dependency on another provider.
internal sealed class ProviderConnection(HttpClient http) : IDisposable
{
    internal sealed record Configuration(string? SecretName, Dictionary<string, string> Values);
    private Configuration _configuration = new(null, []);
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal IPluginHostServices? Host { get; private set; }
    internal string? Key { get; private set; }
    private CloudflareTokens? _oauth;
    internal bool UsesOAuth => _configuration.Values.GetValueOrDefault("authMode") == "oauth";
    internal IReadOnlyList<CloudflareAccount> Accounts => UsesOAuth
        ? JsonSerializer.Deserialize<CloudflareAccount[]>(Get("oauthAccounts", "[]")) ?? [] : [];
    internal HttpClient Http => http;
    internal bool Configured => Host is not null && Key is not null;
    internal string Get(string id, string fallback = "") => _configuration.Values.GetValueOrDefault(id, fallback);
    internal string L(string en, string de)
    {
        try { return Host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en; }
        catch (NotSupportedException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en; }
    }

    internal async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration") ?? new(null, []);
        var secret = saved.SecretName is null ? null : await host.LoadSecretAsync(saved.SecretName);
        _configuration = saved with { Values = saved.Values ?? [] };
        _oauth = UsesOAuth && secret is not null ? JsonSerializer.Deserialize<CloudflareTokens>(secret) : null;
        Key = UsesOAuth ? _oauth?.AccessToken : NormalizeKey(secret); Host = host;
    }
    internal void Deactivate() { Host = null; Key = null; _oauth = null; _configuration = new(null, []); }
    internal async Task SetKeyAsync(string value)
    {
        var key = NormalizeKey(value);
        await _gate.WaitAsync();
        try
        {
            var host = Host ?? throw new InvalidOperationException("Activate the plugin first.");
            var previous = _configuration;
            var staged = key is null ? null : "api-key-" + Guid.NewGuid().ToString("N");
            try
            {
                if (staged is not null) await host.StoreSecretAsync(staged, key!);
                var values = new Dictionary<string,string>(previous.Values);
                values.Remove("authMode"); values.Remove("oauthAccounts");
                var next = previous with { SecretName = staged, Values = values };
                host.SetSetting("configuration", next);
                _configuration = next; Key = key; _oauth = null;
            }
            catch
            {
                if (staged is not null) await CleanupAsync(host, staged);
                throw;
            }
            if (previous.SecretName is not null) await CleanupAsync(host, previous.SecretName);
            host.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }
    internal async Task SaveOAuthAsync(CloudflareTokens tokens, IReadOnlyList<CloudflareAccount> accounts, CancellationToken ct)
    {
        if (accounts.Count == 0) throw new InvalidOperationException("No Cloudflare accounts were granted. Check Account Settings Read and connect again.");
        await _gate.WaitAsync(ct);
        try
        {
            var selected = Get("accountId");
            var account = accounts.Any(a => a.Id == selected) ? selected : accounts.Count == 1 ? accounts[0].Id : "";
            var values = new Dictionary<string,string>(_configuration.Values) {
                ["authMode"] = "oauth", ["accountId"] = account, ["oauthAccounts"] = JsonSerializer.Serialize(accounts)
            };
            await CommitOAuthAsync(tokens, values, ct);
        }
        finally { _gate.Release(); }
    }
    private async Task CommitOAuthAsync(CloudflareTokens tokens, Dictionary<string,string> values, CancellationToken ct)
    {
        var host = Host ?? throw new InvalidOperationException("Activate the plugin first.");
        var previous = _configuration; var staged = "oauth-" + Guid.NewGuid().ToString("N");
        try
        {
            await host.StoreSecretAsync(staged, JsonSerializer.Serialize(tokens)); ct.ThrowIfCancellationRequested();
            var next = new Configuration(staged, values);
            host.SetSetting("configuration", next);
            _configuration = next; _oauth = tokens; Key = tokens.AccessToken;
        }
        catch { await CleanupAsync(host, staged); throw; }
        if (previous.SecretName is not null) await CleanupAsync(host, previous.SecretName);
        host.NotifyCapabilitiesChanged();
    }
    internal async Task EnsureAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_oauth is null || _oauth.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
            CloudflareTokens renewed;
            try { renewed = await new CloudflareOAuth(http).RefreshAsync(_oauth, ct); }
            catch (CloudflareSignInException ex)
            { throw new PluginRequestException(ex.Message, PluginRequestFailureKind.Authentication, innerException: ex); }
            await CommitOAuthAsync(renewed, new(_configuration.Values), ct);
        }
        finally { _gate.Release(); }
    }
    private static async Task CleanupAsync(IPluginHostServices host, string key)
    {
        try { await host.DeleteSecretAsync(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { host.Log(PluginLogLevel.Warning, "An inactive encrypted provider key could not be removed."); }
    }
    internal async Task SaveAsync(string id, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var host = Host ?? throw new InvalidOperationException("Activate the plugin first."); ct.ThrowIfCancellationRequested();
            var values = new Dictionary<string, string>(_configuration.Values) { [id] = value };
            var next = _configuration with { Values = values };
            host.SetSetting("configuration", next); _configuration = next; host.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }
    internal string RequireKey() => Configured ? Key! : throw new PluginRequestException("API key not configured.", PluginRequestFailureKind.Configuration);
    internal static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = key.Trim();
        if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Invalid API key format.");
        return value;
    }
    internal HttpRequestMessage Request(HttpMethod method, string url, string? key = null, string header = "Authorization")
    {
        var request = new HttpRequestMessage(method, url);
        var token = key ?? RequireKey();
        if (header == "Authorization") request.Headers.Authorization = new("Bearer", token);
        else request.Headers.Add(header, token);
        return request;
    }
    internal static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    internal async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(http, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct); ct.ThrowIfCancellationRequested();
        try { var document = JsonDocument.Parse(json); if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw InvalidResponse(); } return document; }
        catch (JsonException) { throw InvalidResponse(); }
    }
    internal static PluginRequestException InvalidResponse() => new("The provider returned an incomplete or invalid response.", PluginRequestFailureKind.OutputIncomplete);
    internal static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    internal static JsonElement Required(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var result) || result.ValueKind != kind) throw InvalidResponse();
        return result;
    }
    internal static string RequiredText(JsonElement element, string name) => !string.IsNullOrWhiteSpace(Text(element, name)) ? Text(element, name)! : throw InvalidResponse();
    internal static double Number(JsonElement element, string name, double fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : fallback;
    internal static string? Language(string? language) => string.IsNullOrWhiteSpace(language) || language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : language.Trim();
    internal static string[] Terms(string? prompt) => PluginDictionaryTerms.Clip(
        prompt?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [], new(MaxTerms: 100, MaxTotalChars: 4000)).ToArray();
    internal static void Audio(byte[] audio, bool translate, bool supportsTranslation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (translate && !supportsTranslation) throw new NotSupportedException("This provider does not support translation.");
        if (audio.Length == 0) throw new ArgumentException("Audio is empty.", nameof(audio));
    }
    public void Dispose() { http.Dispose(); _gate.Dispose(); }
}
