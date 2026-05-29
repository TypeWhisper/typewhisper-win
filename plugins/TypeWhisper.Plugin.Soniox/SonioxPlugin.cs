using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Soniox;

public sealed class SonioxPlugin : ITranscriptionEnginePlugin
{
    internal const string DefaultModelId = "default";

    private const string BaseUrl = "https://api.soniox.com";
    private const string ApiKeySecretName = "api-key";
    private const string SonioxAsyncModelId = "stt-async-v4";
    private const int DefaultMaxPollAttempts = 3600;

    private static readonly TimeSpan DefaultPollDelay = TimeSpan.FromSeconds(1);

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new(DefaultModelId, "Soniox Async")
        {
            IsRecommended = true
        },
    ];

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _pollDelay;
    private readonly int _maxPollAttempts;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);

    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _selectedModelId = DefaultModelId;

    public SonioxPlugin()
        : this(CreateHttpClient())
    {
    }

    internal SonioxPlugin(
        HttpClient httpClient,
        TimeSpan? pollDelay = null,
        int maxPollAttempts = DefaultMaxPollAttempts)
    {
        if (maxPollAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPollAttempts), "Poll attempts must be positive.");

        _httpClient = httpClient;
        _pollDelay = pollDelay ?? DefaultPollDelay;
        _maxPollAttempts = maxPollAttempts;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.soniox";
    public string PluginName => "Soniox";
    public string PluginVersion => "1.0.1";

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

    public UserControl? CreateSettingsView() => new SonioxSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "soniox";
    public string ProviderDisplayName => "Soniox";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (!string.Equals(modelId, DefaultModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown model: {modelId}");

        _selectedModelId = DefaultModelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Soniox does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        string? fileId = null;
        string? transcriptionId = null;

        try
        {
            fileId = await UploadFileAsync(wavAudio, ct);
            transcriptionId = await CreateTranscriptionAsync(fileId, language, ct);
            var completedDetails = await WaitUntilCompletedAsync(transcriptionId, ct);
            var transcriptJson = await FetchTranscriptAsync(transcriptionId, ct);
            return ParseTranscript(transcriptJson, completedDetails, NormalizeLanguage(language));
        }
        finally
        {
            await CleanupAsync(transcriptionId, fileId);
        }
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

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        AddAuthorization(request, normalized);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> UploadFileAsync(byte[] wavAudio, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/files");
        AddAuthorization(request);
        request.Content = form;

        var json = await SendJsonAsync(request, "Soniox file upload", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox file upload response did not include a file id.");
    }

    private async Task<string> CreateTranscriptionAsync(string fileId, string? language, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = SonioxAsyncModelId,
            ["file_id"] = fileId,
        };

        if (NormalizeLanguage(language) is { } normalizedLanguage)
            payload["language_hints"] = new[] { normalizedLanguage };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/transcriptions");
        AddAuthorization(request);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var json = await SendJsonAsync(request, "Soniox transcription creation", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox transcription response did not include a transcription id.");
    }

    private async Task<JsonElement> WaitUntilCompletedAsync(string transcriptionId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _maxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/transcriptions/{transcriptionId}");
            AddAuthorization(request);

            var json = await SendJsonAsync(request, "Soniox transcription status", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = GetString(root, "status");

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return root.Clone();

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Soniox transcription failed: {ExtractApiError(root)}");

            if (attempt < _maxPollAttempts - 1 && _pollDelay > TimeSpan.Zero)
                await Task.Delay(_pollDelay, ct);
        }

        throw new TimeoutException(
            $"Soniox transcription {transcriptionId} did not complete within the configured polling window.");
    }

    private async Task<string> FetchTranscriptAsync(string transcriptionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v1/transcriptions/{transcriptionId}/transcript");
        AddAuthorization(request);

        return await SendJsonAsync(request, "Soniox transcript retrieval", ct);
    }

    private async Task<string> SendJsonAsync(HttpRequestMessage request, string operation, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} error {(int)response.StatusCode}: {ExtractApiError(json)}");
        }

        return json;
    }

    private async Task CleanupAsync(string? transcriptionId, string? fileId)
    {
        if (transcriptionId is not null)
            await DeleteBestEffortAsync($"{BaseUrl}/v1/transcriptions/{transcriptionId}", "transcription");

        if (fileId is not null)
            await DeleteBestEffortAsync($"{BaseUrl}/v1/files/{fileId}", "file");
    }

    private async Task DeleteBestEffortAsync(string uri, string resourceName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        AddAuthorization(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Soniox cleanup could not delete {resourceName}: {(int)response.StatusCode} {ExtractApiError(json)}");
            }
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Soniox cleanup could not delete {resourceName}: {ex.Message}");
        }
    }

    internal static PluginTranscriptionResult ParseTranscript(
        string transcriptJson,
        JsonElement completedDetails,
        string? fallbackLanguage)
    {
        using var doc = JsonDocument.Parse(transcriptJson);
        var root = doc.RootElement;
        var text = GetString(root, "text")?.Trim() ?? "";
        var duration = TryGetDouble(completedDetails, "audio_duration_ms", out var durationMs)
            ? durationMs / 1000.0
            : 0.0;

        var segments = new List<PluginTranscriptionSegment>();
        string? detectedLanguage = null;

        if (root.TryGetProperty("tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in tokens.EnumerateArray())
            {
                var tokenText = GetString(token, "text");
                if (string.IsNullOrWhiteSpace(tokenText))
                    continue;

                detectedLanguage ??= GetString(token, "language");

                if (!TryGetDouble(token, "start_ms", out var startMs)
                    || !TryGetDouble(token, "end_ms", out var endMs))
                {
                    continue;
                }

                var start = startMs / 1000.0;
                var end = endMs / 1000.0;
                segments.Add(new PluginTranscriptionSegment(tokenText.Trim(), start, end));
                duration = Math.Max(duration, end);
            }
        }

        return new PluginTranscriptionResult(text, detectedLanguage ?? fallbackLanguage, duration, NoSpeechProbability: null)
        {
            Segments = segments
        };
    }

    private void AddAuthorization(HttpRequestMessage request) =>
        AddAuthorization(request, _apiKey ?? throw new InvalidOperationException("Plugin not configured. API key required."));

    private static void AddAuthorization(HttpRequestMessage request, string apiKey) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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

    private static string ExtractApiError(JsonElement root)
    {
        var errorType = GetString(root, "error_type");
        var message = GetString(root, "error_message")
            ?? GetString(root, "message")
            ?? GetNestedErrorMessage(root)
            ?? "Unknown error";
        var requestId = GetString(root, "request_id");

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(errorType))
            sb.Append(errorType).Append(": ");

        sb.Append(message);

        if (!string.IsNullOrWhiteSpace(requestId))
            sb.Append(" (request_id: ").Append(requestId).Append(')');

        return sb.ToString();
    }

    private static string? GetNestedErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
            return null;

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString(),
            JsonValueKind.Object => GetString(error, "message") ?? GetString(error, "detail"),
            _ => null
        };
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(5) };

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}
