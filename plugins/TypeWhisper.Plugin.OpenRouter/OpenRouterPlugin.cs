// Protocol snapshot from plugins/TypeWhisper.Plugin.OpenRouter at ce38c355.
// Independent portable implementation; no legacy settings or assembly dependencies.
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

/// <summary>
/// Provides open router plugin behavior.
/// </summary>
public sealed partial class OpenRouterPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ILlmRequestHedgingSupport, IApiKeyPlugin, IPluginProfileSettings, IPluginConnectionSettings, IPluginSettingsActions
{
    private const string BaseUrl = "https://openrouter.ai/api";
    private const string ApiKeySecretName = "api-key";
    private const string FetchedModelsSettingName = "fetchedModels";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels";
    private const string SelectedTranscriptionModelSettingName = "selectedTranscriptionModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string UserSelectedLlmModelSettingName = "userSelectedLlmModel";
    private const string TemperatureModeSettingName = "llmTemperatureMode";
    private const string TemperatureValueSettingName = "llmTemperatureValue";
    private const string TemperatureModeProviderDefault = "providerDefault";
    private const string TemperatureModeCustom = "custom";
    internal const string DefaultLlmModelId = "openrouter/free";
    private const string DefaultLlmModelName = "OpenRouter: Free Models Router (free)";
    private const string LegacyFallbackDefaultLlmModelId = "openai/gpt-4o";
    internal const string DefaultTranscriptionModelId = "openai/whisper-large-v3-turbo";

    /// <inheritdoc />
    public bool SupportsRequestHedging => true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedTranscriptionModelId;
    private string? _selectedLlmModelId;
    private bool _hasUserSelectedLlmModel;
    private string _temperatureMode = TemperatureModeProviderDefault;
    private double _temperatureValue = 0.3;
    private List<OpenRouterFetchedModel> _fetchedTranscriptionModels = [];
    private List<OpenRouterFetchedModel> _fetchedModels = [];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackTranscriptionModels =
    [
        new(DefaultTranscriptionModelId, "OpenAI: Whisper Large V3 Turbo") { IsRecommended = true },
        new("openai/whisper-large-v3", "OpenAI: Whisper Large V3"),
        new("openai/whisper-1", "OpenAI: Whisper 1"),
        new("openai/gpt-4o-mini-transcribe", "OpenAI: GPT-4o Mini Transcribe"),
        new("openai/gpt-4o-transcribe", "OpenAI: GPT-4o Transcribe"),
        new("google/chirp-3", "Google: Chirp 3"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackModels =
    [
        new(DefaultLlmModelId, DefaultLlmModelName) { IsRecommended = true },
        new(LegacyFallbackDefaultLlmModelId, "OpenAI: GPT-4o"),
        new("anthropic/claude-sonnet-4", "Anthropic: Claude Sonnet 4"),
        new("google/gemini-2.5-flash-preview", "Google: Gemini 2.5 Flash"),
        new("meta-llama/llama-3.3-70b-instruct", "Meta: Llama 3.3 70B"),
    ];

    private static readonly OpenRouterFetchedModel DefaultFetchedModel =
        new(DefaultLlmModelId, DefaultLlmModelName, "0", "0");

    /// <summary>
    /// Initializes a new instance of the OpenRouterPlugin class.
    /// </summary>
    public OpenRouterPlugin()
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) })
    {
    }

    internal OpenRouterPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.openrouter";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "OpenRouter";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.1.2";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        if (host.GetSetting<Configuration>("configuration") is { } saved)
        {
            ApplyConfiguration(saved);
            _apiKey = saved.SecretName is { } secret ? NormalizeApiKey(await host.LoadSecretAsync(secret)) : null;
            NormalizeSelectedTranscriptionModel(persist: false);
            NormalizeSelectedLlmModel(persist: false);
            if (saved.SpeechModel != _selectedTranscriptionModelId || saved.TextModel != _selectedLlmModelId)
                CommitConfiguration(CaptureConfiguration(), notify: false);
        }
        else
        {
            _activeSecretName = ApiKeySecretName;
            _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
            _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(
                host.GetSetting<List<OpenRouterFetchedModel>>(FetchedTranscriptionModelsSettingName) ?? []);
            _selectedTranscriptionModelId = host.GetSetting<string>(SelectedTranscriptionModelSettingName);
            _fetchedModels = NormalizeFetchedModels(host.GetSetting<List<OpenRouterFetchedModel>>(FetchedModelsSettingName) ?? []);
            _selectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName);
            _hasUserSelectedLlmModel = host.GetSetting<bool?>(UserSelectedLlmModelSettingName) == true;
            _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingName));
            _temperatureValue = NormalizeTemperatureValue(host.GetSetting<double?>(TemperatureValueSettingName));
            NormalizeSelectedTranscriptionModel(persist: true);
            NormalizeSelectedLlmModel(persist: true);
        }
        _draftTextModels = _draftSpeechModels = null;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        _apiKey = null;
        return Task.CompletedTask;
    }


    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "openrouter";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "OpenRouter";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => IsAvailable;

    /// <summary>
    /// Gets whether the host is running the isolated UI automation fixture.
    /// </summary>
    internal bool IsUiAutomation => _host?.IsUiAutomation == true;

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels.Select(model => WithLanguages(new PluginModelInfo(model.Id, model.Name))).ToList()
            : FallbackTranscriptionModels.Select(WithLanguages).ToList();

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedTranscriptionModelId;
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => false;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        if (TranscriptionModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            throw new ArgumentException($"Unknown model: {modelId}");

        CommitConfiguration(CaptureConfiguration() with { SpeechModel = modelId });
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("OpenRouter STT does not support translation.");

        ct.ThrowIfCancellationRequested();
        if (!IsConfigured)
            throw new PluginRequestException("API key required.", PluginRequestFailureKind.Configuration);

        var modelId = _selectedTranscriptionModelId ?? TranscriptionModels.First().Id;
        return await SendAudioTranscriptionAsync(modelId, wavAudio, NormalizeLanguage(language), ct);
    }

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "OpenRouter";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedModels.Count > 0
            ? _fetchedModels.Select(model => new PluginModelInfo(model.Id, model.Name)
            {
                Publisher = model.Id.Split('/')[0],
                SizeDescription = model.FormattedPricing(L("Free", "Kostenlos")),
                IsRecommended = model.Id == DefaultLlmModelId
            }).ToList()
            : FallbackModels;

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAvailable)
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);

        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels.First().Id
            : model;

        return await SendChatCompletionAsync(modelId, systemPrompt, userText, ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal IReadOnlyList<OpenRouterFetchedModel> FetchedTranscriptionModels => _fetchedTranscriptionModels;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<OpenRouterFetchedModel> FetchedModels => _fetchedModels;
    internal string TemperatureMode => _temperatureMode;
    internal double TemperatureValue => _temperatureValue;

    /// <inheritdoc />
    public Task SetApiKeyAsync(string apiKey) => CommitWithKeyAsync(CaptureConfiguration(), apiKey, default);

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = SupportedModels.FirstOrDefault()?.Id ?? modelId;

        CommitConfiguration(CaptureConfiguration() with { TextModel = modelId, UserSelectedTextModel = true });
    }

    internal void SetFetchedModels(List<OpenRouterFetchedModel> models)
    {
        var normalized = NormalizeFetchedModels(models);
        var selected = normalized.Count == 0 || normalized.Any(m => m.Id == _selectedLlmModelId)
            ? _selectedLlmModelId : normalized[0].Id;
        CommitConfiguration(CaptureConfiguration() with { TextModels = normalized, TextModel = selected });
    }

    internal void SetFetchedTranscriptionModels(List<OpenRouterFetchedModel> models)
    {
        var normalized = NormalizeFetchedTranscriptionModels(models);
        var selected = normalized.Count == 0 || normalized.Any(m => m.Id == _selectedTranscriptionModelId)
            ? _selectedTranscriptionModelId : normalized[0].Id;
        CommitConfiguration(CaptureConfiguration() with { SpeechModels = normalized, SpeechModel = selected });
    }

    internal void SetTemperatureMode(string mode) =>
        CommitConfiguration(CaptureConfiguration() with { TemperatureMode = NormalizeTemperatureMode(mode) });

    internal void SetTemperatureValue(double value) =>
        CommitConfiguration(CaptureConfiguration() with { Temperature = NormalizeTemperatureValue(value) });

    internal async Task<List<OpenRouterFetchedModel>> FetchModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenRouterModelsResponse>(json, JsonOptions);

            var models = decoded?.Data?
                .Where(model => IsTextLlm(model.Architecture?.Modality, model.Id))
                .Select(model => new OpenRouterFetchedModel(
                    model.Id,
                    string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
                    model.Pricing?.Prompt ?? "0",
                    model.Pricing?.Completion ?? "0"))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList()
                ?? [];

            return NormalizeFetchedModels(models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    internal async Task<List<OpenRouterFetchedModel>> FetchTranscriptionModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v1/models?output_modalities=transcription");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenRouterModelsResponse>(json, JsonOptions);

            var models = decoded?.Data?
                .Select(model => new OpenRouterFetchedModel(
                    model.Id,
                    string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
                    model.Pricing?.Prompt ?? "0",
                    model.Pricing?.Completion ?? "0"))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList()
                ?? [];

            return NormalizeFetchedTranscriptionModels(models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    internal async Task<double?> FetchCreditsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            if (TryReadDouble(data, "limit_remaining", out var keyRemaining))
                return keyRemaining;

            if (TryReadDouble(data, "limit", out var limit)
                && TryReadDouble(data, "usage", out var usage))
            {
                return limit - usage;
            }

            return TryReadDouble(data, "limit_remaining", out var remaining)
                ? remaining
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool IsTextLlm(string? modality, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var lowered = id.ToLowerInvariant();
        string[] excluded =
        [
            "embed",
            "embedding",
            "tts",
            "audio",
            "image",
            "image-gen",
            "dall-e",
            "stable-diffusion",
            "midjourney",
            "whisper",
            "moderation",
        ];

        if (excluded.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal)))
            return false;

        if (string.IsNullOrWhiteSpace(modality)) return true;
        var parts = modality.Split("->", StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[1].Equals("text", StringComparison.OrdinalIgnoreCase)
            && parts[0].Split('+', StringSplitOptions.TrimEntries).Contains("text", StringComparer.OrdinalIgnoreCase);
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
                new { role = "user", content = userText }
            },
            ["max_tokens"] = LlmOutputTokenBudget.Calculate(systemPrompt, userText)
        };

        if (_temperatureMode == TemperatureModeCustom)
            body["temperature"] = _temperatureValue;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json);
    }

    private async Task<PluginTranscriptionResult> SendAudioTranscriptionAsync(
        string model,
        byte[] wavAudio,
        string? language,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input_audio"] = new Dictionary<string, string>
            {
                ["data"] = Convert.ToBase64String(wavAudio),
                ["format"] = "wav"
            }
        };

        if (SupportsVerboseTimestamps(model))
        {
            body["response_format"] = "verbose_json";
            body["timestamp_granularities"] = new[] { "segment" };
        }
        if (!string.IsNullOrWhiteSpace(language))
            body["language"] = language;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    private void NormalizeSelectedTranscriptionModel(bool persist)
    {
        var available = TranscriptionModels;
        if (available.Count == 0)
            return;

        if (_selectedTranscriptionModelId is not null
            && available.Any(model => string.Equals(model.Id, _selectedTranscriptionModelId, StringComparison.Ordinal)))
        {
            return;
        }

        _selectedTranscriptionModelId = available.First().Id;
        if (persist)
            _host?.SetSetting(SelectedTranscriptionModelSettingName, _selectedTranscriptionModelId);
    }

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        if (!_hasUserSelectedLlmModel
            || string.IsNullOrWhiteSpace(_selectedLlmModelId))
        {
            _selectedLlmModelId = available.First().Id;
            if (persist)
                _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
            return;
        }

        if (_fetchedModels.Count == 0)
            return;

        if (available.Any(model => string.Equals(model.Id, _selectedLlmModelId, StringComparison.Ordinal)))
        {
            return;
        }

        _selectedLlmModelId = available.First().Id;
        if (persist)
            _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
    }

    private static List<OpenRouterFetchedModel> NormalizeFetchedModels(IEnumerable<OpenRouterFetchedModel> models)
    {
        var normalized = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Where(model => !string.Equals(model.Id, DefaultLlmModelId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            return [];

        return [DefaultFetchedModel, .. normalized];
    }

    private static List<OpenRouterFetchedModel> NormalizeFetchedTranscriptionModels(IEnumerable<OpenRouterFetchedModel> models) =>
        models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    private static string NormalizeTemperatureMode(string? mode) =>
        string.Equals(mode, TemperatureModeCustom, StringComparison.OrdinalIgnoreCase)
            ? TemperatureModeCustom
            : TemperatureModeProviderDefault;

    private static double NormalizeTemperatureValue(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0.0, 2.0) : 0.3;

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
                return true;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(
                    property.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record OpenRouterModelsResponse(List<OpenRouterApiModel> Data);

    private sealed record OpenRouterApiModel(
        string Id,
        string Name,
        OpenRouterPricing? Pricing,
        OpenRouterArchitecture? Architecture);

    private sealed record OpenRouterPricing(string? Prompt, string? Completion);

    private sealed record OpenRouterArchitecture(string? Modality);
}

internal sealed record OpenRouterFetchedModel(
    string Id,
    string Name,
    string PromptPrice,
    string CompletionPrice)
{
    /// <summary>
    /// Performs formatted pricing.
    /// </summary>
    public string FormattedPricing(string freeLabel)
    {
        var promptPer1M = ParsePrice(PromptPrice) * 1_000_000;
        var completionPer1M = ParsePrice(CompletionPrice) * 1_000_000;

        if (Math.Abs(promptPer1M) < 1e-9 && Math.Abs(completionPer1M) < 1e-9)
            return freeLabel;

        return FormattableString.Invariant($"${promptPer1M:0.00}/${completionPer1M:0.00} per 1M");
    }

    private static double ParsePrice(string? value) =>
        double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var result)
            ? result
            : 0;
}
