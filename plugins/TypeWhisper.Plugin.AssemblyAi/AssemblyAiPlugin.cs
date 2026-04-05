using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed class AssemblyAiPlugin : ITranscriptionEnginePlugin
{
    private const string BaseUrl = "https://api.assemblyai.com";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("universal-3-pro", "Universal-3 Pro"),
        new("universal-2", "Universal-2"),
    ];

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.assemblyai";
    public string PluginName => "AssemblyAI";
    public string PluginVersion => "1.1.0";

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

    public UserControl? CreateSettingsView() => new AssemblyAiSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "assemblyai";
    public string ProviderDisplayName => "AssemblyAI";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");
        return await AssemblyAiStreamingSession.ConnectAsync(_apiKey!, language, ct);
    }

    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        // Step 1: Upload audio
        var uploadUrl = await UploadAudioAsync(wavAudio, ct);

        // Step 2: Submit transcription job
        var transcriptId = await SubmitTranscriptionAsync(uploadUrl, language, ct);

        // Step 3: Poll until completed
        return await PollForResultAsync(transcriptId, ct);
    }

    private async Task<string> UploadAudioAsync(byte[] wavAudio, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/upload");
        request.Headers.Add("Authorization", _apiKey);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AssemblyAI upload error {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("upload_url").GetString()
            ?? throw new InvalidOperationException("Missing upload_url in response");
    }

    private async Task<string> SubmitTranscriptionAsync(string audioUrl, string? language, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["audio_url"] = audioUrl,
            ["speech_models"] = new[] { _selectedModelId! },
        };

        if (string.IsNullOrEmpty(language) || language == "auto")
            body["language_detection"] = true;
        else
            body["language_code"] = language;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/transcript");
        request.Headers.Add("Authorization", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AssemblyAI submit error {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Missing id in response");
    }

    private async Task<PluginTranscriptionResult> PollForResultAsync(string transcriptId, CancellationToken ct)
    {
        for (var i = 0; i < 300; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v2/transcript/{transcriptId}");
            request.Headers.Add("Authorization", _apiKey);

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"AssemblyAI poll error {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            if (status == "error")
            {
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : "Unknown error";
                throw new InvalidOperationException($"AssemblyAI transcription failed: {error}");
            }

            if (status == "completed")
            {
                var text = root.GetProperty("text").GetString() ?? "";
                var duration = root.TryGetProperty("audio_duration", out var durEl) ? durEl.GetDouble() : 0.0;
                string? detectedLanguage = root.TryGetProperty("language_code", out var langEl) ? langEl.GetString() : null;
                return new PluginTranscriptionResult(text, detectedLanguage, duration);
            }
        }

        throw new TimeoutException("AssemblyAI transcription timed out after 5 minutes");
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v2/transcript?limit=1");
        request.Headers.Add("Authorization", apiKey);
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
