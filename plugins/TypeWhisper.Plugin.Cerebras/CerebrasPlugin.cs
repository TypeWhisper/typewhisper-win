// Independent port of the legacy Windows provider at 21dc546a, compared with
// TypeWhisperPluginSDK/Plugins/CerebrasPlugin on macOS on September 14, 2026.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cerebras;

/// <summary>Cerebras text processing with host-rendered portable settings.</summary>
public sealed partial class CerebrasPlugin : ILlmProviderPlugin, ILlmRequestHedgingSupport,
    IApiKeyPlugin, IPluginProfileSettings, IPluginConnectionSettings, IPluginSettingsActions
{
    private const string BaseUrl = "https://api.cerebras.ai";
    private readonly HttpClient _http;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private Configuration _configuration = new();
    private string[]? _draftModels;
    private string? _draftModelsKey;

    // Defaults are only a starting point. An explicit refresh uses the account's catalog.
    private static readonly string[] FallbackModels = ["gpt-oss-120b", "qwen-3.8-27b"];

    private sealed record Configuration
    {
        public string? SecretName { get; init; }
        public string? Model { get; init; }
        public string TemperatureMode { get; init; } = "providerDefault";
        public double Temperature { get; init; } = 0.3;
        public string[] Models { get; init; } = [];
    }

    /// <summary>Creates an isolated transport that does not forward keys to redirects.</summary>
    public CerebrasPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(120) }) { }

    internal CerebrasPlugin(HttpClient http) => _http = http;

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.cerebras";
    /// <inheritdoc />
    public string PluginName => "Cerebras";
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public string PluginVersion => "1.1.1";
    /// <inheritdoc />
    public bool IsAvailable => _host is not null && _apiKey is not null;
    /// <inheritdoc />
    public bool IsConfigured => IsAvailable;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => Models(_configuration)
        .Select(id => new PluginModelInfo(id, DisplayName(id)) { IsRecommended = id == SelectedModel }).ToArray();

    private string SelectedModel => _configuration.Model ?? Models(_configuration)[0];
    private static string[] Models(Configuration configuration) => configuration.Models.Length > 0 ? configuration.Models : FallbackModels;
    private static string DisplayName(string id) => id switch
    {
        "gpt-oss-120b" => "GPT-OSS 120B",
        "qwen-3.8-27b" => "Qwen 3.8 27B",
        "llama3.1-8b" => "Llama 3.1 8B",
        "qwen-3-235b-a22b-instruct-2507" => "Qwen 3 235B",
        "zai-glm-4.7" => "ZAI GLM 4.7",
        _ => id
    };

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration") ?? new();
        var models = NormalizeModels(saved.Models ?? []);
        saved = saved with
        {
            Models = models,
            TemperatureMode = saved.TemperatureMode == "custom" ? "custom" : "providerDefault",
            Temperature = double.IsFinite(saved.Temperature) ? Math.Clamp(saved.Temperature, 0, 2) : 0.3
        };
        if (!Models(saved).Contains(saved.Model, StringComparer.Ordinal))
            saved = saved with { Model = Models(saved)[0] };
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
        if (!IsModelId(selected)) throw new ArgumentException("Invalid Cerebras model ID.", nameof(model));
        // Explicit workflow IDs remain usable even when the cached catalog is outdated.
        var reasoningModel = selected is "gpt-oss-120b" or "qwen-3.8-27b" or "zai-glm-4.7" or "kimi-k2.7-code";
        var body = new Dictionary<string, object>
        {
            ["model"] = selected,
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userText } },
            ["max_completion_tokens"] = reasoningModel ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText)
        };
        if (configuration.TemperatureMode == "custom") body["temperature"] = configuration.Temperature;
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        ct.ThrowIfCancellationRequested();
        return ParseChatResponse(json);
    }

    private static string ParseChatResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new PluginRequestException("Cerebras returned an empty response.", PluginRequestFailureKind.EmptyResponse);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "Cerebras");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new JsonException();
            var choice = choices[0];
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String
                || finish.GetString() != "stop") throw new JsonException();
            if (!choice.TryGetProperty("message", out var message)) throw new JsonException();
            // Reasoning and tool calls must never become pasted workflow output.
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(content.GetString())) return content.GetString()!.Trim();
            throw new PluginRequestException("Cerebras returned no visible answer.", PluginRequestFailureKind.EmptyResponse);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException and not PluginRequestException)
        {
            throw new PluginRequestException("Cerebras returned an invalid chat response.", PluginRequestFailureKind.OutputIncomplete);
        }
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct) => _ = await FetchModelsAsync(_apiKey, ct);

    private async Task<string[]> FetchModelsAsync(string? key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey(key));
        using var response = await SendAsync(request, ct);
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String || !IsModelId(id.GetString())) throw new JsonException();
                ids.Add(id.GetString()!);
            }
            var result = NormalizeModels(ids);
            if (result.Length == 0) throw new JsonException();
            return result;
        }
        catch (JsonException)
        {
            throw new PluginRequestException("Cerebras returned an empty or invalid model catalog. The saved list is unchanged.",
                PluginRequestFailureKind.OutputIncomplete);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await OpenAiApiHelper.SendWithErrorHandlingAsync(_http, request, ct); }
        catch (PluginRequestException ex) when (ex.HttpStatusCode == 402)
        {
            throw new PluginRequestException("Cerebras requires available credit or quota. Check billing in your Cerebras account.",
                PluginRequestFailureKind.Permission, 402, isTransient: false);
        }
    }

    private static bool IsModelId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl);
    private static string[] NormalizeModels(IEnumerable<string> models) => models.Where(IsModelId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static string? NormalizeKey(string? key)
    {
        var value = key?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 8192 || value.Any(char.IsControl)) throw new ArgumentException("Invalid API key format.");
        return value;
    }
    private string RequireKey(string? key) => _host is not null && key is not null ? key
        : throw new PluginRequestException("Cerebras API key not configured.", PluginRequestFailureKind.Configuration);
    private void ClearDraft() { _draftModels = null; _draftModelsKey = null; }

    /// <inheritdoc />
    public void Dispose() { _http.Dispose(); _apiKey = null; _host = null; ClearDraft(); }
}
