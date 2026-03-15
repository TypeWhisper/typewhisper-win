using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gemini;

public sealed class GeminiPlugin : ILlmProviderPlugin
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    private const string DefaultModel = "gemini-2.5-flash";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.gemini";
    public string PluginName => "Google Gemini";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new GeminiSettingsView(this);

    // ILlmProviderPlugin

    public string ProviderName => "Google Gemini";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo(DefaultModel, "Gemini 2.5 Flash") { IsRecommended = true },
        new PluginModelInfo("gemini-2.5-pro", "Gemini 2.5 Pro"),
        new PluginModelInfo("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite"),
    ];

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
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
}
