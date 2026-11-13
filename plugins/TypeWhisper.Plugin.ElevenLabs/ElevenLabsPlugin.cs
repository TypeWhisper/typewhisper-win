// Protocol port from the legacy Windows ElevenLabs provider and the macOS ElevenLabs plugin.
// Independent portable implementation; legacy sources and settings remain untouched.
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

internal enum ElevenLabsTranscriptionMode
{
    Automatic,
    RestOnly
}

/// <summary>
/// Provides eleven labs plugin behavior.
/// </summary>
public sealed partial class ElevenLabsPlugin : ITranscriptionEnginePlugin, IApiKeyPlugin, IPluginTextSettings
{
    internal const string ScribeV3ModelId = "scribe_v3";
    internal const string ScribeV2ModelId = "scribe_v2";
    // Scribe v3 costs the same as Scribe v2 and makes fewer errors.
    internal const string DefaultModelId = ScribeV3ModelId;
    internal const int AutomaticSpeakerCount = 0;
    internal const int DefaultSpeakerCount = 1;
    private const string BaseUrl = "https://api.elevenlabs.io";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string TranscriptionModeSettingName = "transcriptionMode";
    private const string TagAudioEventsSettingName = "tagAudioEvents";
    private const string NoVerbatimSettingName = "noVerbatim";
    private const string SpeakerCountSettingName = "numSpeakers";
    private const string UseDictionaryTermsSettingName = "useDictionaryTerms";
    private const string MultilangModeSettingName = "multilangMode";

    private static readonly char[] InvalidKeytermCharacters = ['<', '>', '{', '}', '[', ']', '\\'];
    private static readonly char[] KeytermSeparators = [',', '\r', '\n'];
    private static readonly DictionaryTermsBudget DictionaryBudget = new(
        MaxTerms: 1000,
        MaxCharsPerTerm: 49,
        MaxWordsPerTerm: 5);

    internal const string MedicalModelId = "scribe_v2_medical";

    // Each streaming model has a realtime variant on the same WebSocket endpoint. Scribe v2 Medical is
    // a batch model with Scribe v2's request shape and price; it has no realtime variant.
    private static readonly IReadOnlyList<ElevenLabsModelEntry> ModelEntries =
    [
        new(ScribeV3ModelId, "Scribe v3", ScribeV3ModelId, "scribe_v3_live"),
        new(ScribeV2ModelId, "Scribe v2", ScribeV2ModelId, "scribe_v2_realtime"),
        new(MedicalModelId, "Scribe v2 Medical", MedicalModelId, RealtimeModelId: null),
    ];

    // Scribe's documented languages and the further codes its API accepts.
    // It rejects "jw", "haw" and "vo"; Javanese is "jv".
    private static readonly IReadOnlyList<string> Languages =
    [
        "af", "am", "ar", "as", "ast", "az", "ba", "be", "bg", "bn",
        "bo", "br", "bs", "ca", "ceb", "cs", "cy", "da", "de", "el",
        "en", "es", "et", "eu", "fa", "fi", "fo", "fr", "ga", "gl",
        "gu", "ha", "he", "hi", "hr", "ht", "hu", "hy", "id", "ig",
        "is", "it", "ja", "jv", "ka", "kea", "kk", "km", "kn", "ko",
        "ku", "ky", "la", "lb", "lg", "ln", "lo", "lt", "luo", "lv",
        "mg", "mi", "mk", "ml", "mn", "mr", "ms", "mt", "my", "ne",
        "nl", "nn", "no", "nso", "oc", "or", "pa", "pl", "ps", "pt",
        "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so", "sq",
        "sr", "su", "sv", "sw", "ta", "te", "tg", "th", "tk", "tl",
        "tr", "tt", "uk", "umb", "ur", "uz", "vi", "wo", "xh", "yi",
        "yo", "yue", "zh", "zu",
    ];

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private ElevenLabsTranscriptionMode _transcriptionMode = ElevenLabsTranscriptionMode.Automatic;
    private bool _tagAudioEvents;
    private bool _noVerbatim = true;
    private int _speakerCount = DefaultSpeakerCount;
    private bool _useDictionaryTerms = true;
    private bool _multilangMode;

    /// <summary>
    /// Initializes a new instance of the ElevenLabsPlugin class.
    /// </summary>
    public ElevenLabsPlugin()
        : this(CreateHttpClient())
    {
    }

    internal ElevenLabsPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.elevenlabs";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "ElevenLabs";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.3.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync(ApiKeySecretName);
        _selectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        _transcriptionMode = NormalizeTranscriptionMode(host.GetSetting<string>(TranscriptionModeSettingName));
        _tagAudioEvents = host.GetSetting<bool?>(TagAudioEventsSettingName) ?? false;
        _noVerbatim = host.GetSetting<bool?>(NoVerbatimSettingName) ?? true;
        _speakerCount = NormalizeSpeakerCount(
            host.GetSetting<int?>(SpeakerCountSettingName) ?? DefaultSpeakerCount);
        _useDictionaryTerms = host.GetSetting<bool?>(UseDictionaryTermsSettingName) ?? true;
        _multilangMode = host.GetSetting<bool?>(MultilangModeSettingName) ?? false;
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


    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "elevenlabs";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "ElevenLabs";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        ModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = m.Id == DefaultModelId, LanguageCodes = Languages, LanguageCount = Languages.Count }).ToList();

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => false;
    /// <summary>
    /// Gets whether the provider supports live streaming transcription.
    /// </summary>
    public bool SupportsStreaming =>
        _transcriptionMode == ElevenLabsTranscriptionMode.Automatic && !_tagAudioEvents && _speakerCount == 1
        && ModelStreams(_selectedModelId);
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsStructuredDictionaryTerms => true;

    /// <summary>
    /// Gets whether TypeWhisper may add active dictionary terms to transcription prompts.
    /// </summary>
    public bool SupportsDictionaryTerms => _useDictionaryTerms;
    /// <summary>
    /// Gets the provider limits for REST keyterms.
    /// </summary>
    public DictionaryTermsBudget DictionaryTermsBudget => DictionaryBudget;
    /// <summary>
    /// Gets whether realtime streaming is suitable for the supplied prompt.
    /// </summary>
    public bool SupportsStreamingForPrompt(string? prompt) =>
        SupportsStreaming && ExtractKeyterms(prompt).Count == 0;
    /// <summary>
    /// Realtime accepts keyterms, but with them it often wraps the whole transcript in quotation marks.
    /// With dictionary terms the live stream therefore runs without them as a preview, and the batch API
    /// transcribes the complete recording with the terms for the final text.
    /// </summary>
    public bool SupportsStreamingPreviewForPrompt(string? prompt) =>
        SupportsStreaming && ExtractKeyterms(prompt).Count > 0;
    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => Languages;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var entry = ResolveModelEntry(modelId);
        var streamedBefore = SupportsStreaming;
        _host?.SetSetting(SelectedModelSettingName, entry.Id);
        _selectedModelId = entry.Id;
        if (SupportsStreaming != streamedBefore)
            _host?.NotifyCapabilitiesChanged();
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        if (translate) throw new NotSupportedException("ElevenLabs does not support native translation.");
        ct.ThrowIfCancellationRequested();
        var entry = ResolveModelEntry(_selectedModelId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/speech-to-text");
        request.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);

        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "audio.wav");
        form.Add(new StringContent(entry.RestModelId), "model_id");

        if (NormalizeLanguage(language) is { } normalizedLanguage)
            form.Add(new StringContent(normalizedLanguage), "language_code");

        form.Add(new StringContent("word"), "timestamps_granularity");
        form.Add(new StringContent(FormatBoolean(_tagAudioEvents)), "tag_audio_events");
        form.Add(new StringContent(FormatBoolean(_noVerbatim)), "no_verbatim");

        if (_speakerCount != AutomaticSpeakerCount)
            form.Add(new StringContent(_speakerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)), "num_speakers");

        foreach (var term in ExtractKeyterms(prompt))
            form.Add(new StringContent(term), "keyterms");

        request.Content = form;

        using var response = await SendAsync(request, ct);
        await ThrowIfRejectedAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        ct.ThrowIfCancellationRequested();
        return ParseRestResponse(json, NormalizeLanguage(language));
    }

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");
        if (!SupportsStreaming)
            throw new NotSupportedException("Realtime streaming is disabled by the selected transcription mode.");

        // SupportsStreaming guarantees a realtime variant for the selected model.
        return await ElevenLabsStreamingSession.ConnectAsync(
            _apiKey!,
            RealtimeModelIdFor(_selectedModelId)!,
            NormalizeLanguage(language),
            _noVerbatim,
            _multilangMode && SupportsMultilangMode(_selectedModelId),
            ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal ElevenLabsTranscriptionMode TranscriptionMode => _transcriptionMode;
    internal bool TagAudioEvents => _tagAudioEvents;
    internal bool NoVerbatim => _noVerbatim;
    internal int SpeakerCount => _speakerCount;
    internal bool UseDictionaryTerms => _useDictionaryTerms;
    internal bool MultilangMode => _multilangMode;

    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (normalized is null) await host.DeleteSecretAsync(ApiKeySecretName);
        else await host.StoreSecretAsync(ApiKeySecretName, normalized);
        _apiKey = normalized;
        host.NotifyCapabilitiesChanged();
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new PluginRequestException("Configure an API key first.", PluginRequestFailureKind.Configuration);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/user");
        request.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // A Speech-to-Text-only key may legitimately lack access to the user profile.
            var body = await response.Content.ReadAsStringAsync(ct);
            if (IsUserReadPermissionOnly(body))
                throw new PluginRequestException("The key is saved, but speech access could not be verified because user_read is disabled. Try a dictation to verify Speech-to-Text access.", PluginRequestFailureKind.Configuration);
            ThrowUnauthorized(body);
        }
        await ThrowIfRejectedAsync(response, ct);
    }

    internal static bool IsUserReadPermissionOnly(string json)
    {
        var (status, message) = ReadErrorDetail(json);
        return status == "missing_permissions"
            && string.Equals(message?.Trim(),
                "The API key you used is missing the permission user_read to execute this operation.", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Status, string? Message) ReadErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("detail", out var detail)
                || detail.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (
                detail.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null,
                detail.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null);
        }
        catch (JsonException) { return (null, null); }
    }

    // ElevenLabs answers HTTP 401 not only for a wrong key but also when the key's own credit
    // quota is used up or it lacks a permission. Provider texts are not passed on; the status
    // selects a fixed message instead of reporting every 401 as an invalid key.
    private static void ThrowUnauthorized(string body)
    {
        var (status, _) = ReadErrorDetail(body);
        throw status switch
        {
            "quota_exceeded" => new PluginRequestException(
                "This ElevenLabs API key has used up its credit quota. Raise the key's limit or add credits in your ElevenLabs account.",
                PluginRequestFailureKind.Permission, 401),
            "missing_permissions" => new PluginRequestException(
                "This ElevenLabs API key lacks the Speech to Text permission.",
                PluginRequestFailureKind.Permission, 401),
            _ => new PluginRequestException("ElevenLabs rejected the API key.", PluginRequestFailureKind.Authentication, 401),
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await _httpClient.SendAsync(request, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("ElevenLabs timed out.", PluginRequestFailureKind.Timeout); }
        catch (HttpRequestException)
        { throw new PluginRequestException("Could not reach ElevenLabs.", PluginRequestFailureKind.Network); }
    }

    private static async Task ThrowIfRejectedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        if (status == 401)
            ThrowUnauthorized(await response.Content.ReadAsStringAsync(ct));
        var kind = status switch
        {
            401 => PluginRequestFailureKind.Authentication, 403 => PluginRequestFailureKind.Permission,
            429 => PluginRequestFailureKind.RateLimit, 413 => PluginRequestFailureKind.RequestTooLarge,
            >= 500 => PluginRequestFailureKind.ServerError, _ => PluginRequestFailureKind.InvalidRequest
        };
        throw new PluginRequestException("ElevenLabs rejected the request.", kind, status);
    }

    internal void SetTranscriptionMode(ElevenLabsTranscriptionMode mode)
    {
        if (_transcriptionMode == mode)
            return;

        _host?.SetSetting(TranscriptionModeSettingName, FormatTranscriptionMode(mode));
        _transcriptionMode = mode;
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTagAudioEvents(bool enabled)
    {
        if (_tagAudioEvents == enabled)
            return;

        _host?.SetSetting(TagAudioEventsSettingName, enabled);
        _tagAudioEvents = enabled;
    }

    internal void SetNoVerbatim(bool enabled)
    {
        if (_noVerbatim == enabled)
            return;

        _host?.SetSetting(NoVerbatimSettingName, enabled);
        _noVerbatim = enabled;
    }

    internal void SetSpeakerCount(int speakerCount)
    {
        var normalized = NormalizeSpeakerCount(speakerCount);
        if (_speakerCount == normalized)
            return;

        _host?.SetSetting(SpeakerCountSettingName, normalized);
        _speakerCount = normalized;
    }

    internal void SetMultilangMode(bool enabled)
    {
        if (_multilangMode == enabled)
            return;

        _host?.SetSetting(MultilangModeSettingName, enabled);
        _multilangMode = enabled;
    }

    internal void SetUseDictionaryTerms(bool enabled)
    {
        if (_useDictionaryTerms == enabled)
            return;

        _host?.SetSetting(UseDictionaryTermsSettingName, enabled);
        _useDictionaryTerms = enabled;
        _host?.NotifyCapabilitiesChanged();
    }

    internal static PluginTranscriptionResult ParseRestResponse(string json, string? fallbackLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl)
            ? textEl.GetString()?.Trim() ?? ""
            : "";
        var detectedLanguage = root.TryGetProperty("language_code", out var langEl) && langEl.ValueKind == JsonValueKind.String
            ? ElevenLabsLanguageCode.AppCode(langEl.GetString()) ?? fallbackLanguage
            : fallbackLanguage;

        var entries = new List<ElevenLabsTranscriptEntry>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                if (ReadEntry(wordEl) is { } entry)
                    entries.Add(entry);
            }
        }

        var segments = ElevenLabsSegmentBuilder.Build(entries);
        var duration = segments.Count == 0 ? 0 : segments.Max(segment => segment.End);
        return new PluginTranscriptionResult(text, detectedLanguage, duration, NoSpeechProbability: null)
        {
            Segments = segments
        };
    }

    // Scribe returns one entry per word, per space between words and per tagged audio event.
    // Entries with missing or inconsistent times are dropped; spacing keeps its text so the
    // segment texts join to the transcript.
    private static ElevenLabsTranscriptEntry? ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("text", out var textEl)
            || textEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var kind = element.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString() switch
            {
                "spacing" => ElevenLabsEntryKind.Spacing,
                "audio_event" => ElevenLabsEntryKind.AudioEvent,
                "word" => ElevenLabsEntryKind.Word,
                _ => (ElevenLabsEntryKind?)null,
            }
            : ElevenLabsEntryKind.Word;
        if (kind is null)
            return null;

        var text = textEl.GetString() ?? "";
        if (kind == ElevenLabsEntryKind.Spacing)
            return new(kind.Value, text, 0, 0);

        if (string.IsNullOrWhiteSpace(text)
            || !TryGetDouble(element, "start", out var start)
            || !TryGetDouble(element, "end", out var end)
            || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
        {
            return null;
        }

        return new(kind.Value, text, start, end);
    }

    internal static IReadOnlyList<string> ExtractKeyterms(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var part in PluginDictionaryTerms.ParsePrompt(prompt, KeytermSeparators))
        {
            var term = part.Trim();
            if (term.Length == 0
                || term.Length >= 50
                || term.IndexOfAny(InvalidKeytermCharacters) >= 0
                || term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 5
                || !seen.Add(term))
            {
                continue;
            }

            terms.Add(term);
            if (terms.Count == 1000)
                break;
        }

        return terms;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language;

    internal static string? RealtimeModelIdFor(string? modelId) =>
        ModelEntries.FirstOrDefault(m => m.Id == modelId)?.RealtimeModelId;

    internal static bool ModelStreams(string? modelId) => RealtimeModelIdFor(modelId) is not null;

    // Only Scribe v3 Live takes multilang_mode; Scribe v2 Realtime rejects the session with invalid_request.
    internal static bool SupportsMultilangMode(string? modelId) => modelId == ScribeV3ModelId;

    // Selections of retired models, such as the deprecated Scribe v1, move to the default model.
    // A saved Scribe v2 selection stays.
    private static string NormalizeModelId(string? modelId) =>
        ModelEntries.Any(m => m.Id == modelId) ? modelId! : DefaultModelId;

    private static ElevenLabsTranscriptionMode NormalizeTranscriptionMode(string? mode) =>
        mode switch
        {
            "restOnly" => ElevenLabsTranscriptionMode.RestOnly,
            _ => ElevenLabsTranscriptionMode.Automatic
        };

    private static string FormatTranscriptionMode(ElevenLabsTranscriptionMode mode) =>
        mode == ElevenLabsTranscriptionMode.RestOnly ? "restOnly" : "automatic";

    private static int NormalizeSpeakerCount(int speakerCount) =>
        speakerCount is AutomaticSpeakerCount or >= 1 and <= 32
            ? speakerCount
            : DefaultSpeakerCount;

    private static string FormatBoolean(bool value) => value ? "true" : "false";

    private static ElevenLabsModelEntry ResolveModelEntry(string modelId) =>
        ModelEntries.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

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

    private sealed record ElevenLabsModelEntry(
        string Id,
        string DisplayName,
        string RestModelId,
        string? RealtimeModelId);
}
