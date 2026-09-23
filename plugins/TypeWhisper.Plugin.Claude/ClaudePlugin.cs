// Independent port of the legacy Windows provider at 8631b9a0. Model discovery
// and selection behavior compared with the macOS Claude plugin on 2026-09-15.
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Claude;

/// <summary>Anthropic Messages provider with portable, host-rendered settings.</summary>
public sealed partial class ClaudePlugin : ILlmProviderPlugin, ILlmRequestHedgingSupport,
    IApiKeyPlugin, IPluginProfileSettings, IPluginConnectionSettings, IPluginSettingsActions
{
    private const string BaseUrl = "https://api.anthropic.com";
    private readonly HttpClient _http;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private Configuration _configuration = new();
    private string[]? _draftModels;
    private string? _draftModelsKey;
    private long _generation;

    private static readonly string[] FallbackModels =
        ["claude-sonnet-5", "claude-opus-5", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"];

    private sealed record Configuration
    {
        public string? SecretName { get; init; }
        public string? Model { get; init; }
        public string TemperatureMode { get; init; } = "providerDefault";
        public double Temperature { get; init; } = 0.3;
        public string[] Models { get; init; } = [];
    }

    /// <summary>Creates a transport that never forwards keys to redirect targets.</summary>
    public ClaudePlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(120) }) { }
    internal ClaudePlugin(HttpClient http) => _http = http;

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.claude";
    /// <inheritdoc />
    public string PluginName => "Claude";
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public string PluginVersion => "1.1.0";
    /// <inheritdoc />
    public bool IsAvailable => _host is not null && _apiKey is not null;
    /// <inheritdoc />
    public bool IsConfigured => IsAvailable;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => Models(_configuration)
        .Append(SelectedModel).Distinct(StringComparer.Ordinal)
        .Select(id => new PluginModelInfo(id, DisplayName(id)) { IsRecommended = id == SelectedModel }).ToArray();

    private string SelectedModel => _configuration.Model ?? Models(_configuration)[0];
    private static string[] Models(Configuration configuration) => configuration.Models.Length > 0 ? configuration.Models : FallbackModels;
    private static string DisplayName(string id) => id switch
    {
        "claude-sonnet-5" => "Claude Sonnet 5",
        "claude-opus-5" => "Claude Opus 5",
        "claude-sonnet-4-6" => "Claude Sonnet 4.6",
        "claude-haiku-4-5" or "claude-haiku-4-5-20251001" => "Claude Haiku 4.5",
        _ => id
    };

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration") ?? new();
        saved = saved with
        {
            Models = NormalizeModels(saved.Models ?? []),
            Model = IsModelId(saved.Model) ? saved.Model : FallbackModels[0],
            TemperatureMode = saved.TemperatureMode == "custom" ? "custom" : "providerDefault",
            Temperature = double.IsFinite(saved.Temperature) ? Math.Clamp(saved.Temperature, 0, 1) : 0.3
        };
        var key = saved.SecretName is null ? null : NormalizeKey(await host.LoadSecretAsync(saved.SecretName));
        _configuration = saved;
        _apiKey = key;
        _host = host;
        ClearDraft();
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        _host = null;
        _apiKey = null;
        _configuration = new();
        ClearDraft();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RequireKey(_apiKey);
        var configuration = _configuration;
        var selected = string.IsNullOrWhiteSpace(model) ? configuration.Model ?? Models(configuration)[0] : model.Trim();
        if (!IsModelId(selected)) throw new ArgumentException("Invalid Claude model ID.", nameof(model));
        var body = new Dictionary<string, object>
        {
            ["model"] = selected,
            ["system"] = systemPrompt,
            ["messages"] = new[] { new { role = "user", content = userText } },
            ["max_tokens"] = LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
        };
        // Sampling controls are no longer supported on newer models. Match the
        // macOS behavior, with a conservative allowlist for older model families.
        if (configuration.TemperatureMode == "custom" && SupportsTemperature(selected))
            body["temperature"] = configuration.Temperature;
        using var request = Request(HttpMethod.Post, "/v1/messages", key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        ct.ThrowIfCancellationRequested();
        return ParseMessage(json);
    }

    private static bool SupportsTemperature(string id) =>
        id is "claude-sonnet-4-6" or "claude-opus-4-6" or "claude-haiku-4-5" or "claude-haiku-4-5-20251001"
        or "claude-sonnet-4-5" or "claude-sonnet-4-5-20250929" or "claude-opus-4-5" or "claude-opus-4-5-20251101";

    private static string ParseMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new PluginRequestException("Claude returned an empty response.", PluginRequestFailureKind.EmptyResponse);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            LlmResponseTruncationGuard.ThrowIfAnthropicResponseTruncated(root, "Claude");
            if (root.GetProperty("stop_reason").GetString() != "end_turn") throw new JsonException();
            var content = root.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Array) throw new JsonException();
            var answer = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type is "thinking" or "redacted_thinking") continue;
                // Tool use and unrecognized blocks cannot be delivered as a finished text answer.
                if (type != "text") throw new JsonException();
                var value = block.GetProperty("text");
                if (value.ValueKind != JsonValueKind.String) throw new JsonException();
                answer.Append(value.GetString());
            }
            var text = answer.ToString().Trim();
            if (text.Length > 0) return text;
            throw new PluginRequestException("Claude returned no visible answer.", PluginRequestFailureKind.EmptyResponse);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException || ex is InvalidOperationException and not PluginRequestException)
        {
            throw new PluginRequestException("Claude returned an invalid or incomplete message.", PluginRequestFailureKind.OutputIncomplete);
        }
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct) => _ = await FetchModelsAsync(_apiKey, ct);

    private async Task<string[]> FetchModelsAsync(string? key, CancellationToken ct)
    {
        key = RequireKey(key);
        var ids = new List<string>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            for (var page = 0; page < 20; page++)
            {
                ct.ThrowIfCancellationRequested();
                using var request = Request(HttpMethod.Get, "/v1/models?limit=1000" +
                    (cursor is null ? "" : "&after_id=" + Uri.EscapeDataString(cursor)), key);
                using var response = await SendAsync(request, ct);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                var data = root.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array) throw new JsonException();
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    if (!IsModelId(id)) throw new JsonException();
                    ids.Add(id!);
                }
                if (!root.GetProperty("has_more").GetBoolean())
                {
                    if (ids.Count == 0) throw new JsonException();
                    ct.ThrowIfCancellationRequested();
                    return NormalizeModels(ids);
                }
                cursor = root.GetProperty("last_id").GetString();
                if (data.GetArrayLength() == 0 || !IsModelId(cursor) || !cursors.Add(cursor!)) throw new JsonException();
            }
            throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException || ex is InvalidOperationException and not PluginRequestException)
        {
            throw new PluginRequestException("Claude returned an empty, invalid or incomplete model catalog. The saved list is unchanged.",
                PluginRequestFailureKind.OutputIncomplete);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await OpenAiApiHelper.SendWithErrorHandlingAsync(_http, request, ct); }
        catch (PluginRequestException ex) when (ex.HttpStatusCode == 413)
        {
            throw new PluginRequestException("Claude rejected the request because it is too large.",
                PluginRequestFailureKind.RequestTooLarge, 413, ex.RetryAfter);
        }
        catch (PluginRequestException ex) when (ex.HttpStatusCode == 404 && request.Method == HttpMethod.Post)
        {
            throw new PluginRequestException("Claude could not find the selected model. Refresh models and choose an available model in the workflow.",
                PluginRequestFailureKind.InvalidRequest, 404, ex.RetryAfter);
        }
    }

    private static bool IsModelId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl);
    private static string[] NormalizeModels(IEnumerable<string> models) => models.Where(IsModelId).Distinct(StringComparer.Ordinal).ToArray();
    private static string? NormalizeKey(string? key)
    {
        var value = key?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 8192 || value.Any(char.IsControl)) throw new ArgumentException("Invalid API key format.");
        return value;
    }
    private string RequireKey(string? key) => _host is not null && key is not null ? key
        : throw new PluginRequestException("Claude API key not configured.", PluginRequestFailureKind.Configuration);
    private void ClearDraft() { _draftModels = null; _draftModelsKey = null; Interlocked.Increment(ref _generation); }

    /// <inheritdoc />
    public void Dispose() { _http.Dispose(); _apiKey = null; _host = null; ClearDraft(); }
}
