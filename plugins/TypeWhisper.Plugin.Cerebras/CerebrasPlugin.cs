// Independent port of the legacy Windows provider at 21dc546a, compared with
// TypeWhisperPluginSDK/Plugins/CerebrasPlugin on macOS on September 14, 2026.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly ModelCatalogAutoRefresh _modelRefresh;
    // The background model refresh writes the saved configuration from another thread. This lock orders that
    // write with the settings commits and guards the host, key and configuration it checks.
    private readonly object _configurationLock = new();
    private IPluginHostServices? _host;
    private string? _apiKey;
    private Configuration _configuration = new();
    private string[]? _draftModels;
    private string? _draftModelsKey;
    private DateTimeOffset _draftModelsFetchedAt;

    // Defaults are only a starting point. A refresh uses the account's catalog. In this order they are also the
    // default model when the saved one is not offered.
    private static readonly string[] FallbackModels = ["gpt-oss-120b", "qwen-3.8-27b"];

    private sealed record Configuration
    {
        public string? SecretName { get; init; }
        public string? Model { get; init; }
        public string TemperatureMode { get; init; } = "providerDefault";
        public double Temperature { get; init; } = 0.3;
        public string[] Models { get; init; } = [];
        /// <summary>When <see cref="Models"/> was fetched. Lists saved before 1.2.0 have none.</summary>
        [JsonConverter(typeof(FetchDateConverter))]
        public DateTimeOffset? ModelsFetchedAtUtc { get; init; }
    }

    /// <summary>Creates an isolated transport that does not forward keys to redirects.</summary>
    public CerebrasPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(120) }, refreshModelsAutomatically: ModelCatalogAutoRefresh.AllowedInThisProcess) { }

    // Tests use this constructor. It leaves the background refresh off, so they never reach Cerebras.
    internal CerebrasPlugin(HttpClient http, bool refreshModelsAutomatically = false, TimeProvider? clock = null)
    {
        _http = http;
        _modelRefresh = new ModelCatalogAutoRefresh(refreshModelsAutomatically, clock);
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.cerebras";
    /// <inheritdoc />
    public string PluginName => "Cerebras";
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public string PluginVersion => "1.2.0";
    /// <inheritdoc />
    public bool IsAvailable => _host is not null && _apiKey is not null;
    /// <inheritdoc />
    public bool IsConfigured => IsAvailable;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            // One snapshot: the background refresh may replace the configuration meanwhile.
            var configuration = _configuration;
            var selected = SelectedModelOf(configuration);
            return Models(configuration)
                .Select(id => new PluginModelInfo(id, DisplayName(id)) { IsRecommended = id == selected }).ToArray();
        }
    }

    private string SelectedModel => SelectedModelOf(_configuration);

    /// <summary>
    /// The saved model while the list offers it, otherwise the default model. The saved value stays, so it applies
    /// again once a later list offers it.
    /// </summary>
    private static string SelectedModelOf(Configuration configuration)
    {
        var models = Models(configuration);
        if (configuration.Model is { } saved && models.Contains(saved, StringComparer.Ordinal)) return saved;
        return DefaultModelOf(models);
    }

    // The list is sorted by ID, so its first entry is no sensible default.
    private static string DefaultModelOf(string[] models) =>
        FallbackModels.FirstOrDefault(model => models.Contains(model, StringComparer.Ordinal)) ?? models[0];

    /// <summary>
    /// What the settings field shows: the saved model even when the list lacks it, otherwise the list's default. The
    /// editor sends every field on Save, and a page opened before a background refresh still sends this value, so an
    /// untouched field never replaces the saved model.
    /// </summary>
    private static string ShownModelOf(Configuration configuration, string[] listed) =>
        configuration.Model ?? DefaultModelOf(listed);

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
        _modelRefresh.Cancel();
        var saved = host.GetSetting<Configuration>("configuration") ?? new();
        var models = NormalizeModels(saved.Models ?? []);
        saved = saved with
        {
            Models = models,
            // The fallback list was never fetched, so a date without a list means nothing.
            ModelsFetchedAtUtc = models.Length > 0 ? saved.ModelsFetchedAtUtc : null,
            // A saved model the list does not offer is kept; SelectedModelOf decides what is used.
            Model = IsModelId(saved.Model) ? saved.Model : null,
            TemperatureMode = saved.TemperatureMode == "custom" ? "custom" : "providerDefault",
            Temperature = double.IsFinite(saved.Temperature) ? Math.Clamp(saved.Temperature, 0, 2) : 0.3
        };
        var key = saved.SecretName is null ? null : NormalizeKey(await host.LoadSecretAsync(saved.SecretName));
        lock (_configurationLock)
        {
            _configuration = saved;
            _apiKey = key;
            _host = host;
        }
        ClearDraft();
        // A list without a date, saved by an older version, stays in use until the refresh replaces it.
        StartModelRefreshIfStale();
        // The app often runs for days without activating plugins again. The hourly check reloads a list that ages
        // meanwhile and retries a failed refresh.
        _modelRefresh.StartRechecks(StartModelRefreshIfStale);
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        lock (_configurationLock)
        {
            _host = null;
            _apiKey = null;
            _configuration = new();
        }
        // Only after the key is gone: a check still running then starts nothing, and a refresh it started is cancelled.
        _modelRefresh.StopRechecks();
        _modelRefresh.Cancel();
        ClearDraft();
        return Task.CompletedTask;
    }

    internal Task ModelRefreshTask => _modelRefresh.Current;
    internal bool RefreshesModelsAutomatically => _modelRefresh.Enabled;
    internal DateTimeOffset? ModelsFetchedAt => _configuration.ModelsFetchedAtUtc;

    /// <summary>
    /// Cerebras retires models, and a list that only changes on Refresh models keeps offering them. Activation, a key
    /// change and the hourly recheck reload a saved list that is a day old or has no date.
    /// </summary>
    private void StartModelRefreshIfStale()
    {
        // The hourly recheck runs this on a timer thread. Checking and starting under the lock means a deactivation or
        // key change either comes first, so nothing starts with the old key, or cancels the refresh started here.
        lock (_configurationLock)
        {
            var host = _host;
            var key = _apiKey;
            if (host is null || key is null || !_modelRefresh.IsStale(_configuration.ModelsFetchedAtUtc)) return;
            _modelRefresh.Start(ct => RefreshSavedModelsAsync(host, key, ct), ex => LogRefreshFailure(host, ex));
        }
    }

    // A failed or empty catalog throws in FetchModelsAsync, so the saved list stays.
    private async Task RefreshSavedModelsAsync(IPluginHostServices host, string key, CancellationToken ct)
    {
        var models = await FetchModelsAsync(key, ct);
        bool changed;
        lock (_configurationLock)
        {
            // A result that arrives after deactivation or a key change belongs to another connection.
            if (ct.IsCancellationRequested || !ReferenceEquals(_host, host) || !string.Equals(_apiKey, key, StringComparison.Ordinal))
                return;
            // Only the saved list and its date change; a pending draft and the other settings stay as they are.
            var current = _configuration;
            var next = current with { Models = models, ModelsFetchedAtUtc = _modelRefresh.UtcNow };
            host.SetSetting("configuration", next);
            _configuration = next;
            changed = !current.Models.SequenceEqual(models, StringComparer.Ordinal);
        }
        if (changed) host.NotifyCapabilitiesChanged();
    }

    // Kind and status only: provider error messages can quote the response.
    private static void LogRefreshFailure(IPluginHostServices host, Exception ex)
    {
        var reason = ex is PluginRequestException request
            ? request.HttpStatusCode is { } status ? $"{request.FailureKind}, HTTP {status}" : request.FailureKind.ToString()
            : ex.GetType().Name;
        host.Log(PluginLogLevel.Warning, $"Automatic Cerebras model refresh failed ({reason}). The saved list is unchanged.");
    }

    /// <inheritdoc />
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RequireKey(_apiKey);
        var configuration = _configuration;
        var selected = string.IsNullOrWhiteSpace(model) ? SelectedModelOf(configuration) : model.Trim();
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
    private void ClearDraft() { _draftModels = null; _draftModelsKey = null; _draftModelsFetchedAt = default; }

    /// <inheritdoc />
    public void Dispose()
    {
        // The key goes first, so a recheck still running starts nothing. Then the rechecks stop and a refresh in
        // flight is cancelled before its transport goes away.
        lock (_configurationLock) { _apiKey = null; _host = null; }
        _modelRefresh.Dispose();
        _http.Dispose();
        ClearDraft();
    }

    /// <summary>Reads a damaged fetch date as no date, so activation refreshes the list instead of failing to load.</summary>
    private sealed class FetchDateConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value) && value != default)
                return value.ToUniversalTime();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value is { } fetchedAt) writer.WriteStringValue(fetchedAt.ToUniversalTime());
            else writer.WriteNullValue();
        }
    }
}
