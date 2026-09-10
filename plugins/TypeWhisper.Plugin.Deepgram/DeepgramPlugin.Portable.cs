using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
#if WINDOWS
using System.Windows.Controls;
#endif
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Deepgram;

/// <summary>
/// Provides deepgram plugin behavior.
/// </summary>
public sealed class DeepgramPlugin : ITranscriptionEnginePlugin, IApiKeyPlugin
{
    private const string BaseUrl = "https://api.deepgram.com";

    private readonly HttpClient _httpClient;

    /// <summary>Creates the provider transport.</summary>
    public DeepgramPlugin() : this(new HttpClient { Timeout = TimeSpan.FromMinutes(3) }) { }
    internal DeepgramPlugin(HttpClient httpClient) => _httpClient = httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    // https://developers.deepgram.com/docs/models-languages-overview (2026-09-10)
    private static readonly IReadOnlyList<string> Nova2Languages = Array.AsReadOnly(
        "multi bg ca zh zh-CN zh-Hans zh-TW zh-Hant zh-HK cs da da-DK nl en en-US en-AU en-GB en-NZ en-IN et fi nl-BE fr fr-CA de de-CH el hi hu id it ja ko ko-KR lv lt ms no pl pt pt-BR pt-PT ro ru sk es es-419 sv sv-SE th th-TH tr uk vi".Split(' '));
    private static readonly IReadOnlyList<string> Nova3Languages = Array.AsReadOnly(
        "multi af af-ZA ar ar-AE ar-SA ar-QA ar-KW ar-SY ar-LB ar-PS ar-JO ar-EG ar-SD ar-TD ar-MA ar-DZ ar-TN ar-IQ ar-IR hy as as-IN be bn bs bg ca zh-HK zh zh-CN zh-Hans zh-TW zh-Hant hr cs cs-CZ da da-DK nl en en-US en-AU en-GB en-IN en-NZ et fi nl-BE fr fr-CA ka ka-GE de de-CH el gu gu-IN he hi hu id it ja kn kk kk-KZ ko ko-KR lv lt mk ms mr mn ne no ps ps-AF fa pl pt pt-BR pt-PT pa pa-IN ro ru sr sk sl es es-419 sv sv-SE tl ta te th th-TH tr tr-TR uk ur vi".Split(' '));
    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("nova-3", "Nova-3") { LanguageCodes = Nova3Languages, LanguageCount = Nova3Languages.Count(code => code != "multi" && !code.Contains('-')) },
        new("nova-2", "Nova-2") { LanguageCodes = Nova2Languages, LanguageCount = Nova2Languages.Count(code => code != "multi" && !code.Contains('-')) },
    ];

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.deepgram";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Deepgram";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.1.3";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        var savedModel = host.GetSetting<string>("SelectedModelId");
        _selectedModelId = Models.Any(model => model.Id == savedModel) ? savedModel : Models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        _apiKey = null;
        _selectedModelId = null;
        return Task.CompletedTask;
    }

#if WINDOWS
    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new DeepgramSettingsView(this);
#endif

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "deepgram";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Deepgram";
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

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages =>
        _selectedModelId == "nova-2" ? Nova2Languages : Nova3Languages;

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => false;
    /// <summary>
    /// Gets whether the provider supports live streaming transcription.
    /// </summary>
    public bool SupportsStreaming => true;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");
        return await DeepgramStreamingSession.ConnectAsync(_apiKey!, _selectedModelId, language, ct);
    }

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        _host?.SetSetting("SelectedModelId", modelId);
        _selectedModelId = modelId;
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        if (translate) throw new NotSupportedException("Deepgram does not support native translation.");
        ct.ThrowIfCancellationRequested();
        var langParam = string.IsNullOrEmpty(language) || language == "auto"
            ? "&detect_language=true"
            : $"&language={Uri.EscapeDataString(language)}";
        var url = $"{BaseUrl}/v1/listen?model={_selectedModelId}&smart_format=true&punctuate=true{langParam}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiKey);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transcript = root
            .GetProperty("results")
            .GetProperty("channels")[0]
            .GetProperty("alternatives")[0]
            .GetProperty("transcript")
            .GetString() ?? "";

        var duration = root.GetProperty("metadata").GetProperty("duration").GetDouble();

        string? detectedLanguage = null;
        if (root.GetProperty("results").GetProperty("channels")[0].TryGetProperty("detected_language", out var langEl))
            detectedLanguage = langEl.GetString();

        return new PluginTranscriptionResult(transcript, detectedLanguage, duration, NoSpeechProbability: null);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

    /// <summary>Persists credentials before publishing the new configuration.</summary>
    public async Task SetApiKeyAsync(string apiKey)
    {
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        var value = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (value is null) await host.DeleteSecretAsync("api-key");
        else await host.StoreSecretAsync("api-key", value);
        _apiKey = value;
    }

    /// <summary>Checks credentials without uploading audio.</summary>
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new PluginRequestException("Configure an API key first.", PluginRequestFailureKind.Configuration);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiKey);
        using var response = await SendAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("Deepgram timed out.", PluginRequestFailureKind.Timeout); }
        catch (HttpRequestException)
        { throw new PluginRequestException("Could not reach Deepgram.", PluginRequestFailureKind.Network); }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        var kind = status switch
        {
            401 => PluginRequestFailureKind.Authentication,
            403 => PluginRequestFailureKind.Permission,
            429 => PluginRequestFailureKind.RateLimit,
            413 => PluginRequestFailureKind.RequestTooLarge,
            >= 500 => PluginRequestFailureKind.ServerError,
            _ => PluginRequestFailureKind.InvalidRequest
        };
        // Never include the provider response body, which can echo input or credentials.
        throw new PluginRequestException("Deepgram rejected the request.", kind, status);
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
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
