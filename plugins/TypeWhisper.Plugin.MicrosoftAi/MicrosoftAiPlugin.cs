using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.MicrosoftAi;

internal enum MicrosoftAiTranscriptStyle
{
    Clean,
    Verbatim
}

/// <summary>
/// Provides Microsoft MAI Transcribe through the Azure Speech fast transcription API.
/// </summary>
public sealed class MicrosoftAiPlugin : ITranscriptionEnginePlugin
{
    internal const string DefaultModelId = "MAI-Transcribe-2";
    internal const string LegacyModelId = "MAI-Transcribe-1.5";
    internal const string ApiVersion = "2025-10-15";
    internal const int MaximumAudioBytes = 250_000_000;
    internal const double MaximumAudioDurationSeconds = 2 * 60 * 60;

    private const string ApiKeySecretName = "api-key";
    private const string EndpointSettingName = "endpoint";
    private const string SelectedModelSettingName = "selectedModel";
    private const string CachedModelsSettingName = "cachedModels";
    private const string TranscriptStyleSettingName = "transcriptStyle";
    private const string SpeakerDiarizationSettingName = "speakerDiarizationEnabled";

    private static readonly IReadOnlyList<string> FallbackModelIds = [DefaultModelId, LegacyModelId];
    private static readonly IReadOnlyList<string> AllowedHostSuffixes =
    [
        ".cognitiveservices.azure.com",
        ".api.cognitive.microsoft.com",
        ".services.ai.azure.com",
        ".openai.azure.com",
        ".cognitiveservices.azure.us",
        ".api.cognitive.microsoft.us",
        ".services.ai.azure.us",
        ".openai.azure.us",
        ".cognitiveservices.azure.cn",
        ".api.cognitive.azure.cn",
        ".services.ai.azure.cn",
        ".openai.azure.cn",
    ];
    private static readonly IReadOnlyList<string> RegionalHostSuffixes =
    [
        ".api.cognitive.microsoft.com",
        ".api.cognitive.microsoft.us",
        ".api.cognitive.azure.cn",
    ];
    internal static readonly IReadOnlyList<string> SupportedMaiRegions =
        ["eastus", "northeurope", "southeastasia", "westus"];
    private static readonly IReadOnlyList<string> Languages =
    [
        "af", "ar", "as", "az", "bg", "bn", "bs", "ca", "cs", "da", "de", "el", "en", "es", "et",
        "fa", "fi", "fil", "fr", "gl", "gu", "he", "hi", "hu", "hy", "id", "is", "it", "ja", "kk",
        "kn", "ko", "lt", "lv", "mk", "ml", "mr", "ms", "nb", "ne", "nl", "or", "pa", "pl", "pt",
        "ro", "ru", "sk", "sl", "sv", "sw", "ta", "te", "th", "tr", "uk", "ur", "vi", "yue", "zh",
    ];
    private static readonly DictionaryTermsBudget DictionaryBudget =
        new(MaxTerms: 500, MaxTotalChars: 20_000);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _endpointValue = "";
    private string _selectedModelId = DefaultModelId;
    private IReadOnlyList<string> _fetchedModelIds = [];
    private MicrosoftAiTranscriptStyle _transcriptStyle = MicrosoftAiTranscriptStyle.Clean;
    private bool _speakerDiarizationEnabled;

    /// <summary>
    /// Initializes a new Microsoft AI plugin instance.
    /// </summary>
    public MicrosoftAiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(180) })
    {
    }

    internal MicrosoftAiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.microsoft-ai";

    /// <inheritdoc />
    public string PluginName => "Microsoft AI";

    /// <inheritdoc />
    public string PluginVersion => "1.0.0";

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeSecret(await host.LoadSecretAsync(ApiKeySecretName));
        _endpointValue = NormalizePersistedEndpoint(host.GetSetting<string>(EndpointSettingName));
        _fetchedModelIds = NormalizeModelIds(host.GetSetting<IReadOnlyList<string>>(CachedModelsSettingName) ?? []);

        var storedModel = host.GetSetting<string>(SelectedModelSettingName)?.Trim();
        _selectedModelId = AllModelIds.Contains(storedModel ?? "", StringComparer.OrdinalIgnoreCase)
            ? AllModelIds.First(model => string.Equals(model, storedModel, StringComparison.OrdinalIgnoreCase))
            : DefaultModelId;
        if (!string.Equals(storedModel, _selectedModelId, StringComparison.Ordinal))
            host.SetSetting(SelectedModelSettingName, _selectedModelId);

        _transcriptStyle = string.Equals(
            host.GetSetting<string>(TranscriptStyleSettingName),
            "verbatim",
            StringComparison.OrdinalIgnoreCase)
            ? MicrosoftAiTranscriptStyle.Verbatim
            : MicrosoftAiTranscriptStyle.Clean;
        _speakerDiarizationEnabled =
            host.GetSetting<bool?>(SpeakerDiarizationSettingName) ?? false;

        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public UserControl? CreateSettingsView() => new MicrosoftAiSettingsView(this);

    /// <inheritdoc />
    public string ProviderId => "microsoft-ai";

    /// <inheritdoc />
    public string ProviderDisplayName => "Microsoft AI (MAI Transcribe)";

    /// <inheritdoc />
    public bool IsConfigured => _apiKey is not null && Endpoint is not null;

    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        AllModelIds.Select(CreateModelInfo).ToList();

    /// <inheritdoc />
    public string? SelectedModelId => _selectedModelId;

    /// <inheritdoc />
    public bool SupportsTranslation => false;

    /// <inheritdoc />
    public bool SupportsStreaming => false;

    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;

    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => DictionaryBudget;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => Languages;

    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        var normalized = modelId?.Trim();
        var selected = AllModelIds.FirstOrDefault(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new ArgumentException($"Unknown Microsoft AI transcription model: {modelId}", nameof(modelId));

        if (string.Equals(_selectedModelId, selected, StringComparison.Ordinal))
            return;

        _selectedModelId = selected;
        _host?.SetSetting(SelectedModelSettingName, selected);
        _host?.NotifyCapabilitiesChanged();
    }

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeCoreAsync(wavAudio, NormalizeLanguage(language), translate, prompt, ct);

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        var hints = languageHints
            .Select(NormalizeLanguage)
            .Where(static value => value is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TranscribeCoreAsync(
            wavAudio,
            hints.Count == 1 ? hints[0] : null,
            translate,
            prompt,
            ct);
    }

    internal string EndpointValue => _endpointValue;
    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal MicrosoftAiTranscriptStyle TranscriptStyle => _transcriptStyle;
    internal bool SpeakerDiarizationEnabled => _speakerDiarizationEnabled;
    internal bool SelectedModelSupportsDiarization => !IsLegacyModel(_selectedModelId);

    internal void SetEndpoint(string value)
    {
        var wasConfigured = IsConfigured;
        var normalized = NormalizeEndpoint(value)?.AbsoluteUri.TrimEnd('/') ?? value.Trim();
        if (string.Equals(_endpointValue, normalized, StringComparison.Ordinal))
            return;

        _endpointValue = normalized;
        _host?.SetSetting(EndpointSettingName, normalized);
        if (_host is not null && wasConfigured != IsConfigured)
            _host.NotifyCapabilitiesChanged();
    }

    internal async Task SetApiKeyAsync(string value)
    {
        var wasConfigured = IsConfigured;
        var normalized = NormalizeSecret(value);
        if (string.Equals(_apiKey, normalized, StringComparison.Ordinal))
            return;

        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);
        }

        _apiKey = normalized;
        if (_host is not null && wasConfigured != IsConfigured)
            _host.NotifyCapabilitiesChanged();
    }

    internal void SetTranscriptStyle(MicrosoftAiTranscriptStyle value)
    {
        if (_transcriptStyle == value)
            return;
        _transcriptStyle = value;
        _host?.SetSetting(
            TranscriptStyleSettingName,
            value == MicrosoftAiTranscriptStyle.Verbatim ? "verbatim" : "clean");
    }

    internal void SetSpeakerDiarizationEnabled(bool enabled)
    {
        var normalized = enabled && SelectedModelSupportsDiarization;
        if (_speakerDiarizationEnabled == normalized)
            return;
        _speakerDiarizationEnabled = normalized;
        _host?.SetSetting(SpeakerDiarizationSettingName, normalized);
    }

    internal async Task<bool> RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        var endpoint = Endpoint;
        var apiKey = _apiKey;
        if (endpoint is null || apiKey is null)
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelCatalogUri(endpoint));
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Microsoft AI model catalog returned HTTP {(int)response.StatusCode}.");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var fetched = data.EnumerateArray()
                .Select(static item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .OfType<string>();
            var normalized = NormalizeModelIds(fetched);
            if (normalized.Count == 0)
                return false;

            _fetchedModelIds = normalized;
            _host?.SetSetting(CachedModelsSettingName, normalized);
            if (!AllModelIds.Contains(_selectedModelId, StringComparer.OrdinalIgnoreCase))
            {
                _selectedModelId = DefaultModelId;
                _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
            }
            _host?.NotifyCapabilitiesChanged();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException)
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"Microsoft AI model catalog refresh failed with {ex.GetType().Name}.");
            return false;
        }
    }

    internal static Uri? NormalizeEndpoint(string? rawValue)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (!IsValidResourceName(value))
                return null;
            value = $"https://{value}.cognitiveservices.azure.com";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (!AllowedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)))
            return null;

        return new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    internal static Uri BuildTranscriptionUri(Uri endpoint) =>
        new(endpoint, $"speechtotext/transcriptions:transcribe?api-version={ApiVersion}");

    internal static Uri BuildModelCatalogUri(Uri endpoint) => new(endpoint, "openai/v1/models");

    internal static string? GetRegionalEndpointRegion(Uri endpoint)
    {
        var host = endpoint.IdnHost.ToLowerInvariant();
        foreach (var suffix in RegionalHostSuffixes)
        {
            if (!host.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var region = host[..^suffix.Length];
            return region.Length > 0 && !region.Contains('.') ? region : null;
        }
        return null;
    }

    internal static string? GetUnsupportedMaiRegion(Uri endpoint)
    {
        var region = GetRegionalEndpointRegion(endpoint);
        return region is not null
            && !SupportedMaiRegions.Contains(region, StringComparer.OrdinalIgnoreCase)
            ? region
            : null;
    }

    internal static IReadOnlyList<string> ExtractDictionaryTerms(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];
        return PluginDictionaryTerms.Clip(
            prompt.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            DictionaryBudget);
    }

    internal static string BuildDefinitionJson(
        string modelId,
        string? language,
        IReadOnlyList<string> phrases,
        MicrosoftAiTranscriptStyle transcriptStyle,
        bool speakerDiarizationEnabled)
    {
        var legacy = IsLegacyModel(modelId);
        var enhancedMode = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["model"] = modelId,
        };
        if (legacy)
        {
            if (transcriptStyle == MicrosoftAiTranscriptStyle.Verbatim)
                enhancedMode["transcribeStyle"] = "verbatim";
        }
        else
        {
            enhancedMode["modelOptions"] = new Dictionary<string, object?>
            {
                ["timestamps"] = "segment",
                ["transcribeStyle"] = transcriptStyle == MicrosoftAiTranscriptStyle.Verbatim
                    ? "verbatim"
                    : "clean",
            };
        }

        var definition = new Dictionary<string, object?>
        {
            ["enhancedMode"] = enhancedMode,
            ["profanityFilterMode"] = "None",
        };
        if (language is not null)
            definition["locales"] = new[] { language };
        if (speakerDiarizationEnabled && !legacy)
            definition["diarization"] = new Dictionary<string, object?> { ["enabled"] = true };
        if (phrases.Count > 0)
            definition["phraseList"] = new Dictionary<string, object?> { ["phrases"] = phrases };

        return JsonSerializer.Serialize(definition, JsonOptions);
    }

    internal static PluginTranscriptionResult ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var segments = new List<PluginTranscriptionSegment>();
            string? detectedLanguage = null;
            var hasSpeakers = false;

            if (root.TryGetProperty("phrases", out var phrases) && phrases.ValueKind == JsonValueKind.Array)
            {
                foreach (var phrase in phrases.EnumerateArray())
                {
                    var phraseText = phrase.TryGetProperty("text", out var textElement)
                        ? textElement.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrEmpty(phraseText))
                        continue;

                    var start = Math.Max(GetOptionalDouble(phrase, "offsetMilliseconds") ?? 0, 0) / 1000d;
                    var duration = Math.Max(GetOptionalDouble(phrase, "durationMilliseconds") ?? 0, 0) / 1000d;
                    var speaker = phrase.TryGetProperty("speaker", out var speakerElement)
                        ? NormalizeSpeakerLabel(speakerElement)
                        : null;
                    hasSpeakers |= speaker is not null;
                    var segmentText = speaker is null ? phraseText : $"{speaker}: {phraseText}";
                    segments.Add(new PluginTranscriptionSegment(segmentText, start, start + duration));

                    if (detectedLanguage is null
                        && phrase.TryGetProperty("locale", out var localeElement)
                        && localeElement.ValueKind == JsonValueKind.String)
                    {
                        detectedLanguage = NormalizeLanguage(localeElement.GetString());
                    }
                }
            }

            var combinedText = ReadCombinedText(root);
            var outputText = hasSpeakers
                ? string.Join("\n", segments.Select(static segment => segment.Text))
                : !string.IsNullOrEmpty(combinedText)
                    ? combinedText
                    : string.Join(" ", segments.Select(static segment => segment.Text));

            var durationSeconds = Math.Max(GetOptionalDouble(root, "durationMilliseconds") ?? 0, 0) / 1000d;
            if (segments.Count > 0)
                durationSeconds = Math.Max(durationSeconds, segments.Max(static segment => segment.End));

            return new PluginTranscriptionResult(
                outputText,
                detectedLanguage,
                durationSeconds,
                NoSpeechProbability: null)
            {
                Segments = segments,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new PluginRequestException(
                "Azure Speech returned an invalid transcription response.",
                PluginRequestFailureKind.OutputIncomplete,
                innerException: ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private Uri? Endpoint => NormalizeEndpoint(_endpointValue);

    private IReadOnlyList<string> AllModelIds => NormalizeModelIds(FallbackModelIds.Concat(_fetchedModelIds));

    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        var endpoint = Endpoint;
        var apiKey = _apiKey;
        if (endpoint is null || apiKey is null)
        {
            throw new PluginRequestException(
                "Microsoft AI transcription requires an Azure Speech endpoint and API key.",
                PluginRequestFailureKind.Configuration);
        }
        if (translate)
        {
            throw new PluginRequestException(
                "MAI Transcribe does not support translation.",
                PluginRequestFailureKind.InvalidRequest);
        }
        if (wavAudio.Length >= MaximumAudioBytes
            || TryGetWavDurationSeconds(wavAudio) is { } duration
                && duration >= MaximumAudioDurationSeconds)
        {
            throw new PluginRequestException(
                "The audio file is too large for MAI Transcribe. Use WAV audio shorter than two hours and smaller than 250 MB.",
                PluginRequestFailureKind.RequestTooLarge);
        }

        var definitionJson = BuildDefinitionJson(
            _selectedModelId,
            language,
            ExtractDictionaryTerms(prompt),
            _transcriptStyle,
            _speakerDiarizationEnabled);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildTranscriptionUri(endpoint));
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(definitionJson, Encoding.UTF8, "application/json"), "definition");
        var audio = new ByteArrayContent(wavAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "audio.wav");
        request.Content = form;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException(
                "Could not reach Azure Speech. Check the endpoint and your network connection.",
                PluginRequestFailureKind.Network,
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                "The Azure Speech transcription request timed out.",
                PluginRequestFailureKind.Timeout,
                innerException: ex);
        }

        using (response)
        {
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw CreateHttpFailure(response, responseJson, endpoint, apiKey);
            return ParseResponse(responseJson);
        }
    }

    private PluginRequestException CreateHttpFailure(
        HttpResponseMessage response,
        string responseBody,
        Uri endpoint,
        string apiKey)
    {
        var statusCode = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
            retryAfter = retryAt - DateTimeOffset.UtcNow;

        var summary = ExtractErrorSummary(responseBody, apiKey);
        if (statusCode == 400
            && summary.Contains("enhanced mode with model", StringComparison.OrdinalIgnoreCase)
            && GetUnsupportedMaiRegion(endpoint) is { } region)
        {
            const string localizationKey = "Settings.RegionUnsupported";
            var supportedRegions = string.Join(", ", SupportedMaiRegions);
            var localizedMessage = _host?.Localization.GetString(
                localizationKey,
                region,
                supportedRegions);
            var regionMessage = string.IsNullOrWhiteSpace(localizedMessage)
                || string.Equals(localizedMessage, localizationKey, StringComparison.Ordinal)
                ? $"MAI Transcribe is not available in the Azure region {region}. Use a Speech resource in one of these regions: {supportedRegions}."
                : localizedMessage;
            return new PluginRequestException(
                regionMessage,
                PluginRequestFailureKind.InvalidRequest,
                statusCode);
        }

        var kind = statusCode switch
        {
            401 or 403 => PluginRequestFailureKind.Authentication,
            408 => PluginRequestFailureKind.Timeout,
            413 => PluginRequestFailureKind.RequestTooLarge,
            429 => PluginRequestFailureKind.RateLimit,
            >= 500 and <= 599 => PluginRequestFailureKind.ServerError,
            >= 400 and <= 499 => PluginRequestFailureKind.InvalidRequest,
            _ => PluginRequestFailureKind.Unknown,
        };
        var message = statusCode switch
        {
            401 or 403 => "Azure Speech rejected the API key. Check the key and endpoint for the selected Speech resource.",
            413 => "The audio file is too large for Azure Speech. Use WAV audio shorter than two hours and smaller than 250 MB.",
            429 => "Azure Speech rate limit reached. Wait and try again.",
            _ => $"Microsoft AI transcription failed (HTTP {statusCode}): {summary}",
        };
        return new PluginRequestException(message, kind, statusCode, retryAfter);
    }

    private static string ExtractErrorSummary(string body, string apiKey)
    {
        var summary = "Azure Speech returned an error.";
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (TryReadErrorText(root, out var errorText))
                    summary = errorText;
            }
            catch (JsonException)
            {
                summary = body.Trim();
            }
        }

        summary = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!string.IsNullOrEmpty(apiKey))
            summary = summary.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        return summary.Length <= 500 ? summary : summary[..500] + "…";
    }

    private static bool TryReadErrorText(JsonElement root, out string text)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            text = "";
            return false;
        }

        foreach (var propertyName in new[] { "message", "detail", "code" })
        {
            if (root.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                text = property.GetString()!;
                return true;
            }
        }

        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
            {
                text = error.GetString()!;
                return true;
            }
            if (error.ValueKind == JsonValueKind.Object && TryReadErrorText(error, out text))
                return true;
        }

        text = "";
        return false;
    }

    private static string ReadCombinedText(JsonElement root)
    {
        if (!root.TryGetProperty("combinedPhrases", out var combined)
            || combined.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Join(
            "\n",
            combined.EnumerateArray()
                .Select(static phrase => phrase.TryGetProperty("text", out var text) ? text.GetString()?.Trim() : null)
                .Where(static text => !string.IsNullOrEmpty(text))!);
    }

    private static string? NormalizeSpeakerLabel(JsonElement speaker)
    {
        var value = speaker.ValueKind switch
        {
            JsonValueKind.String => speaker.GetString(),
            JsonValueKind.Number => speaker.GetRawText(),
            _ => null,
        };
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return trimmed.Contains("speaker", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"Speaker {trimmed}";
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
            ? value
            : null;

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim().Replace('_', '-');
        return string.IsNullOrEmpty(normalized)
            || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string NormalizePersistedEndpoint(string? endpoint) =>
        NormalizeEndpoint(endpoint)?.AbsoluteUri.TrimEnd('/') ?? endpoint?.Trim() ?? "";

    private static string? NormalizeSecret(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsValidResourceName(string value)
    {
        if (value.Length is < 1 or > 64
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }
        return value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsLegacyModel(string modelId) =>
        string.Equals(modelId, LegacyModelId, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> NormalizeModelIds(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values
            .Select(static value => value.Trim())
            .Where(static value => value.Contains("mai-transcribe", StringComparison.OrdinalIgnoreCase))
            .Where(seen.Add)
            .OrderByDescending(static value =>
                string.Equals(value, DefaultModelId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(static value => IsLegacyModel(value))
            .ThenByDescending(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PluginModelInfo CreateModelInfo(string modelId)
    {
        var displayName = string.Equals(modelId, DefaultModelId, StringComparison.OrdinalIgnoreCase)
            ? "MAI Transcribe 2"
            : IsLegacyModel(modelId)
                ? "MAI Transcribe 1.5"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(modelId.Replace('-', ' ').ToLowerInvariant());
        return new PluginModelInfo(modelId, displayName)
        {
            IsRecommended = string.Equals(modelId, DefaultModelId, StringComparison.OrdinalIgnoreCase),
            LanguageCount = IsLegacyModel(modelId) ? 43 : 60,
        };
    }

    private static double? TryGetWavDurationSeconds(byte[] wavAudio)
    {
        if (wavAudio.Length < 12
            || !wavAudio.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !wavAudio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        uint byteRate = 0;
        uint dataSize = 0;
        var offset = 12;
        while (offset <= wavAudio.Length - 8)
        {
            var chunkId = wavAudio.AsSpan(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            var contentOffset = offset + 8;
            if (chunkSize > int.MaxValue || contentOffset > wavAudio.Length - (int)chunkSize)
                break;

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 12)
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wavAudio.AsSpan(contentOffset + 8, 4));
            else if (chunkId.SequenceEqual("data"u8))
                dataSize = chunkSize;

            var paddedSize = (long)chunkSize + (chunkSize & 1);
            if (paddedSize > int.MaxValue - contentOffset)
                break;
            offset = contentOffset + (int)paddedSize;
        }

        return byteRate > 0 && dataSize > 0 ? dataSize / (double)byteRate : null;
    }
}
