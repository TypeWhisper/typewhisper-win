using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Speechmatics;

/// <summary>
/// Provides speechmatics plugin behavior.
/// </summary>
public sealed class SpeechmaticsPlugin : ITranscriptionEnginePlugin
{
    private const string BaseUrl = "https://asr.api.speechmatics.com/v2";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("enhanced", "Speechmatics Enhanced"),
    ];

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.speechmatics";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Speechmatics";
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
    public string ProviderId => "speechmatics";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Speechmatics";
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

        var lang = string.IsNullOrEmpty(language) || language == "auto" ? "en" : language;

        // Step 1: Submit batch transcription job
        var config = JsonSerializer.Serialize(new
        {
            type = "transcription",
            transcription_config = new
            {
                language = lang,
                operating_point = "enhanced",
            }
        });

        using var submitContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        submitContent.Add(fileContent, "data_file", "audio.wav");
        submitContent.Add(new StringContent(config, Encoding.UTF8, "application/json"), "config");

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/jobs");
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        submitRequest.Content = submitContent;

        var submitResponse = await _httpClient.SendAsync(submitRequest, ct);
        var submitJson = await submitResponse.Content.ReadAsStringAsync(ct);

        if (!submitResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"Speechmatics API error {(int)submitResponse.StatusCode}: {submitJson}");

        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId = submitDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("No job ID in Speechmatics response");

        // Step 2: Poll for completion
        var transcript = await PollForTranscriptAsync(jobId, ct);
        return transcript;
    }

    private async Task<PluginTranscriptionResult> PollForTranscriptAsync(string jobId, CancellationToken ct)
    {
        const int maxAttempts = 120;
        const int delayMs = 2000;

        for (var i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delayMs, ct);

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/jobs/{jobId}");
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var statusResponse = await _httpClient.SendAsync(statusRequest, ct);
            var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);

            if (!statusResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"Speechmatics status error {(int)statusResponse.StatusCode}: {statusJson}");

            using var statusDoc = JsonDocument.Parse(statusJson);
            var job = statusDoc.RootElement.GetProperty("job");
            var status = job.GetProperty("status").GetString();

            if (status == "done")
            {
                // Fetch transcript
                using var transcriptRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl}/jobs/{jobId}/transcript?format=json-v2");
                transcriptRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                var transcriptResponse = await _httpClient.SendAsync(transcriptRequest, ct);
                var transcriptJson = await transcriptResponse.Content.ReadAsStringAsync(ct);

                if (!transcriptResponse.IsSuccessStatusCode)
                    throw new HttpRequestException($"Speechmatics transcript error {(int)transcriptResponse.StatusCode}: {transcriptJson}");

                return ParseTranscript(transcriptJson, job);
            }

            if (status == "rejected" || status == "deleted")
                throw new InvalidOperationException($"Speechmatics job {jobId} {status}");
        }

        throw new TimeoutException($"Speechmatics job {jobId} did not complete within {maxAttempts * delayMs / 1000}s");
    }

    private static PluginTranscriptionResult ParseTranscript(string json, JsonElement job)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("alternatives", out var alts)
                    && alts.ValueKind == JsonValueKind.Array
                    && alts.GetArrayLength() > 0)
                {
                    var content = alts[0].GetProperty("content").GetString();
                    if (!string.IsNullOrEmpty(content))
                        sb.Append(content);
                }
            }
        }

        double duration = 0;
        if (job.TryGetProperty("duration", out var durEl))
            duration = durEl.GetDouble();

        string? detectedLanguage = null;
        if (root.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("language", out var langEl))
        {
            detectedLanguage = langEl.GetString();
        }

        return new PluginTranscriptionResult(sb.ToString().Trim(), detectedLanguage, duration, NoSpeechProbability: null);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
