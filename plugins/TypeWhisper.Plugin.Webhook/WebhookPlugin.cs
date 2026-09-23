using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Webhook;

/// <summary>Explicitly configured JSON delivery without changing the transcribed text.</summary>
public sealed partial class WebhookPlugin : IPostProcessorPlugin, IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    private sealed record Endpoint(string Id, string Name, string Url, string Method, bool Enabled, string Workflows, string? SecretName = null);
    private readonly HttpClient _http;
    private readonly TimeSpan _deliveryBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPluginHostServices? _host;
    private Endpoint[] _endpoints = [];
    private string? _editing;
    private readonly Queue<string> _log = new();
    private readonly object _logLock = new();
    /// <summary>Creates an isolated transport; redirects and ambient cookies are disabled.</summary>
    public WebhookPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) }) { }
    internal WebhookPlugin(HttpClient http, TimeSpan? deliveryBudget = null)
    { _http = http; _deliveryBudget = deliveryBudget ?? TimeSpan.FromSeconds(10); }
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.webhook";
    /// <inheritdoc />
    public string PluginName => "Webhook";
    /// <inheritdoc />
    public string PluginVersion => "1.3.2";
    /// <inheritdoc />
    public string ProcessorName => "Webhook delivery";
    /// <inheritdoc />
    public int Priority => 900;
    private IPluginHostServices Host => _host ?? throw new InvalidOperationException("Plugin is not active.");
    private string L(string english, string german) => (PortableLocalization.TryGet(_host)?.CurrentLanguage ?? CultureInfo.CurrentUICulture.Name)
        .StartsWith("de", StringComparison.OrdinalIgnoreCase) ? german : english;
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        var endpoints = host.GetSetting<Endpoint[]>("endpoints.v2") ?? [];
        if (endpoints.Length > 50 || endpoints.Any(e => e is null || !Guid.TryParse(e.Id, out _) || e.Name is null || e.Url is null || e.Workflows is null || e.Method is not ("POST" or "PUT")) || endpoints.Select(e => e.Id).Distinct().Count() != endpoints.Length)
            throw new InvalidDataException("Invalid webhook configuration. The saved file has been preserved.");
        _host = host; _endpoints = endpoints; _editing = endpoints.FirstOrDefault()?.Id;
        lock (_logLock) _log.Clear();
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task DeactivateAsync() { _host = null; _endpoints = []; _editing = null; return Task.CompletedTask; }
    private Endpoint? Selected => _endpoints.FirstOrDefault(e => e.Id == _editing) ?? _endpoints.FirstOrDefault();
    /// <inheritdoc />
    public string ProfileSelectorId => "webhook";
    /// <inheritdoc />
    public string? AddProfileActionId => _endpoints.Length < 50 ? "add" : null;
    /// <inheritdoc />
    public string? RemoveProfileActionId => Selected is { } e ? "remove:" + e.Id : null;
    /// <inheritdoc />
    public bool ShowApiKeySettings => false;
    /// <inheritdoc />
    public string ConnectionIdentity => Selected?.Id ?? "none";
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var fields = new List<PluginTextSetting> {
                new(ProfileSelectorId, L("Destinations", "Ziele"),
                    L("Send dictation as JSON to your automation, such as n8n or a local service. Test with sample text first; enable delivery when ready. Delivery runs before text insertion.",
                        "Sende Diktate als JSON an deine Automation, etwa n8n oder einen lokalen Dienst. Zuerst mit Beispieltext testen, dann die Zustellung aktivieren. Die Zustellung erfolgt vor dem Einfügen des Textes."), ConnectionIdentity)
                { Choices = _endpoints.Select(e => new PluginSettingChoice(e.Id, e.Name)).ToArray() }
            };
            if (Selected is { } e) fields.AddRange(new PluginTextSetting[] {
                Field(e, "name", L("Name", "Name"), "", e.Name, 200),
                Field(e, "url", L("Destination URL", "Ziel-URL"), L("HTTPS endpoint, or HTTP on localhost for a local test.", "HTTPS-Endpunkt oder HTTP auf localhost für einen lokalen Test."), e.Url, 4096),
                Field(e, "enabled", L("Send after dictation", "Nach dem Diktieren senden"), L("Off keeps the destination saved without sending dictations.", "Aus behält das Ziel gespeichert, ohne Diktate zu senden."), e.Enabled ? "true" : "false")
                    with { Choices = [new("false", L("Off", "Aus")), new("true", L("On", "An"))] },
                Field(e, "method", L("HTTP method", "HTTP-Methode"), "", e.Method) with { Choices = [new("POST", "POST"), new("PUT", "PUT")] },
                Field(e, "workflows", L("Only these workflows (optional)", "Nur diese Workflows (optional)"), L("Choose one or more workflows. All workflows also includes dictation without a workflow.", "Wähle einen oder mehrere Workflows. Alle Workflows schließt Diktate ohne Workflow ein."), e.Workflows, 4000) with { IsMultiline = true },
                Field(e, "headers", L("Authentication headers (optional)", "Authentifizierungs-Header (optional)"),
                    L("JSON object, for example {\"Authorization\":\"Bearer ...\"}. Encrypted after saving. Blank keeps saved headers; {} removes them. Content-Type is automatic.",
                        "JSON-Objekt, z. B. {\"Authorization\":\"Bearer ...\"}. Nach dem Speichern verschlüsselt. Leer behält vorhandene Header, {} entfernt sie. Content-Type wird automatisch gesetzt."), "", 8192) with { IsMultiline = true }
            });
            return fields;
        }
    }
    private static PluginTextSetting Field(Endpoint e, string id, string title, string description, string value, int maximum = 32768) =>
        new(e.Id + ":" + id, title, description, value, maximum) { Section = PluginSettingsSection.Connection };

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == ProfileSelectorId)
        {
            if (!_endpoints.Any(e => e.Id == value)) throw new ArgumentException("Unknown destination.");
            _editing = value; return Task.CompletedTask;
        }
        return SaveProfileSettingsAsync(ConnectionIdentity, new Dictionary<string,string> { [id] = value }, null, ct);
    }

    private (Endpoint Endpoint, string? Headers) ReadDraft(string profileId, IReadOnlyDictionary<string,string> values)
    {
        if (Selected is not { } selected || selected.Id != profileId) throw new ArgumentException("The selected destination changed. Reopen its settings.");
        var next = selected; string? headers = null;
        var fields = TextSettings.ToDictionary(f => f.Id);
        foreach (var (id, value) in values)
        {
            if (!fields.TryGetValue(id, out var field) || !id.StartsWith(profileId + ":", StringComparison.Ordinal) || value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
                throw new ArgumentException("Invalid webhook setting.");
            switch (id[(profileId.Length + 1)..])
            {
                case "name": next = next with { Name = value.Trim() }; break;
                case "url": next = next with { Url = value.Trim() }; break;
                case "enabled": next = next with { Enabled = bool.Parse(value) }; break;
                case "method": next = next with { Method = value }; break;
                case "workflows": next = next with { Workflows = value }; break;
                case "headers": if (!string.IsNullOrWhiteSpace(value)) headers = JsonSerializer.Serialize(ParseHeaders(value)); break;
            }
        }
        if (string.IsNullOrWhiteSpace(next.Name) || next.Name.Any(char.IsControl)) throw new ArgumentException("Enter a destination name.");
        if (next.Url.Length > 0 || next.Enabled) ValidateUrl(next.Url);
        return (next, headers);
    }

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string,string> values, string? apiKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var host = Host;
            var (endpoint, headers) = ReadDraft(profileId, values);
            var oldSecret = endpoint.SecretName ?? "headers-" + endpoint.Id;
            string? staged = null;
            try
            {
                if (headers is not null)
                {
                    staged = "headers-" + endpoint.Id + "-" + Guid.NewGuid().ToString("N");
                    await host.StoreSecretAsync(staged, headers).ConfigureAwait(false);
                    endpoint = endpoint with { SecretName = staged };
                }
                ct.ThrowIfCancellationRequested();
                Save(_endpoints.Select(e => e.Id == endpoint.Id ? endpoint : e).ToArray());
            }
            catch
            {
                if (staged is not null) await CleanupSecretAsync(host, staged).ConfigureAwait(false);
                throw;
            }
            if (staged is not null) await CleanupSecretAsync(host, oldSecret).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [
        .. AddProfileActionId is null ? Array.Empty<PluginSettingsAction>() : new[] { Action("add", L("Add destination", "Ziel hinzufügen")) },
        .. Selected is not { } e ? Array.Empty<PluginSettingsAction>() : new[] {
            Action("test:" + e.Id, L("Send test message", "Testnachricht senden")),
            Action("log", L("Recent deliveries", "Letzte Zustellungen")),
            Action("remove:" + e.Id, L("Remove destination…", "Ziel entfernen…")) }
    ];
    private static PluginSettingsAction Action(string id, string title) => new(id, title, "") { Section = PluginSettingsSection.Connection };

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var host = Host;
            if (id == "log") { lock (_logLock) return _log.Count == 0 ? L("No deliveries yet.", "Noch keine Zustellungen.") : string.Join("\n", _log); }
            if (id == "add" && _endpoints.Length < 50)
            {
                var endpoint = new Endpoint(Guid.NewGuid().ToString("N"), L("New destination", "Neues Ziel") + " " + (_endpoints.Length + 1), "", "POST", false, "");
                Save([.. _endpoints, endpoint]); _editing = endpoint.Id;
            }
            else if (Selected is { } e && id == "remove:" + e.Id)
            {
                Save(_endpoints.Where(x => x.Id != e.Id).ToArray()); _editing = _endpoints.FirstOrDefault()?.Id;
                await CleanupSecretAsync(host, e.SecretName ?? "headers-" + e.Id).ConfigureAwait(false);
            }
            else throw new ArgumentException("Unknown or stale action.");
            return L("Saved.", "Gespeichert.");
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId, IReadOnlyDictionary<string,string> values, string? apiKey, CancellationToken ct)
    {
        Endpoint endpoint; string? headers;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (profileId != ConnectionIdentity) throw new ArgumentException("The selected destination changed.");
            if (actionId == "log") { lock (_logLock) return new(_log.Count == 0 ? L("No deliveries yet.", "Noch keine Zustellungen.") : string.Join("\n", _log)); }
            if (actionId != "test:" + profileId) throw new ArgumentException("Unknown action.");
            (endpoint, headers) = ReadDraft(profileId, values);
            ValidateUrl(endpoint.Url);
            // Saved credentials belong to the saved URL. A changed test URL must not receive them implicitly.
            headers ??= endpoint.Url == Selected!.Url ? await Host.LoadSecretAsync(endpoint.SecretName ?? "headers-" + endpoint.Id).ConfigureAwait(false) : "{}";
        }
        finally { _gate.Release(); }
        return new(await DeliverAsync(endpoint, headers, "Hallo TypeWhisper!", new() { SourceLanguage = "de", ProfileName = "TypeWhisper test" }, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_deliveryBudget);
        foreach (var endpoint in _endpoints.Where(e => e.Enabled).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) break;
            var workflows = endpoint.Workflows.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (workflows.Length > 0 && !workflows.Contains(context.ProfileName, StringComparer.Ordinal)) continue;
            try
            {
                var stored = await Host.LoadSecretAsync(endpoint.SecretName ?? "headers-" + endpoint.Id).WaitAsync(budget.Token).ConfigureAwait(false);
                await DeliverAsync(endpoint, stored, text, context, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            { Log(endpoint.Name, L("Delivery timed out; remaining destinations were skipped and dictation was preserved.", "Zustellung abgelaufen; weitere Ziele wurden übersprungen und das Diktat bleibt erhalten.")); break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Log(endpoint.Name, L("Delivery failed; dictation was preserved.", "Zustellung fehlgeschlagen; Diktat bleibt erhalten.")); }
        }
        return text;
    }

    private async Task<string> DeliverAsync(Endpoint endpoint, string? headers, string text, PostProcessingContext context, CancellationToken ct)
    {
        try
        {
            ValidateUrl(endpoint.Url);
            using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Url);
            foreach (var h in ParseHeaders(headers ?? "{}")) request.Headers.Add(h.Key, h.Value);
            request.Content = JsonContent.Create(new { text, detectedLanguage = context.SourceLanguage,
                durationSeconds = context.AudioDurationSeconds, profileName = context.ProfileName, timestamp = DateTimeOffset.UtcNow });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var message = (response.IsSuccessStatusCode ? L("Delivered", "Zugestellt") : L("Delivery failed", "Zustellung fehlgeschlagen")) + " (HTTP " + (int)response.StatusCode + ").";
            Log(endpoint.Name, message); return message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = L("Delivery failed. Check the destination and authentication headers.", "Zustellung fehlgeschlagen. Ziel und Authentifizierungs-Header prüfen.");
            Log(endpoint.Name, message); return message;
        }
    }

    private static Dictionary<string,string> ParseHeaders(string json)
    {
        Dictionary<string,string>? headers;
        try { headers = JsonSerializer.Deserialize<Dictionary<string,string>>(json); }
        catch (JsonException ex) { throw new ArgumentException("Expected a JSON object of header names and text values.", ex); }
        if (headers is null) throw new ArgumentException("Expected a JSON object of header names and text values.");
        if (headers.Count > 30) throw new ArgumentException("Too many headers.");
        using var validation = new HttpRequestMessage();
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null || value.Any(c => c is '\r' or '\n' or '\0') ||
                new[] { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Content-Type" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid request header. Content-Type is supplied automatically.");
            try { validation.Headers.Add(key, value); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            { throw new ArgumentException("Invalid request header. Content headers are not allowed here.", ex); }
        }
        return headers;
    }
    private static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Use an HTTPS URL or HTTP localhost URL without credentials or a fragment.");
    }
    private void Save(Endpoint[] next) { Host.SetSetting("endpoints.v2", next); _endpoints = next; }
    private static async Task CleanupSecretAsync(IPluginHostServices host, string key)
    {
        try { await host.DeleteSecretAsync(key).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { host.Log(PluginLogLevel.Warning, "An inactive webhook header secret could not be removed."); }
    }
    private void Log(string name, string message) { lock (_logLock) { _log.Enqueue(DateTimeOffset.Now.ToString("HH:mm:ss") + " · " + name + ": " + message); while (_log.Count > 20) _log.Dequeue(); } }
    /// <inheritdoc />
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
