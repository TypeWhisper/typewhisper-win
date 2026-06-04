using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cerebras;

/// <summary>
/// Provides cerebras plugin behavior.
/// </summary>
public sealed class CerebrasPlugin : ILlmProviderPlugin
{
    private const string BaseUrl = "https://api.cerebras.ai";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.cerebras";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Cerebras";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
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
    public UserControl? CreateSettingsView() => new CerebrasSettingsView(this);

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "Cerebras";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("llama-4-scout-17b-16e-instruct", "Llama 4 Scout 17B"),
    ];

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("API key not configured");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, model, systemPrompt, userText, ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

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

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
