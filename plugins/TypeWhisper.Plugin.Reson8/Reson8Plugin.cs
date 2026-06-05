using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// Provides reson8 plugin behavior.
/// </summary>
public sealed class Reson8Plugin : ITranscriptionEnginePlugin
{
    internal const string DefaultModelId = "__default__";
    internal const string DefaultBaseUrl = "https://api.reson8.dev";
    internal const string DefaultAuthHeader = "Authorization";

    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string CustomBaseUrlSettingName = "customBaseURL";
    private const string CustomAuthHeaderSettingName = "customAuthHeader";
    private const string FetchedCustomModelsSettingName = "fetchedCustomModels";

    private static readonly IReadOnlyList<string> Languages =
    [
        "nl", "en", "fr", "de", "it", "pl", "pt", "es", "sv"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _selectedModelId = DefaultModelId;
    private string _customBaseUrl = DefaultBaseUrl;
    private string _customAuthHeader = DefaultAuthHeader;
    private IReadOnlyList<Reson8CustomModel> _fetchedCustomModels = [];

    /// <summary>
    /// Initializes a new instance of the Reson8Plugin class.
    /// </summary>
    public Reson8Plugin()
        : this(CreateHttpClient())
    {
    }

    internal Reson8Plugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.reson8";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Reson8";
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
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _customBaseUrl = NormalizeBaseUrl(host.GetSetting<string>(CustomBaseUrlSettingName));
        _customAuthHeader = NormalizeAuthHeader(host.GetSetting<string>(CustomAuthHeaderSettingName));
        _fetchedCustomModels = host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName) ?? [];
        _selectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
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
    public UserControl? CreateSettingsView() => new Reson8SettingsView(this);

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "reson8";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Reson8";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        [new PluginModelInfo(DefaultModelId, "Default model"), .. _fetchedCustomModels.Select(m => new PluginModelInfo(m.Id, m.Name))];

    /// <summary>
    /// Gets the selected transcription model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => false;
    /// <summary>
    /// Gets whether the provider supports live streaming transcription.
    /// </summary>
    public bool SupportsStreaming => true;
    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => Languages;

    internal string? ApiKey => _apiKey;
    internal string CustomBaseUrl => _customBaseUrl;
    internal string CustomAuthHeader => _customAuthHeader;
    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels => _fetchedCustomModels;
    internal IPluginLocalization? Loc => _host?.Localization;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        _selectedModelId = normalized;
        _host?.SetSetting(SelectedModelSettingName, normalized);
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPrerecordedUri(_customBaseUrl, _selectedModelId, NormalizeLanguage(language)));
        AddAuthHeader(request, _apiKey!, _customAuthHeader);
        request.Content = new ByteArrayContent(pcm16);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            ThrowForApiError(response.StatusCode, json);

        return ParseTranscriptionResponse(json, NormalizeLanguage(language), pcm16.Length);
    }

    /// <summary>
    /// Transcribes streaming asynchronously.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        try
        {
            var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
            await using var session = await StartStreamingAsync(language, ct);
            var collector = new Reson8TranscriptCollector();
            var transcriptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            session.TranscriptReceived += evt =>
            {
                var text = collector.ApplyEvent(evt);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!onProgress(text))
                        transcriptReceived.TrySetCanceled(ct);
                }

                if (evt.IsFinal)
                    transcriptReceived.TrySetResult();
            };

            const int chunkSize = 8192;
            for (var offset = 0; offset < pcm16.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, pcm16.Length - offset);
                await session.SendAudioAsync(pcm16.AsMemory(offset, count), ct);
            }

            await session.FinalizeAsync(ct);

            var text = collector.FinalText;
            return string.IsNullOrWhiteSpace(text)
                ? await TranscribeAsync(wavAudio, language, translate, prompt, ct)
                : new PluginTranscriptionResult(text, NormalizeLanguage(language), PcmDurationSeconds(pcm16.Length), NoSpeechProbability: null);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return await TranscribeAsync(wavAudio, language, translate, prompt, ct);
        }
    }

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        return await Reson8StreamingSession.ConnectAsync(
            _apiKey!,
            _customBaseUrl,
            _customAuthHeader,
            _selectedModelId,
            NormalizeLanguage(language),
            ct);
    }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        IPluginHostServices? hostToNotify = null;

        await _apiKeyWriteLock.WaitAsync();
        try
        {
            var wasConfigured = IsConfigured;
            var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

            _apiKey = normalized;
            if (_host is not null)
            {
                if (normalized is null)
                    await _host.DeleteSecretAsync(ApiKeySecretName);
                else
                    await _host.StoreSecretAsync(ApiKeySecretName, normalized);

                if (changed && wasConfigured != IsConfigured)
                    hostToNotify = _host;
            }
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        hostToNotify?.NotifyCapabilitiesChanged();
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPrerecordedUri(_customBaseUrl, DefaultModelId, language: null));
        AddAuthHeader(request, normalized, _customAuthHeader);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.StatusCode != HttpStatusCode.Unauthorized;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal async Task<IReadOnlyList<Reson8CustomModel>> FetchCustomModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_customBaseUrl}/v1/custom-model");
        AddAuthHeader(request, _apiKey!, _customAuthHeader);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<Reson8CustomModel>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    internal void SetFetchedCustomModels(IReadOnlyList<Reson8CustomModel> models)
    {
        _fetchedCustomModels = models.ToArray();
        _host?.SetSetting(FetchedCustomModelsSettingName, _fetchedCustomModels);

        if (_selectedModelId != DefaultModelId && _fetchedCustomModels.All(m => m.Id != _selectedModelId))
        {
            _selectedModelId = DefaultModelId;
            _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
        }

        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetCustomBaseUrl(string? url)
    {
        _customBaseUrl = NormalizeBaseUrl(url);
        _host?.SetSetting(CustomBaseUrlSettingName, _customBaseUrl == DefaultBaseUrl ? null : _customBaseUrl);
    }

    internal void SetCustomAuthHeader(string? header)
    {
        _customAuthHeader = NormalizeAuthHeader(header);
        _host?.SetSetting(CustomAuthHeaderSettingName, _customAuthHeader == DefaultAuthHeader ? null : _customAuthHeader);
    }

    internal static Uri BuildPrerecordedUri(string baseUrl, string? modelId, string? language)
    {
        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1"
        };

        if (!string.IsNullOrWhiteSpace(language))
            query.Add($"language={Uri.EscapeDataString(language)}");

        if (!string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(modelId, DefaultModelId, StringComparison.Ordinal))
        {
            query.Add($"custom_model_id={Uri.EscapeDataString(modelId)}");
        }

        return new Uri($"{NormalizeBaseUrl(baseUrl)}/v1/speech-to-text/prerecorded?{string.Join("&", query)}");
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? fallbackLanguage,
        int pcm16ByteLength)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = GetString(root, "text")?.Trim() ?? "";
        var language = GetString(root, "language") ?? GetString(root, "detected_language") ?? fallbackLanguage;
        return new PluginTranscriptionResult(text, language, PcmDurationSeconds(pcm16ByteLength), NoSpeechProbability: null);
    }

    internal static string ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (GetString(root, "code") is { } code && GetString(root, "message") is { } codeMessage)
                return $"{code}: {codeMessage}";

            return GetString(root, "message")
                ?? GetString(root, "error")
                ?? GetString(root, "detail")
                ?? "Unknown error";
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json;
        }
    }

    internal static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    internal static void AddAuthHeader(HttpRequestMessage request, string apiKey, string authHeader)
    {
        var normalizedHeader = NormalizeAuthHeader(authHeader);
        if (string.Equals(normalizedHeader, DefaultAuthHeader, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
            return;
        }

        request.Headers.TryAddWithoutValidation(normalizedHeader, apiKey);
    }

    internal static string AuthHeaderValue(string apiKey, string authHeader) =>
        string.Equals(NormalizeAuthHeader(authHeader), DefaultAuthHeader, StringComparison.OrdinalIgnoreCase)
            ? $"ApiKey {apiKey}"
            : apiKey;

    private static string NormalizeModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string NormalizeBaseUrl(string? url)
    {
        var normalized = string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.Trim();
        while (normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];

        return string.IsNullOrWhiteSpace(normalized) ? DefaultBaseUrl : normalized;
    }

    private static string NormalizeAuthHeader(string? header) =>
        string.IsNullOrWhiteSpace(header) ? DefaultAuthHeader : header.Trim();

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void ThrowForApiError(HttpStatusCode statusCode, string json)
    {
        var message = ExtractApiError(json);
        switch (statusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new UnauthorizedAccessException("Invalid Reson8 API key.");
            case HttpStatusCode.NotFound:
                throw new KeyNotFoundException($"Reson8 custom model not found: {message}");
            case HttpStatusCode.RequestEntityTooLarge:
                throw new InvalidOperationException($"Reson8 file too large: {message}");
            case HttpStatusCode.TooManyRequests:
                throw new HttpRequestException($"Reson8 rate limit exceeded: {message}");
            case HttpStatusCode.InternalServerError:
                throw new HttpRequestException($"Reson8 server error: {message}");
            default:
                throw new HttpRequestException($"Reson8 API error {(int)statusCode}: {message}");
        }
    }

    private static double PcmDurationSeconds(int pcm16ByteLength) =>
        pcm16ByteLength <= 0 ? 0 : pcm16ByteLength / 2.0 / 16000.0;

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}

internal static class WavPcm16Extractor
{
    /// <summary>
    /// Performs extract pcm16.
    /// </summary>
    public static byte[] ExtractPcm16(byte[] wavAudio)
    {
        if (wavAudio.Length < 44
            || !HasAscii(wavAudio, 0, "RIFF")
            || !HasAscii(wavAudio, 8, "WAVE"))
        {
            return wavAudio;
        }

        var offset = 12;
        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (offset + 8 <= wavAudio.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BitConverter.ToInt32(wavAudio, offset + 4);
            offset += 8;
            if (chunkSize < 0 || offset + chunkSize > wavAudio.Length)
                break;

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToInt16(wavAudio, offset);
                channels = BitConverter.ToInt16(wavAudio, offset + 2);
                sampleRate = BitConverter.ToInt32(wavAudio, offset + 4);
                bitsPerSample = BitConverter.ToInt16(wavAudio, offset + 14);
            }
            else if (chunkId == "data")
            {
                data = wavAudio.Skip(offset).Take(chunkSize).ToArray();
            }

            offset += chunkSize + (chunkSize % 2);
        }

        if (data is null)
            return wavAudio;

        if (audioFormat == 1 && channels == 1 && sampleRate == 16000 && bitsPerSample == 16)
            return data;

        return data;
    }

    private static bool HasAscii(byte[] bytes, int offset, string value)
    {
        if (offset + value.Length > bytes.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (bytes[offset + i] != value[i])
                return false;
        }

        return true;
    }
}
