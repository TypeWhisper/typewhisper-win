using System.Net.Http;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cohere;

/// <summary>
/// Provides cohere plugin behavior.
/// </summary>
public sealed class CoherePlugin : ILlmProviderPlugin, IDisposable
{
    private const string BaseUrl = "https://api.cohere.com/compatibility";
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.cohere";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Cohere";
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
        _apiKey = await host.LoadSecretAsync("apiKey");
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

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "Cohere";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("command-a-03-2025", "Command A") { IsRecommended = true },
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

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new System.Windows.Thickness(8) };
        var label = new TextBlock { Text = "API Key", Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var box = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey)) box.Password = _apiKey;
        var btn = new Button
        {
            Content = "Save",
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            _apiKey = box.Password;
            if (_host is not null) await _host.StoreSecretAsync("apiKey", _apiKey);
        };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        return new UserControl { Content = panel };
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();
}
