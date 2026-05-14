using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed class OpenRouterPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin
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

    public OpenRouterPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
    {
    }

    internal OpenRouterPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openrouter";
    public string PluginName => "OpenRouter";
    public string PluginVersion => "1.1.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
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
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new OpenRouterSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "openrouter";
    public string ProviderDisplayName => "OpenRouter";
    public bool IsConfigured => IsAvailable;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels.Select(model => new PluginModelInfo(model.Id, model.Name)).ToList()
            : FallbackTranscriptionModels;

    public string? SelectedModelId => _selectedTranscriptionModelId;
    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (TranscriptionModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            throw new ArgumentException($"Unknown model: {modelId}");

        _selectedTranscriptionModelId = modelId;
        _host?.SetSetting(SelectedTranscriptionModelSettingName, modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("OpenRouter STT does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        var modelId = _selectedTranscriptionModelId ?? TranscriptionModels.First().Id;
        return await SendAudioTranscriptionAsync(modelId, wavAudio, NormalizeLanguage(language), ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "OpenRouter";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedModels.Count > 0
            ? _fetchedModels.Select(model => new PluginModelInfo(model.Id, model.Name)).ToList()
            : FallbackModels;

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("API key not configured");

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

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var wasAvailable = IsAvailable;
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

        _apiKey = normalized;
        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);

            if (changed && wasAvailable != IsAvailable)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/auth/key");
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

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
        _hasUserSelectedLlmModel = true;
        _host?.SetSetting(UserSelectedLlmModelSettingName, true);
    }

    internal void SetFetchedModels(List<OpenRouterFetchedModel> models)
    {
        _fetchedModels = NormalizeFetchedModels(models);
        _host?.SetSetting(FetchedModelsSettingName, _fetchedModels);
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetFetchedTranscriptionModels(List<OpenRouterFetchedModel> models)
    {
        _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(models);
        _host?.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
        NormalizeSelectedTranscriptionModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTemperatureMode(string mode)
    {
        _temperatureMode = NormalizeTemperatureMode(mode);
        _host?.SetSetting(TemperatureModeSettingName, _temperatureMode);
    }

    internal void SetTemperatureValue(double value)
    {
        _temperatureValue = NormalizeTemperatureValue(value);
        _host?.SetSetting(TemperatureValueSettingName, _temperatureValue);
    }

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

            var models = decoded?.Data
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

            var models = decoded?.Data
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

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/auth/key");
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

        return string.IsNullOrWhiteSpace(modality)
            || modality.EndsWith("->text", StringComparison.OrdinalIgnoreCase);
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
            ["max_tokens"] = 2048
        };

        if (_temperatureMode == TemperatureModeCustom)
            body["temperature"] = _temperatureValue;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
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

        if (!string.IsNullOrWhiteSpace(language))
            body["language"] = language;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    private static string ParseChatCompletionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            return content.GetString()?.Trim() ?? "";
        }

        return "";
    }

    private static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString()?.Trim() ?? ""
            : "";
        var language = root.TryGetProperty("language", out var languageElement)
            ? languageElement.GetString()
            : null;

        var duration = 0.0;
        if (root.TryGetProperty("duration", out var durationElement)
            && durationElement.TryGetDouble(out var rootDuration))
        {
            duration = rootDuration;
        }
        else if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("seconds", out var secondsElement)
            && secondsElement.TryGetDouble(out var usageSeconds))
        {
            duration = usageSeconds;
        }

        return new PluginTranscriptionResult(text, language, duration, null);
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
            || string.IsNullOrWhiteSpace(_selectedLlmModelId)
            || string.Equals(_selectedLlmModelId, LegacyFallbackDefaultLlmModelId, StringComparison.OrdinalIgnoreCase))
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
        Math.Clamp(value ?? 0.3, 0.0, 2.0);

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
