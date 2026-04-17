using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Groq;

public sealed class GroqPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin
{
    private const string BaseUrl = "https://api.groq.com/openai";
    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedApiModelName;
    private string? _selectedLlmModelId;
    private List<FetchedLlmModel> _fetchedLlmModels = [];

    private static readonly IReadOnlyList<TranscriptionModelEntry> TranscriptionModelEntries =
    [
        new("whisper-large-v3", "Whisper Large V3", "whisper-large-v3", SupportsTranslation: true),
        new("whisper-large-v3-turbo", "Whisper Large V3 Turbo", "whisper-large-v3-turbo", SupportsTranslation: false),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new("llama-3.3-70b-versatile", "Llama 3.3 70B"),
        new("llama-3.1-8b-instant", "Llama 3.1 8B"),
        new("openai/gpt-oss-120b", "GPT-OSS 120B"),
        new("openai/gpt-oss-20b", "GPT-OSS 20B"),
        new("moonshotai/kimi-k2-instruct-0905", "Kimi K2"),
    ];

    public GroqPlugin()
        : this(CreateHttpClient())
    {
    }

    internal GroqPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.groq";
    public string PluginName => "Groq";
    public string PluginVersion => "1.0.2";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? TranscriptionModelEntries[0].Id;
        _selectedLlmModelId = host.GetSetting<string>("selectedLlmModel");
        _fetchedLlmModels = NormalizeFetchedLlmModels(host.GetSetting<List<FetchedLlmModel>>("fetchedLlmModels") ?? []);

        var selectedTranscription = TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId)
            ?? TranscriptionModelEntries[0];
        _selectedModelId = selectedTranscription.Id;
        _selectedApiModelName = selectedTranscription.ApiModelName;

        NormalizeSelectedLlmModel();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new GroqSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "groq";
    public string ProviderDisplayName => "Groq";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        TranscriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToList();

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation
    {
        get
        {
            if (!IsConfigured || _selectedModelId is null)
                return false;
            var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId);
            return entry?.SupportsTranslation ?? false;
        }
    }

    public void SelectModel(string modelId)
    {
        var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
        _selectedApiModelName = entry.ApiModelName;
        _host?.SetSetting("selectedModel", modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedApiModelName is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, BaseUrl, _apiKey!, _selectedApiModelName,
            wavAudio, language, translate, "verbose_json", ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "Groq";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList()
            : FallbackLlmModels;

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var modelId = ResolveLlmModelId(string.IsNullOrWhiteSpace(model) ? null : model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, modelId, systemPrompt, userText, ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<FetchedLlmModel> FetchedLlmModels => _fetchedLlmModels;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(_apiKey, normalizedApiKey, StringComparison.Ordinal);

        _apiKey = normalizedApiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);

            if (changed && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        _selectedLlmModelId = modelId;
        _host?.SetSetting("selectedLlmModel", modelId);
    }

    internal void SetFetchedLlmModels(List<FetchedLlmModel> models)
    {
        _fetchedLlmModels = NormalizeFetchedLlmModels(models);

        _host?.SetSetting("fetchedLlmModels", _fetchedLlmModels);
        NormalizeSelectedLlmModel();
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<FetchedLlmModel>?> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => new FetchedLlmModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null))
                .Where(m => IsLlmModel(m.Id))
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsLlmModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        var excluded = new[]
        {
            "whisper",
            "distil-whisper",
            "tool-use",
            "orpheus",
            "tts",
            "prompt-guard",
            "safeguard",
        };

        return !excluded.Any(lowered.Contains);
    }

    internal string ResolveLlmModelId(string? requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : _selectedLlmModelId ?? SupportedModels.First().Id;

    private void NormalizeSelectedLlmModel()
    {
        var availableIds = new HashSet<string>(SupportedModels.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        if (_selectedLlmModelId is not null && availableIds.Contains(_selectedLlmModelId))
            return;

        _selectedLlmModelId = SupportedModels.FirstOrDefault()?.Id;
        if (_selectedLlmModelId is not null)
            _host?.SetSetting("selectedLlmModel", _selectedLlmModelId);
    }

    private static List<FetchedLlmModel> NormalizeFetchedLlmModels(IEnumerable<FetchedLlmModel> models) =>
        models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && IsLlmModel(m.Id))
            .DistinctBy(m => m.Id)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record TranscriptionModelEntry(
        string Id, string DisplayName, string ApiModelName, bool SupportsTranslation);
}

internal sealed record FetchedLlmModel(string Id, string? OwnedBy);
