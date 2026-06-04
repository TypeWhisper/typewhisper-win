using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>
/// Provides voxtral plugin behavior.
/// </summary>
public sealed class VoxtralPlugin : ITranscriptionEnginePlugin
{
    private const string BaseUrl = "https://api.mistral.ai";
    private const string ModelId = "mistral-whisper";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.voxtral";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Voxtral";
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
        var panel = new StackPanel { Margin = new Thickness(8) };

        var label = new TextBlock
        {
            Text = "Mistral API Key",
            Margin = new Thickness(0, 0, 0, 4),
        };

        var box = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey))
            box.Password = _apiKey;

        var status = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
        };

        var btn = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            var key = box.Password.Trim();
            _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
            if (_host is not null)
            {
                if (string.IsNullOrWhiteSpace(key))
                    await _host.DeleteSecretAsync("api-key");
                else
                    await _host.StoreSecretAsync("api-key", key);
            }
            status.Text = string.IsNullOrWhiteSpace(key) ? "" : "Saved";
            _host?.NotifyCapabilitiesChanged();
        };

        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "voxtral";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Voxtral";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new PluginModelInfo(ModelId, "Voxtral (Mistral Whisper)")];

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => true;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        if (modelId != ModelId)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. Mistral API key required.");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, BaseUrl, _apiKey!, ModelId,
            wavAudio, language, translate, "verbose_json", ct, prompt);
    }

    internal string? ApiKey => _apiKey;

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
    public void Dispose() => _httpClient.Dispose();
}
