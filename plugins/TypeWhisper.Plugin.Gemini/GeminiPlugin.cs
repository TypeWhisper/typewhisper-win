using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gemini;

/// <summary>
/// Provides Gemini plugin behavior.
/// </summary>
public sealed class GeminiPlugin : ILlmProviderPlugin, ILlmRequestHedgingSupport
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    private const string ApiKeySecretName = "api-key";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels.v2";
    internal const string DefaultModel = "gemini-flash-latest";

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new(DefaultModel, "Gemini Flash Latest") { IsRecommended = true },
        new("gemini-pro-latest", "Gemini Pro Latest"),
        new("gemini-flash-lite-latest", "Gemini Flash-Lite Latest"),
    ];

    private static readonly string[] ExcludedModelTokens =
    [
        "embedding",
        "-image",
        "tts",
        "live",
        "audio",
        "robotics",
        "computer-use",
        "deep-research",
        "omni",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private List<GeminiFetchedModel> _fetchedLlmModels = [];

    /// <summary>
    /// Initializes a new instance of the GeminiPlugin class.
    /// </summary>
    public GeminiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    internal GeminiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public bool SupportsRequestHedging => true;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.gemini";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Google Gemini";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.2.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<GeminiFetchedModel>>(FetchedLlmModelsSettingName) ?? []);
        host.Log(
            PluginLogLevel.Info,
            $"Activated (configured={IsAvailable}, fetchedModels={_fetchedLlmModels.Count})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new GeminiSettingsView(this);

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "Google Gemini";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            if (_fetchedLlmModels.Count == 0)
                return FallbackLlmModels;

            var defaultModelId = ResolveDefaultModelId(_fetchedLlmModels);
            return _fetchedLlmModels
                .OrderByDescending(model => string.Equals(
                    model.Id,
                    defaultModelId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new PluginModelInfo(model.Id, model.DisplayName ?? model.Id)
                {
                    IsRecommended = string.Equals(model.Id, defaultModelId, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
        }
    }

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);

        var modelId = string.IsNullOrWhiteSpace(model) ? SupportedModels.First().Id : model;
        return await SendChatCompletionAsync(modelId, systemPrompt, userText, ct);
    }

    // API key and model catalog management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal IReadOnlyList<GeminiFetchedModel> FetchedLlmModels => _fetchedLlmModels;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var wasAvailable = IsAvailable;
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);
        var catalogChanged = changed && _fetchedLlmModels.Count > 0;

        _apiKey = normalized;
        if (catalogChanged)
            _fetchedLlmModels = [];

        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);

            if (catalogChanged)
                _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);

            if ((changed && wasAvailable != IsAvailable) || catalogChanged)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SetFetchedLlmModels(IEnumerable<GeminiFetchedModel> models)
    {
        var normalized = NormalizeFetchedLlmModels(models);
        if (ModelCatalogsEqual(_fetchedLlmModels, normalized))
            return;

        _fetchedLlmModels = normalized;
        _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<GeminiFetchedModel>?> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{BaseUrl}/models", _apiKey!);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Model catalog request failed with HTTP status {(int)response.StatusCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var catalog = JsonSerializer.Deserialize<GeminiCompatibleModelsResponse>(json, JsonOptions);
            return NormalizeFetchedLlmModels(catalog?.Data?.OfType<GeminiCompatibleModel>() ?? []);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _host?.Log(PluginLogLevel.Warning, "Model catalog request timed out.");
            return null;
        }
        catch (OperationCanceledException)
        {
            _host?.Log(PluginLogLevel.Debug, "Model catalog request was canceled by the caller.");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog request failed with {ex.GetType().Name}.");
            return null;
        }
        catch (JsonException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog parsing failed with {ex.GetType().Name}.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog request failed with {ex.GetType().Name}.");
            return null;
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{BaseUrl}/models", normalized);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsCompatibleChatModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var normalized = NormalizeModelId(id);
        if (!normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ExcludedModelTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> SendChatCompletionAsync(
        string model,
        string systemPrompt,
        string userText,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            },
            ["max_tokens"] = 2048,
        };

        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{BaseUrl}/chat/completions",
            _apiKey!);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json);
    }

    private static string ParseChatCompletionResponse(string json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new PluginRequestException(
                    "The provider returned a malformed response.",
                    PluginRequestFailureKind.EmptyResponse,
                    innerException: ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        throw new PluginRequestException(
            "The provider returned an empty response.",
            PluginRequestFailureKind.EmptyResponse);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiCompatibleModel> models) =>
        NormalizeFetchedLlmModels(models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                model.Id!,
                model.DisplayName)));

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiFetchedModel> models) =>
        models
            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                NormalizeModelId(model.Id),
                string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim()))
            .Where(model => IsCompatibleChatModelId(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(model => string.Equals(model.Id, DefaultModel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolveDefaultModelId(IReadOnlyList<GeminiFetchedModel> models)
    {
        var alias = models.FirstOrDefault(model => string.Equals(
            model.Id,
            DefaultModel,
            StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
            return alias.Id;

        return models
            .Where(model => model.Id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                && model.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)
                && !model.Id.Contains("lite", StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .FirstOrDefault()
            ?? models[0].Id;
    }

    private static Version GetModelVersion(string id)
    {
        const string prefix = "gemini-";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new Version(0, 0);

        var versionEnd = id.IndexOf('-', prefix.Length);
        if (versionEnd <= prefix.Length)
            return new Version(0, 0);

        var versionText = id[prefix.Length..versionEnd];
        if (!versionText.Contains('.'))
            versionText += ".0";

        return Version.TryParse(versionText, out var version)
            ? version
            : new Version(0, 0);
    }

    private static bool ModelCatalogsEqual(
        IReadOnlyList<GeminiFetchedModel> first,
        IReadOnlyList<GeminiFetchedModel> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal));

    private static string NormalizeModelId(string id)
    {
        var normalized = id.Trim();
        return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? normalized["models/".Length..]
            : normalized;
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed record GeminiFetchedModel(string Id, string? DisplayName);

internal sealed record GeminiCompatibleModelsResponse(
    [property: JsonPropertyName("data")] List<GeminiCompatibleModel?>? Data);

internal sealed record GeminiCompatibleModel(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);
