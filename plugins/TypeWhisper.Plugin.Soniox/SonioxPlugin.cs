using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using NAudio.MediaFoundation;
using NAudio.Wave;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Soniox;

/// <summary>
/// Provides soniox plugin behavior.
/// </summary>
public sealed class SonioxPlugin : ITranscriptionEnginePlugin
{
    internal const string DefaultModelId = "default";

    internal const string DefaultRegionId = "us";
    private const string RegionSettingKey = "region";
    private const string ApiKeySecretName = "api-key";
    private const string SonioxAsyncModelId = "stt-async-v5";
    private const string InvalidAudioFileErrorType = "invalid_audio_file";
    private const string ErrorTypeDataKey = "TypeWhisper.Soniox.ErrorType";
    private const string IsFileUploadDataKey = "TypeWhisper.Soniox.IsFileUpload";
    private const int TranscriptionUploadBitRate = 48_000;
    private const int DefaultMaxPollAttempts = 3600;
    private const int MaxSubtitleSegmentCharacters = 84;
    private const int MinSentenceSegmentCharacters = 20;
    private const double MaxSubtitleSegmentDurationSeconds = 6.0;
    private const double SubtitleSegmentPauseSplitSeconds = 0.75;

    private const double PollBackoffFactor = 1.5;

    // Async transcription completion is polled. Start with a short delay so brief dictation
    // clips (which finish in well under a second) are picked up quickly, then exponentially
    // back off toward a cap so long recordings do not hammer the API.
    private static readonly TimeSpan DefaultInitialPollDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DefaultMaxPollDelay = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new(DefaultModelId, "Soniox Async")
        {
            IsRecommended = true
        },
    ];

    // Soniox data residency: each region has its own domain and requires an API key from a
    // project created in that region. https://soniox.com/docs/stt/data-residency
    private static readonly IReadOnlyList<SonioxRegion> Regions =
    [
        new(DefaultRegionId, "United States", "https://api.soniox.com"),
        new("eu", "European Union", "https://api.eu.soniox.com"),
        new("jp", "Japan", "https://api.jp.soniox.com"),
    ];

    // Each auto-detect probe is bounded so an unreachable region fails fast rather than
    // blocking the settings Test flow for the full HttpClient timeout.
    private static readonly TimeSpan RegionProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _initialPollDelay;
    private readonly TimeSpan _maxPollDelay;
    private readonly int _maxPollAttempts;
    private readonly Func<byte[], SonioxTranscriptionUpload> _compressedUploadFactory;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);

    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _selectedModelId = DefaultModelId;
    private string _region = DefaultRegionId;
    private Task _lastCleanupTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the SonioxPlugin class.
    /// </summary>
    public SonioxPlugin()
        : this(CreateHttpClient())
    {
    }

    internal SonioxPlugin(
        HttpClient httpClient,
        TimeSpan? pollDelay = null,
        int maxPollAttempts = DefaultMaxPollAttempts,
        Func<byte[], SonioxTranscriptionUpload>? compressedUploadFactory = null)
    {
        if (maxPollAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPollAttempts), "Poll attempts must be positive.");

        _httpClient = httpClient;
        // A caller-supplied pollDelay (used by tests for determinism) pins both bounds to a
        // fixed interval; otherwise poll with an adaptive initial delay that backs off to a cap.
        _initialPollDelay = pollDelay ?? DefaultInitialPollDelay;
        _maxPollDelay = pollDelay ?? DefaultMaxPollDelay;
        _maxPollAttempts = maxPollAttempts;
        _compressedUploadFactory = compressedUploadFactory ?? CreateCompressedUpload;
    }

    private string BaseUrl => ResolveRegion(_region).BaseUrl;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.soniox";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Soniox";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.1.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _selectedModelId = DefaultModelId;
        _region = NormalizeRegionId(host.GetSetting<string>(RegionSettingKey));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured}, region={_region})");
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
    public UserControl? CreateSettingsView() => new SonioxSettingsView(this);

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "soniox";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Soniox";
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
        if (!string.Equals(modelId, DefaultModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown model: {modelId}");

        _selectedModelId = DefaultModelId;
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        await TranscribeWithLanguageHintsAsync(
            wavAudio,
            NormalizeLanguage(language) is { } normalizedLanguage ? [normalizedLanguage] : [],
            translate,
            prompt,
            ct);

    /// <summary>
    /// Transcribes PCM audio using ordered language hints.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Soniox does not support translation.");

        var apiKey = _apiKey;
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Plugin not configured. API key required.");

        var normalizedLanguageHints = languageHints
            .Select(NormalizeLanguage)
            .Where(static language => language is not null)
            .Select(static language => language!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var context = new SonioxTranscriptionContext(
            BaseUrl,
            apiKey,
            SonioxAsyncModelId,
            normalizedLanguageHints);
        var preferredUpload = CreatePreferredUpload(wavAudio, ct);

        try
        {
            return await TranscribeUploadAsync(preferredUpload, context, ct);
        }
        catch (Exception ex) when (
            preferredUpload.Format == SonioxUploadFormat.M4a
            && ShouldRetryWithWav(ex))
        {
            ct.ThrowIfCancellationRequested();
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox rejected the M4A upload; retrying the complete transaction with WAV: {ex.Message}");
            return await TranscribeUploadAsync(CreateWavUpload(wavAudio), context, ct);
        }
    }

    private async Task<PluginTranscriptionResult> TranscribeUploadAsync(
        SonioxTranscriptionUpload upload,
        SonioxTranscriptionContext context,
        CancellationToken ct)
    {
        string? fileId = null;
        string? transcriptionId = null;

        try
        {
            fileId = await UploadFileAsync(upload, context, ct);
            transcriptionId = await CreateTranscriptionAsync(fileId, context, ct);
            var completedDetails = await WaitUntilCompletedAsync(transcriptionId, context, ct);
            var transcriptJson = await FetchTranscriptAsync(transcriptionId, context, ct);
            var result = ParseTranscript(
                transcriptJson,
                completedDetails,
                context.LanguageHints.FirstOrDefault());

            // Deleting the uploaded file and the transcription is best-effort housekeeping the
            // caller does not need to wait for. Run it off the critical path so the two extra
            // round-trips do not add to perceived dictation latency.
            _lastCleanupTask = CleanupInBackgroundAsync(transcriptionId, fileId, context);
            return result;
        }
        catch
        {
            await CleanupAfterFailureAsync(transcriptionId, fileId, context);
            throw;
        }
    }

    // Settings support

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

    internal static IReadOnlyList<SonioxRegion> AvailableRegions => Regions;

    /// <summary>Gets the currently selected Soniox data-residency region id.</summary>
    internal string RegionId => _region;

    /// <summary>Persists the selected Soniox region. Unknown ids fall back to the default (US).</summary>
    internal void SetRegion(string regionId)
    {
        var normalized = NormalizeRegionId(regionId);
        if (string.Equals(_region, normalized, StringComparison.Ordinal))
            return;

        _region = normalized;
        _host?.SetSetting(RegionSettingKey, normalized);
    }

    /// <summary>
    /// Probes each regional endpoint with the key and returns the region id it authenticates
    /// against, or null if none accept it. Region is bound to the key's project, not the
    /// user's location, so probing detects the correct region reliably.
    /// </summary>
    internal async Task<string?> DetectRegionAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return null;

        foreach (var region in Regions)
        {
            // Bound the probe so an unreachable region fails fast instead of blocking on the
            // full HttpClient timeout; a probe timeout skips to the next region, while a real
            // caller cancellation still propagates.
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(RegionProbeTimeout);

            try
            {
                if (await ProbeModelsAsync(region.BaseUrl, normalized, probeCts.Token))
                    return region.Id;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // This region's probe timed out; try the next region.
            }
        }

        return null;
    }

    /// <summary>
    /// Best-effort cleanup task from the most recent successful transcription.
    /// Exposed so tests can await background cleanup deterministically.
    /// </summary>
    internal Task LastCleanupTask => _lastCleanupTask;

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

        return await ProbeModelsAsync(BaseUrl, normalized, ct);
    }

    private async Task<bool> ProbeModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
            AddAuthorization(request, apiKey);
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

    private SonioxTranscriptionUpload CreatePreferredUpload(byte[] wavAudio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return _compressedUploadFactory(wavAudio);
        }
        catch (Exception ex) when (IsExpectedCompressionFailure(ex))
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox M4A encoding failed; falling back to WAV: {ex.Message}");
            return CreateWavUpload(wavAudio);
        }
    }

    private static bool IsExpectedCompressionFailure(Exception ex) =>
        ex is InvalidDataException
        or IOException
        or InvalidOperationException
        or COMException
        or NotSupportedException;

    internal static SonioxTranscriptionUpload CreateCompressedUpload(byte[] wavAudio)
    {
        ArgumentNullException.ThrowIfNull(wavAudio);

        using var input = new MemoryStream(wavAudio, writable: false);
        using var reader = new WaveFileReader(input);
        using var output = new MemoryStream();
        MediaFoundationEncoder.EncodeToAac(reader, output, TranscriptionUploadBitRate);

        var bytes = output.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("Media Foundation produced an empty AAC upload.");

        return new SonioxTranscriptionUpload(
            bytes,
            "audio.m4a",
            "audio/mp4",
            SonioxUploadFormat.M4a);
    }

    private static SonioxTranscriptionUpload CreateWavUpload(byte[] wavAudio) =>
        new(wavAudio, "audio.wav", "audio/wav", SonioxUploadFormat.Wav);

    private static bool ShouldRetryWithWav(Exception ex) =>
        ex switch
        {
            HttpRequestException httpException
                when IsFileUploadException(httpException)
                && (httpException.StatusCode == HttpStatusCode.UnsupportedMediaType
                    || IsInvalidAudioFile(GetErrorType(httpException))) => true,
            InvalidOperationException jobException
                when IsInvalidAudioFile(GetErrorType(jobException)) => true,
            _ => false
        };

    private static bool IsFileUploadException(Exception ex) =>
        ex.Data[IsFileUploadDataKey] is true;

    private static string? GetErrorType(Exception ex) =>
        ex.Data[ErrorTypeDataKey] as string;

    private static bool IsInvalidAudioFile(string? errorType) =>
        string.Equals(errorType, InvalidAudioFileErrorType, StringComparison.OrdinalIgnoreCase);

    private async Task<string> UploadFileAsync(
        SonioxTranscriptionUpload upload,
        SonioxTranscriptionContext context,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(upload.Data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        form.Add(fileContent, "file", upload.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{context.BaseUrl}/v1/files");
        AddAuthorization(request, context.ApiKey);
        request.Content = form;

        var json = await SendJsonAsync(request, "Soniox file upload", ct, isFileUpload: true);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox file upload response did not include a file id.");
    }

    private async Task<string> CreateTranscriptionAsync(
        string fileId,
        SonioxTranscriptionContext context,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = context.ModelId,
            ["file_id"] = fileId,
        };

        if (context.LanguageHints.Count > 0)
            payload["language_hints"] = context.LanguageHints;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{context.BaseUrl}/v1/transcriptions");
        AddAuthorization(request, context.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var json = await SendJsonAsync(request, "Soniox transcription creation", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox transcription response did not include a transcription id.");
    }

    private async Task<JsonElement> WaitUntilCompletedAsync(
        string transcriptionId,
        SonioxTranscriptionContext context,
        CancellationToken ct)
    {
        var delay = _initialPollDelay;
        for (var attempt = 0; attempt < _maxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{context.BaseUrl}/v1/transcriptions/{transcriptionId}");
            AddAuthorization(request, context.ApiKey);

            var json = await SendJsonAsync(request, "Soniox transcription status", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = GetString(root, "status");

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return root.Clone();

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = ExtractApiErrorDetails(root);
                var exception = new InvalidOperationException(
                    $"Soniox transcription failed: {FormatApiError(error)}");
                if (!string.IsNullOrWhiteSpace(error.ErrorType))
                    exception.Data[ErrorTypeDataKey] = error.ErrorType;
                throw exception;
            }

            if (attempt < _maxPollAttempts - 1 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(
                    (long)(delay.Ticks * PollBackoffFactor),
                    _maxPollDelay.Ticks));
            }
        }

        throw new TimeoutException(
            $"Soniox transcription {transcriptionId} did not complete within the configured polling window.");
    }

    private async Task<string> FetchTranscriptAsync(
        string transcriptionId,
        SonioxTranscriptionContext context,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{context.BaseUrl}/v1/transcriptions/{transcriptionId}/transcript");
        AddAuthorization(request, context.ApiKey);

        return await SendJsonAsync(request, "Soniox transcript retrieval", ct);
    }

    private async Task<string> SendJsonAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken ct,
        bool isFileUpload = false)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = ExtractApiErrorDetails(json);
            var exception = new HttpRequestException(
                $"{operation} error {(int)response.StatusCode}: {FormatApiError(error)}",
                inner: null,
                response.StatusCode);
            if (!string.IsNullOrWhiteSpace(error.ErrorType))
                exception.Data[ErrorTypeDataKey] = error.ErrorType;
            if (isFileUpload)
                exception.Data[IsFileUploadDataKey] = true;
            throw exception;
        }

        return json;
    }

    private async Task CleanupAsync(
        string? transcriptionId,
        string? fileId,
        SonioxTranscriptionContext context)
    {
        if (transcriptionId is not null)
        {
            await DeleteBestEffortAsync(
                $"{context.BaseUrl}/v1/transcriptions/{transcriptionId}",
                "transcription",
                context.ApiKey);
        }

        if (fileId is not null)
            await DeleteBestEffortAsync($"{context.BaseUrl}/v1/files/{fileId}", "file", context.ApiKey);
    }

    private async Task CleanupAfterFailureAsync(
        string? transcriptionId,
        string? fileId,
        SonioxTranscriptionContext context)
    {
        try
        {
            await CleanupAsync(transcriptionId, fileId, context);
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Soniox failure cleanup failed: {ex.Message}");
        }
    }

    private async Task CleanupInBackgroundAsync(
        string? transcriptionId,
        string? fileId,
        SonioxTranscriptionContext context)
    {
        // Yield so the transcript is returned to the caller before cleanup round-trips run.
        await Task.Yield();

        try
        {
            await CleanupAsync(transcriptionId, fileId, context);
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Soniox background cleanup failed: {ex.Message}");
        }
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
        => FormatApiError(ExtractApiErrorDetails(json));

    private static SonioxErrorDetails ExtractApiErrorDetails(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractApiErrorDetails(doc.RootElement);
        }
        catch (JsonException)
        {
            return new SonioxErrorDetails(
                ErrorType: null,
                Message: string.IsNullOrWhiteSpace(json) ? "Unknown error" : json,
                RequestId: null);
        }
    }

    private static string ExtractApiError(JsonElement root)
        => FormatApiError(ExtractApiErrorDetails(root));

    private static SonioxErrorDetails ExtractApiErrorDetails(JsonElement root)
    {
        var errorType = GetString(root, "error_type");
        var message = GetString(root, "error_message")
            ?? GetString(root, "message")
            ?? GetNestedErrorMessage(root)
            ?? "Unknown error";
        var requestId = GetString(root, "request_id");
        return new SonioxErrorDetails(errorType, message, requestId);
    }

    private static string FormatApiError(SonioxErrorDetails error)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(error.ErrorType))
            sb.Append(error.ErrorType).Append(": ");

        sb.Append(error.Message);

        if (!string.IsNullOrWhiteSpace(error.RequestId))
            sb.Append(" (request_id: ").Append(error.RequestId).Append(')');

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

    private static string NormalizeRegionId(string? regionId) => ResolveRegion(regionId).Id;

    private static SonioxRegion ResolveRegion(string? regionId) =>
        Regions.FirstOrDefault(region =>
            string.Equals(region.Id, regionId?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Regions[0];

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

    private sealed record SonioxTranscriptionContext(
        string BaseUrl,
        string ApiKey,
        string ModelId,
        IReadOnlyList<string> LanguageHints);

    private sealed record SonioxErrorDetails(string? ErrorType, string Message, string? RequestId);

    private sealed record SonioxTimedToken(string Text, double Start, double End);

    /// <summary>A Soniox data-residency region and its REST base URL.</summary>
    internal sealed record SonioxRegion(string Id, string DisplayName, string BaseUrl);

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}

internal enum SonioxUploadFormat
{
    Wav,
    M4a
}

internal sealed record SonioxTranscriptionUpload(
    byte[] Data,
    string FileName,
    string ContentType,
    SonioxUploadFormat Format);
