using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Webhook;

/// <summary>Delivers completed text through the portable host post-processing pipeline.</summary>
public sealed class WebhookPlugin : IPostProcessorPlugin, IPluginTextSettings, IPluginSettingsActions
{
    private sealed record Endpoint(string Id, string Name, string Url, string Method, bool Enabled, string Workflows);
    private readonly HttpClient _http;
    private IPluginHostServices? _host;
    private List<Endpoint> _endpoints = [];
    private readonly Queue<string> _log = new();
    /// <summary>Creates a webhook provider without sending requests.</summary>
    public WebhookPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) }) { }
    internal WebhookPlugin(HttpClient http) => _http = http;
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.webhook";
    /// <inheritdoc />
    public string PluginName => "Webhook";
    /// <inheritdoc />
    public string PluginVersion => "1.2.0";
    /// <inheritdoc />
    public string ProcessorName => "Webhook delivery";
    /// <inheritdoc />
    public int Priority => 900;
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    { _host = host; _endpoints = host.GetSetting<List<Endpoint>>("endpoints.v2") ?? []; return Task.CompletedTask; }
    /// <inheritdoc />
    public Task DeactivateAsync() { _host = null; return Task.CompletedTask; }
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => _endpoints.SelectMany(e => new PluginTextSetting[] {
        new(e.Id + ":name", "Name", "", e.Name, 200),
        new(e.Id + ":url", "URL", "HTTPS endpoint, or HTTP on localhost for local integrations.", e.Url, 4096),
        new(e.Id + ":method", "HTTP method", "", e.Method) { Choices = [new("POST","POST"),new("PUT","PUT")] },
        new(e.Id + ":workflows", "Workflow filter", "One workflow name per line; empty matches all workflows.", e.Workflows) { IsMultiline = true },
        new(e.Id + ":headers", "Replace headers", "JSON object. Stored in the secret store; leave empty to keep existing headers. Use {} to clear.", "") { IsMultiline = true },
        new(e.Id + ":enabled", "Enabled", "Sends transcription text when this endpoint and plugin are enabled.", e.Enabled.ToString().ToLowerInvariant()) { Choices = [new("false","Off"),new("true","On")] }
    }).ToArray();
    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var host = _host ?? throw new InvalidOperationException("Plugin is not active.");
        var parts = id.Split(':');
        var index = _endpoints.FindIndex(e => e.Id == parts[0]);
        if (parts.Length != 2 || index < 0) throw new ArgumentException("Unknown setting.", nameof(id));
        var field = TextSettings.Single(s => s.Id == id);
        if (value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value)) throw new ArgumentException("Invalid setting.", nameof(value));
        var current = _endpoints[index];
        if (parts[1] == "headers")
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var headers = JsonSerializer.Deserialize<Dictionary<string,string>>(value) ?? throw new ArgumentException("Expected a JSON object.");
            using var validation = new HttpRequestMessage();
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || header.Value is null || header.Value.Contains('\r') || header.Value.Contains('\n') || header.Key.Equals("Host",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid header.");
                validation.Headers.Add(header.Key, header.Value);
            }
            await host.StoreSecretAsync("headers-" + current.Id, JsonSerializer.Serialize(headers));
            return;
        }
        if (parts[1] == "url" && value.Length > 0) ValidateUrl(value);
        if (parts[1] == "enabled" && value == "true") ValidateUrl(current.Url);
        var updated = current with { Name = parts[1] == "name" ? value.Trim() : current.Name,
            Url = parts[1] == "url" ? value.Trim() : current.Url, Method = parts[1] == "method" ? value : current.Method,
            Workflows = parts[1] == "workflows" ? value : current.Workflows, Enabled = parts[1] == "enabled" ? bool.Parse(value) : current.Enabled };
        var next = _endpoints.ToList(); next[index] = updated; Save(next);
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("add", "Add webhook", "Creates a disabled endpoint."),
        new("log", "Delivery log", "Shows the last twenty delivery outcomes without response bodies or credentials."),
        .. _endpoints.Select(e => new PluginSettingsAction("remove:" + e.Id, "Remove " + e.Name, "Removes this endpoint."))];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == "log") return string.Join("\n", _log);
        if (id == "add") Save([.. _endpoints, new(Guid.NewGuid().ToString("N"), "New webhook", "", "POST", false, "")]);
        else if (id.StartsWith("remove:") && _endpoints.Any(e => e.Id == id[7..]))
        { Save(_endpoints.Where(e => e.Id != id[7..]).ToList()); await _host!.DeleteSecretAsync("headers-" + id[7..]); }
        else throw new ArgumentException("Unknown action.", nameof(id));
        return "Saved.";
    }
    /// <inheritdoc />
    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        foreach (var endpoint in _endpoints.Where(e => e.Enabled).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var workflows = endpoint.Workflows.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (workflows.Length > 0 && !workflows.Contains(context.ProfileName, StringComparer.Ordinal)) continue;
            try
            {
                ValidateUrl(endpoint.Url);
                using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Url);
                var stored = await _host!.LoadSecretAsync("headers-" + endpoint.Id);
                foreach (var h in JsonSerializer.Deserialize<Dictionary<string,string>>(stored ?? "{}")!) request.Headers.Add(h.Key,h.Value);
                request.Content = JsonContent.Create(new { text, detectedLanguage = context.SourceLanguage,
                    durationSeconds = context.AudioDurationSeconds, profileName = context.ProfileName, timestamp = DateTimeOffset.UtcNow });
                using var response = await _http.SendAsync(request, ct);
                Log(endpoint.Name + ": HTTP " + (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or ArgumentException)
            { Log(endpoint.Name + ": delivery failed (" + ex.GetType().Name + ")."); }
        }
        return text;
    }
    private static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("Use an HTTPS URL or HTTP localhost URL.");
    }
    private void Save(List<Endpoint> next) { _host!.SetSetting("endpoints.v2", next); _endpoints = next; _host.NotifyCapabilitiesChanged(); }
    private void Log(string message) { _log.Enqueue(message); while (_log.Count > 20) _log.Dequeue(); }
    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
