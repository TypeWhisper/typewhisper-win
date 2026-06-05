using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Claude;

/// <summary>
/// Provides claude plugin behavior.
/// </summary>
public sealed class ClaudePlugin : ILlmProviderPlugin
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.claude";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Claude";
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
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
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
    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new System.Windows.Thickness(8) };
        var label = new TextBlock { Text = "API Key", Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var box = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey)) box.Password = _apiKey;
        var btn = new Button { Content = "Save", Margin = new System.Windows.Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        btn.Click += async (_, _) =>
        {
            _apiKey = box.Password;
            if (_host is not null) await _host.StoreSecretAsync("api-key", _apiKey);
        };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        return new UserControl { Content = panel };
    }

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "Claude";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => IsConfigured;

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("claude-sonnet-4-20250514", "Claude Sonnet 4"),
        new PluginModelInfo("claude-haiku-4-5-20251001", "Claude Haiku 4.5"),
    ];

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var requestBody = new
        {
            model,
            max_tokens = 2048,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userText }
            }
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _host?.Log(PluginLogLevel.Error, $"Anthropic API error {response.StatusCode}: {responseBody}");
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement.GetProperty("content");
        if (content.GetArrayLength() == 0)
            throw new InvalidOperationException("Anthropic API returned empty content array");

        return content[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Anthropic API returned null text");
    }

    // Internal helpers for settings view

    internal bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
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

    internal bool ValidateApiKeyFormat(string apiKey)
    {
        return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("sk-ant-");
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
