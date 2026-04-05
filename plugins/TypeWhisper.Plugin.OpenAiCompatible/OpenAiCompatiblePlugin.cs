using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed class OpenAiCompatiblePlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _baseUrl;
    private string? _selectedModelId;
    private string? _selectedLlmModelId;
    private List<FetchedModel> _fetchedModels = [];

    // ITypeWhisperPlugin
    public string PluginId => "com.typewhisper.openai-compatible";
    public string PluginName => "OpenAI Compatible";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _baseUrl = host.GetSetting<string>("baseUrl");
        _selectedModelId = host.GetSetting<string>("selectedModel");
        _selectedLlmModelId = host.GetSetting<string>("selectedLlmModel");

        var modelsJson = host.GetSetting<string>("fetchedModels");
        if (!string.IsNullOrEmpty(modelsJson))
        {
            try { _fetchedModels = JsonSerializer.Deserialize<List<FetchedModel>>(modelsJson) ?? []; }
            catch { _fetchedModels = []; }
        }

        host.Log(PluginLogLevel.Info, $"Activated (baseUrl={_baseUrl}, configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new OpenAiCompatibleSettingsView(this);

    // ITranscriptionEnginePlugin
    public string ProviderId => "openai-compatible";
    public string ProviderDisplayName => "Custom Server";

    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var models = _fetchedModels
                .Select(m => new PluginModelInfo(m.Id, m.Id))
                .ToList();

            if (models.Count == 0 && !string.IsNullOrEmpty(_selectedModelId))
                return [new PluginModelInfo(_selectedModelId, _selectedModelId)];

            return models;
        }
    }

    public string? SelectedModelId => _selectedModelId;

    public void SelectModel(string modelId)
    {
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public bool SupportsTranslation => true;

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_baseUrl))
            throw new InvalidOperationException("Server-URL nicht konfiguriert");
        if (string.IsNullOrEmpty(_selectedModelId))
            throw new InvalidOperationException("Kein Transkriptions-Modell ausgewählt");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, _baseUrl!, _apiKey ?? "", _selectedModelId!,
            wavAudio, language, translate, "verbose_json", ct);
    }

    // ILlmProviderPlugin
    public string ProviderName => "OpenAI Compatible";

    public bool IsAvailable => IsConfigured && !string.IsNullOrEmpty(_selectedLlmModelId);

    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            var models = _fetchedModels
                .Select(m => new PluginModelInfo(m.Id, m.Id))
                .ToList();

            if (models.Count == 0 && !string.IsNullOrEmpty(_selectedLlmModelId))
                return [new PluginModelInfo(_selectedLlmModelId, _selectedLlmModelId)];

            return models;
        }
    }

    public async Task<string> ProcessAsync(
        string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_baseUrl))
            throw new InvalidOperationException("Server-URL nicht konfiguriert");

        var modelId = !string.IsNullOrEmpty(model) ? model : _selectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("Kein LLM-Modell ausgewählt");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, _baseUrl!, _apiKey ?? "", modelId,
            systemPrompt, userText, ct);
    }

    // Internal methods for settings view
    internal string? BaseUrl => _baseUrl;
    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? SelectedTranscriptionModelId => _selectedModelId;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<FetchedModel> FetchedModels => _fetchedModels;

    internal void SetBaseUrl(string url)
    {
        var normalized = url.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];
        _baseUrl = normalized;
        _host?.SetSetting("baseUrl", normalized);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task SetApiKeyAsync(string key)
    {
        _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(key))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", key);
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        _selectedLlmModelId = modelId;
        _host?.SetSetting("selectedLlmModel", modelId);
    }

    internal void SetFetchedModels(List<FetchedModel> models)
    {
        _fetchedModels = models;
        try
        {
            var json = JsonSerializer.Serialize(models);
            _host?.SetSetting("fetchedModels", json);
        }
        catch { /* best effort */ }
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<FetchedModel>> FetchModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_baseUrl))
            return [];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => new FetchedModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null))
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .OrderBy(m => m.Id)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return [];
        }
    }

    internal async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_baseUrl))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record FetchedModel(string Id, string? OwnedBy);
