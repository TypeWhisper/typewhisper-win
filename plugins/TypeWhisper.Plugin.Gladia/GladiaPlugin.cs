using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gladia;

/// <summary>
/// Provides gladia plugin behavior.
/// </summary>
public sealed class GladiaPlugin : ITranscriptionEnginePlugin
{
    private const string BaseUrl = "https://api.gladia.io/v2";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("default", "Gladia (Auto)"),
    ];

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.gladia";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Gladia";
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
        var btn = new Button
        {
            Content = "Save",
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            var key = box.Password;
            _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
            if (_host is not null)
            {
                if (string.IsNullOrWhiteSpace(key))
                    await _host.DeleteSecretAsync("api-key");
                else
                    await _host.StoreSecretAsync("api-key", key);
            }
        };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        return new UserControl { Content = panel };
    }

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "gladia";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Gladia";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => false;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
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
            throw new InvalidOperationException("Plugin not configured. API key required.");

        // Gladia pre-recorded endpoint with multipart form data
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "audio", "audio.wav");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/pre-recorded");
        request.Headers.Add("x-gladia-key", _apiKey);
        request.Content = content;

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gladia API error {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transcript = root
            .GetProperty("result")
            .GetProperty("transcription")
            .GetProperty("full_transcript")
            .GetString() ?? "";

        double duration = 0;
        if (root.GetProperty("result").GetProperty("transcription")
            .TryGetProperty("duration", out var durEl))
            duration = durEl.GetDouble();

        string? detectedLanguage = null;
        if (root.GetProperty("result").GetProperty("transcription")
            .TryGetProperty("languages", out var langsEl)
            && langsEl.ValueKind == JsonValueKind.Array
            && langsEl.GetArrayLength() > 0)
        {
            detectedLanguage = langsEl[0].GetString();
        }

        return new PluginTranscriptionResult(transcript, detectedLanguage, duration, NoSpeechProbability: null);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
