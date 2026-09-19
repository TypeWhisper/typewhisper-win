using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// Provides reson8 plugin behavior.
/// </summary>
public sealed partial class Reson8Plugin : ITranscriptionEnginePlugin
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
        "nl", "en", "fr", "de", "it", "pl", "pt", "es", "sv", "fy"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string?, CancellationToken, Task<IStreamingSession>>? _streamingFactory;
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

    internal Reson8Plugin(HttpClient httpClient, Func<string?, CancellationToken, Task<IStreamingSession>>? streamingFactory = null)
    {
        _httpClient = httpClient;
        _streamingFactory = streamingFactory;
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
    public string PluginVersion => "1.2.7";

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
    /// <inheritdoc />
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
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsStreaming => true;
    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => Languages;

    internal string? ApiKey => _apiKey;
    internal string CustomBaseUrl => _customBaseUrl;
    internal string CustomAuthHeader => _customAuthHeader;
    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels => _fetchedCustomModels;
    internal IPluginLocalization? Loc => PortableLocalization.TryGet(_host);

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        _host?.SetSetting(SelectedModelSettingName, normalized);
        _selectedModelId = normalized;
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

        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamToken = progressCancellation.Token;
        try
        {
            var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
            await using var session = await StartStreamingAsync(language, streamToken);
            var collector = new Reson8TranscriptCollector();

            session.TranscriptReceived += evt =>
            {
                var text = collector.ApplyEvent(evt);
                if (!streamToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(text) && !onProgress(text))
                    progressCancellation.Cancel();
            };

            const int chunkSize = 8192;
            for (var offset = 0; offset < pcm16.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, pcm16.Length - offset);
                streamToken.ThrowIfCancellationRequested();
                await session.SendAudioAsync(pcm16.AsMemory(offset, count), streamToken);
            }

            streamToken.ThrowIfCancellationRequested();
            await session.FinalizeAsync(streamToken);
            streamToken.ThrowIfCancellationRequested();

            var text = collector.FinalText;
            return string.IsNullOrWhiteSpace(text)
                ? await TranscribeAsync(wavAudio, language, translate, prompt, streamToken)
                : new PluginTranscriptionResult(text, NormalizeLanguage(language), PcmDurationSeconds(pcm16.Length), NoSpeechProbability: null);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            streamToken.ThrowIfCancellationRequested();
            return await TranscribeAsync(wavAudio, language, translate, prompt, streamToken);
        }
    }

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");
        if (_streamingFactory is not null) return await _streamingFactory(language, ct);

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

        await _apiKeyWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

            if (_host is not null)
            {
                var oldSelection = _host.GetSetting<string>(SelectedModelSettingName);
                var oldModels = _host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName);
                var selectionWritten = false;
                var modelsWritten = false;
                try
                {
                    if (changed)
                    {
                        // Write dependent settings first; restore them if a later
                        // write fails so the old credential keeps its model state.
                        _host.SetSetting(SelectedModelSettingName, DefaultModelId);
                        selectionWritten = true;
                        _host.SetSetting(FetchedCustomModelsSettingName, Array.Empty<Reson8CustomModel>());
                        modelsWritten = true;
                    }
                    if (normalized is null)
                        await _host.DeleteSecretAsync(ApiKeySecretName).ConfigureAwait(false);
                    else
                        await _host.StoreSecretAsync(ApiKeySecretName, normalized).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    var errors = new List<Exception> { failure };
                    try { if (modelsWritten) _host.SetSetting(FetchedCustomModelsSettingName, oldModels); }
                    catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                    try { if (selectionWritten) _host.SetSetting(SelectedModelSettingName, oldSelection); }
                    catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                    if (errors.Count > 1) throw new AggregateException("Credential update failed and model settings could not be fully restored.", errors);
                    throw;
                }
            }
            _apiKey = normalized;
            if (changed)
            {
                _fetchedCustomModels = [];
                _selectedModelId = DefaultModelId;
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
            HttpMethod.Get, $"{_customBaseUrl}/v1/custom-model");
        AddAuthHeader(request, normalized, _customAuthHeader);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
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

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<Reson8CustomModel>>(json, JsonOptions)
            ?? throw new JsonException("The custom model catalog was empty or invalid.");
    }

    internal void SetFetchedCustomModels(IReadOnlyList<Reson8CustomModel> models)
    {
        var nextModels = models.ToArray();
        var nextSelection = _selectedModelId != DefaultModelId && nextModels.All(m => m.Id != _selectedModelId)
            ? DefaultModelId : _selectedModelId;
        if (_host is { } host)
        {
            var previousSelection = host.GetSetting<string>(SelectedModelSettingName);
            host.SetSetting(SelectedModelSettingName, nextSelection);
            try { host.SetSetting(FetchedCustomModelsSettingName, nextModels); }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                try { host.SetSetting(SelectedModelSettingName, previousSelection); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException)
                { throw new AggregateException("Model refresh failed and its selection could not be restored.", failure, rollback); }
                throw;
            }
        }
        _selectedModelId = nextSelection;
        _fetchedCustomModels = nextModels;
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetCustomBaseUrl(string? url)
    {
        var normalized = NormalizeBaseUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || string.IsNullOrEmpty(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Enter an absolute HTTP(S) server URL without credentials, query or fragment.", nameof(url));
        if (string.Equals(normalized, _customBaseUrl, StringComparison.Ordinal)) return;
        if (_host is { } host)
        {
            var oldSelection = host.GetSetting<string>(SelectedModelSettingName);
            var oldModels = host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName);
            var selectionWritten = false;
            var modelsWritten = false;
            try
            {
                host.SetSetting(SelectedModelSettingName, DefaultModelId);
                selectionWritten = true;
                host.SetSetting(FetchedCustomModelsSettingName, Array.Empty<Reson8CustomModel>());
                modelsWritten = true;
                // Commit the endpoint only after all dependent settings succeed.
                host.SetSetting(CustomBaseUrlSettingName, normalized == DefaultBaseUrl ? null : normalized);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                var errors = new List<Exception> { failure };
                try { if (modelsWritten) host.SetSetting(FetchedCustomModelsSettingName, oldModels); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                try { if (selectionWritten) host.SetSetting(SelectedModelSettingName, oldSelection); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                if (errors.Count > 1) throw new AggregateException("Server update failed and model settings could not be fully restored.", errors);
                throw;
            }
        }
        _customBaseUrl = normalized;
        _selectedModelId = DefaultModelId;
        _fetchedCustomModels = [];
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetCustomAuthHeader(string? header)
    {
        var normalized = NormalizeAuthHeader(header);
        if (normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
            throw new ArgumentException("Enter a valid HTTP header name.", nameof(header));
        _host?.SetSetting(CustomAuthHeaderSettingName, normalized == DefaultAuthHeader ? null : normalized);
        _customAuthHeader = normalized;
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

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

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
            throw new ArgumentException("Audio must be a PCM16 mono 16 kHz WAV file.", nameof(wavAudio));
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
            if (chunkSize < 0 || (long)offset + chunkSize > wavAudio.Length)
                throw new ArgumentException("The WAV contains a truncated or invalid chunk.", nameof(wavAudio));

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
            throw new ArgumentException("The WAV contains no audio data.", nameof(wavAudio));

        if (audioFormat == 1 && channels == 1 && sampleRate == 16000 && bitsPerSample == 16 && data.Length % 2 == 0)
            return data;

        throw new ArgumentException("Audio must be PCM16 mono at 16 kHz.", nameof(wavAudio));
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
