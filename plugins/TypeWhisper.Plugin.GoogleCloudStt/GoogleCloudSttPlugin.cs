using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GoogleCloudStt;

/// <summary>
/// Provides google cloud stt plugin behavior.
/// </summary>
public sealed class GoogleCloudSttPlugin : ITranscriptionEnginePlugin
{
    private const string ApiEndpoint = "https://speech.googleapis.com/v1/speech:recognize";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.google-cloud-stt";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Google Cloud STT";
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
            Text = "Google Cloud API Key",
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
    public string ProviderId => "google-cloud-stt";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Google Cloud";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new PluginModelInfo("latest_long", "Google Cloud (Long)")];

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
        if (modelId != "latest_long")
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

        // Strip WAV header (first 44 bytes) to get raw PCM16 data
        var pcmData = wavAudio.Length > 44 ? wavAudio[44..] : wavAudio;
        var audioBase64 = Convert.ToBase64String(pcmData);

        var langCode = !string.IsNullOrEmpty(language) && language != "auto" ? language : "en-US";
        // Google expects BCP-47 codes; convert short ISO codes (e.g. "en" -> "en-US", "de" -> "de-DE")
        if (langCode.Length == 2)
            langCode = MapToGoogleLanguageCode(langCode);

        var requestBody = new
        {
            config = new
            {
                encoding = "LINEAR16",
                sampleRateHertz = 16000,
                languageCode = langCode,
                model = "latest_long",
            },
            audio = new
            {
                content = audioBase64,
            },
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}?key={_apiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(responseJson, langCode);
    }

    private static PluginTranscriptionResult ParseResponse(string json, string requestedLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("alternatives", out var alternatives)
                    && alternatives.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alt in alternatives.EnumerateArray())
                    {
                        if (alt.TryGetProperty("transcript", out var transcript))
                        {
                            if (sb.Length > 0)
                                sb.Append(' ');
                            sb.Append(transcript.GetString());
                        }
                    }
                }
            }
        }

        // Google STT v1 does not return audio duration in the response;
        // extract from totalBilledTime if available, otherwise default to 0.
        double duration = 0;
        if (root.TryGetProperty("totalBilledTime", out var billedTime))
        {
            var billedStr = billedTime.GetString() ?? "";
            // Format: "15s" or "15.500s"
            if (billedStr.EndsWith("s") &&
                double.TryParse(billedStr[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var secs))
            {
                duration = secs;
            }
        }

        // Extract language from the first result's languageCode if present
        string? detectedLang = null;
        if (root.TryGetProperty("results", out var resultsForLang)
            && resultsForLang.ValueKind == JsonValueKind.Array
            && resultsForLang.GetArrayLength() > 0)
        {
            var first = resultsForLang[0];
            if (first.TryGetProperty("languageCode", out var lc))
                detectedLang = lc.GetString();
        }

        return new PluginTranscriptionResult(
            sb.ToString().Trim(),
            detectedLang ?? requestedLanguage,
            duration);
    }

    private static string MapToGoogleLanguageCode(string iso) => iso.ToLowerInvariant() switch
    {
        "en" => "en-US",
        "de" => "de-DE",
        "fr" => "fr-FR",
        "es" => "es-ES",
        "it" => "it-IT",
        "pt" => "pt-BR",
        "ja" => "ja-JP",
        "ko" => "ko-KR",
        "zh" => "zh-CN",
        "ru" => "ru-RU",
        "nl" => "nl-NL",
        "pl" => "pl-PL",
        "sv" => "sv-SE",
        "da" => "da-DK",
        "fi" => "fi-FI",
        "no" => "nb-NO",
        "tr" => "tr-TR",
        "ar" => "ar-SA",
        "hi" => "hi-IN",
        "uk" => "uk-UA",
        "cs" => "cs-CZ",
        _ => iso,
    };

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();
}
