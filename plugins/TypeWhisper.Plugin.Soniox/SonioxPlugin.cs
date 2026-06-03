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
    private const int MaxSubtitleSegmentCharacters = 84;
    private const int MinSentenceSegmentCharacters = 20;
    private const double MaxSubtitleSegmentDurationSeconds = 6.0;
    private const double SubtitleSegmentPauseSplitSeconds = 0.75;

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
    public string PluginVersion => "1.0.2";

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

        var apiKey = _apiKey;
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Plugin not configured. API key required.");

        string? fileId = null;
        string? transcriptionId = null;

        try
        {
            fileId = await UploadFileAsync(wavAudio, apiKey, ct);
            transcriptionId = await CreateTranscriptionAsync(fileId, language, apiKey, ct);
            var completedDetails = await WaitUntilCompletedAsync(transcriptionId, apiKey, ct);
            var transcriptJson = await FetchTranscriptAsync(transcriptionId, apiKey, ct);
            return ParseTranscript(transcriptJson, completedDetails, NormalizeLanguage(language));
        }
        finally
        {
            await CleanupAsync(transcriptionId, fileId, apiKey);
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

            if (!changed)
                return;

            if (_host is not null)
            {
                if (normalized is null)
                    await _host.DeleteSecretAsync(ApiKeySecretName);
                else
                    await _host.StoreSecretAsync(ApiKeySecretName, normalized);

                hostToNotify = _host;
            }

            _apiKey = normalized;

            if (wasConfigured == IsConfigured)
                hostToNotify = null;
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            AddAuthorization(request, normalized);
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<string> UploadFileAsync(byte[] wavAudio, string apiKey, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/files");
        AddAuthorization(request, apiKey);
        request.Content = form;

        var json = await SendJsonAsync(request, "Soniox file upload", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox file upload response did not include a file id.");
    }

    private async Task<string> CreateTranscriptionAsync(
        string fileId,
        string? language,
        string apiKey,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = SonioxAsyncModelId,
            ["file_id"] = fileId,
        };

        if (NormalizeLanguage(language) is { } normalizedLanguage)
            payload["language_hints"] = new[] { normalizedLanguage };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/transcriptions");
        AddAuthorization(request, apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var json = await SendJsonAsync(request, "Soniox transcription creation", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox transcription response did not include a transcription id.");
    }

    private async Task<JsonElement> WaitUntilCompletedAsync(string transcriptionId, string apiKey, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _maxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/transcriptions/{transcriptionId}");
            AddAuthorization(request, apiKey);

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

    private async Task<string> FetchTranscriptAsync(string transcriptionId, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v1/transcriptions/{transcriptionId}/transcript");
        AddAuthorization(request, apiKey);

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

    private async Task CleanupAsync(string? transcriptionId, string? fileId, string apiKey)
    {
        if (transcriptionId is not null)
            await DeleteBestEffortAsync($"{BaseUrl}/v1/transcriptions/{transcriptionId}", "transcription", apiKey);

        if (fileId is not null)
            await DeleteBestEffortAsync($"{BaseUrl}/v1/files/{fileId}", "file", apiKey);
    }

    private async Task DeleteBestEffortAsync(string uri, string resourceName, string apiKey)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        AddAuthorization(request, apiKey);

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
        catch (HttpRequestException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Soniox cleanup could not delete {resourceName}: {ex.Message}");
        }
        catch (TaskCanceledException ex)
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

        var segmentTokens = new List<SonioxTimedToken>();
        string? detectedLanguage = null;
        var transcriptCursor = 0;

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
                var displayText = ResolveDisplayText(text, tokenText, ref transcriptCursor);
                if (end <= start)
                    continue;

                if (!string.IsNullOrWhiteSpace(displayText))
                    segmentTokens.Add(new SonioxTimedToken(displayText, start, end));

                duration = Math.Max(duration, end);
            }
        }

        return new PluginTranscriptionResult(text, detectedLanguage ?? fallbackLanguage, duration, NoSpeechProbability: null)
        {
            Segments = BuildSubtitleSegments(segmentTokens)
        };
    }

    private static List<PluginTranscriptionSegment> BuildSubtitleSegments(IReadOnlyList<SonioxTimedToken> tokens)
    {
        var segments = new List<PluginTranscriptionSegment>();
        var text = new StringBuilder();
        var start = 0.0;
        var end = 0.0;
        var hasSegment = false;

        foreach (var token in tokens)
        {
            if (hasSegment && ShouldStartNewSubtitleSegment(token, text, start, end))
                FlushSegment();

            if (!hasSegment)
            {
                text.Clear();
                start = token.Start;
                hasSegment = true;
            }

            text.Append(token.Text);
            end = token.End;

            if (ShouldEndSubtitleSegment(text, start, end))
                FlushSegment();
        }

        FlushSegment();
        return segments;

        void FlushSegment()
        {
            if (!hasSegment)
                return;

            var normalizedText = NormalizeSubtitleText(text.ToString());
            if (normalizedText.Length > 0)
                segments.Add(new PluginTranscriptionSegment(normalizedText, start, end));

            text.Clear();
            hasSegment = false;
        }
    }

    private static bool ShouldStartNewSubtitleSegment(
        SonioxTimedToken token,
        StringBuilder currentText,
        double currentStart,
        double currentEnd)
    {
        if (token.Start - currentEnd > SubtitleSegmentPauseSplitSeconds)
            return true;

        if (token.End - currentStart > MaxSubtitleSegmentDurationSeconds)
            return true;

        var currentLength = NormalizeSubtitleText(currentText.ToString()).Length;
        var tokenLength = NormalizeSubtitleText(token.Text).Length;
        return currentLength > 0 && currentLength + tokenLength > MaxSubtitleSegmentCharacters;
    }

    private static bool ShouldEndSubtitleSegment(StringBuilder currentText, double start, double end)
    {
        var normalizedText = NormalizeSubtitleText(currentText.ToString());
        if (normalizedText.Length >= MinSentenceSegmentCharacters
            && EndsWithSentenceTerminator(normalizedText))
        {
            return true;
        }

        return end - start >= MaxSubtitleSegmentDurationSeconds;
    }

    private static string ResolveDisplayText(string transcriptText, string tokenText, ref int transcriptCursor)
    {
        var trimmedToken = tokenText.Trim();
        if (trimmedToken.Length == 0)
            return "";

        if (transcriptText.Length > 0 && transcriptCursor <= transcriptText.Length)
        {
            var match = transcriptText.IndexOf(trimmedToken, transcriptCursor, StringComparison.Ordinal);
            if (match >= 0)
            {
                var end = match + trimmedToken.Length;
                var displayText = transcriptText[transcriptCursor..end];
                transcriptCursor = end;
                return displayText;
            }
        }

        return trimmedToken;
    }

    private static string NormalizeSubtitleText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "";

        var sb = new StringBuilder(trimmed.Length);
        var previousWasWhitespace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhitespace)
                    sb.Append(' ');

                previousWasWhitespace = true;
                continue;
            }

            sb.Append(ch);
            previousWasWhitespace = false;
        }

        return sb.ToString();
    }

    private static bool EndsWithSentenceTerminator(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch is '"' or '\'' or ')' or ']' or '}')
                continue;

            return ch is '.' or '!' or '?';
        }

        return false;
    }

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

    private static string? NormalizeLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : trimmed;
    }

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

    private sealed record SonioxTimedToken(string Text, double Start, double End);

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}
