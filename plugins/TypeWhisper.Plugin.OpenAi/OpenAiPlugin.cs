using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

public sealed class OpenAiPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin
{
    private const string BaseUrl = "https://api.openai.com";
    private const string TranslationModel = "gpt-4o-mini";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedApiModelName;
    private string _selectedResponseFormat = "verbose_json";

    private static readonly IReadOnlyList<TranscriptionModelEntry> TranscriptionModelEntries =
    [
        new("whisper-1", "Whisper 1", "whisper-1", "verbose_json", SupportsTranslation: true),
        new("gpt-4o-transcribe", "GPT-4o Transcribe", "gpt-4o-transcribe", "json", SupportsTranslation: false),
        new("gpt-4o-mini-transcribe", "GPT-4o Mini Transcribe", "gpt-4o-mini-transcribe", "json", SupportsTranslation: false),
    ];

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openai";
    public string PluginName => "OpenAI";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new OpenAiSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "openai";
    public string ProviderDisplayName => "OpenAI";
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
        _selectedResponseFormat = entry.ResponseFormat;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedApiModelName is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, BaseUrl, _apiKey!, _selectedApiModelName,
            wavAudio, language, translate, _selectedResponseFormat, ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "OpenAI";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
        [new PluginModelInfo(TranslationModel, "GPT-4o Mini")];

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, model, systemPrompt, userText, ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record TranscriptionModelEntry(
        string Id, string DisplayName, string ApiModelName,
        string ResponseFormat, bool SupportsTranslation);
}
