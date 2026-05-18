using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SmallestAi;

public sealed class SmallestAiPlugin : ITranscriptionEnginePlugin
{
    private const string BaseUrl = "https://api.smallest.ai";
    private const string PulseEndpoint = $"{BaseUrl}/waves/v1/pulse/get_text";
    private const string ApiKeySecretName = "api-key";
    private const string DefaultModelId = "pulse";

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new(DefaultModelId, "Pulse")
    ];

    private static readonly IReadOnlyList<string> Languages =
    [
        "ar", "bn", "de", "en", "es", "fr", "gu", "hi", "it", "ja",
        "ka", "ko", "ml", "mr", "nl", "or", "pa", "pt", "ru", "ta",
        "te", "yue", "zh", "multi-eu", "multi-indic", "multi-asian", "multi"
    ];

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _selectedModelId = DefaultModelId;

    public SmallestAiPlugin()
        : this(CreateHttpClient())
    {
    }

    internal SmallestAiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.smallest-ai";
    public string PluginName => "Smallest AI Pulse";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _selectedModelId = DefaultModelId;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new SmallestAiSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "smallest-ai";
    public string ProviderDisplayName => "Smallest AI";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public IReadOnlyList<string> SupportedLanguages => Languages;

    public void SelectModel(string modelId)
    {
        if (Models.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Smallest AI Pulse does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildPulseUri(language, includeWordTimestamps: true));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = CreateWavContent(wavAudio);

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Smallest AI Pulse API error {(int)response.StatusCode}: {ExtractApiError(json)}");
        }

        return ParseTranscriptionResponse(json, NormalizeLanguage(language));
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        return await SmallestAiStreamingSession.ConnectAsync(_apiKey!, NormalizeLanguage(language), ct);
    }

    // Settings support

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

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

        using var request = new HttpRequestMessage(HttpMethod.Post, PulseEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalized);
        request.Content = CreateWavContent([0]);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return true;

            var statusCode = (int)response.StatusCode;
            return statusCode is 400 or 415 or 422;
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

    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json, string? fallbackLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (IsApiError(root))
            throw new InvalidOperationException(ExtractApiError(root));

        var text = GetString(root, "transcription")?.Trim()
            ?? GetString(root, "text")?.Trim()
            ?? "";
        var language = GetString(root, "language")
            ?? GetFirstString(root, "languages")
            ?? fallbackLanguage;

        var duration = TryGetDouble(root, "duration", out var durationValue) ? durationValue : 0;
        var segments = ParseUtteranceSegments(root, ref duration);
        if (segments.Count == 0)
            segments = ParseWordSegments(root, ref duration);

        return new PluginTranscriptionResult(text, language, duration, NoSpeechProbability: null)
        {
            Segments = segments
        };
    }

    private static List<PluginTranscriptionSegment> ParseUtteranceSegments(JsonElement root, ref double duration)
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty("utterances", out var utterances)
            || utterances.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var utterance in utterances.EnumerateArray())
        {
            var text = GetString(utterance, "text")?.Trim();
            if (string.IsNullOrWhiteSpace(text)
                || !TryGetDouble(utterance, "start", out var start)
                || !TryGetDouble(utterance, "end", out var end))
            {
                continue;
            }

            segments.Add(new PluginTranscriptionSegment(text, start, end));
            duration = Math.Max(duration, end);
        }

        return segments;
    }

    private static List<PluginTranscriptionSegment> ParseWordSegments(JsonElement root, ref double duration)
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty("words", out var words)
            || words.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var word in words.EnumerateArray())
        {
            var text = (GetString(word, "word") ?? GetString(word, "text"))?.Trim();
            if (string.IsNullOrWhiteSpace(text)
                || !TryGetDouble(word, "start", out var start)
                || !TryGetDouble(word, "end", out var end))
            {
                continue;
            }

            segments.Add(new PluginTranscriptionSegment(text, start, end));
            duration = Math.Max(duration, end);
        }

        return segments;
    }

    private static Uri BuildPulseUri(string? language, bool includeWordTimestamps)
    {
        var query = new List<string>();
        var normalizedLanguage = NormalizeLanguage(language);
        if (normalizedLanguage is not null)
            query.Add($"language={Uri.EscapeDataString(normalizedLanguage)}");

        if (includeWordTimestamps)
            query.Add("word_timestamps=true");

        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return new Uri(PulseEndpoint + suffix);
    }

    private static ByteArrayContent CreateWavContent(byte[] wavAudio)
    {
        var content = new ByteArrayContent(wavAudio);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return content;
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    internal static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    private static bool IsApiError(JsonElement root)
    {
        if (GetString(root, "status") is { } status
            && status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (root.TryGetProperty("error", out var error)
            && error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            return true;
        }

        return false;
    }

    private static string ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractApiError(doc.RootElement);
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json;
        }
    }

    internal static string ExtractApiError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? "Unknown error";

            if (error.ValueKind == JsonValueKind.Object)
            {
                if (GetString(error, "message") is { } objectMessage)
                    return objectMessage;
                if (GetString(error, "detail") is { } detail)
                    return detail;
            }
        }

        return GetString(root, "message")
            ?? GetString(root, "detail")
            ?? "Unknown error";
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}
