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
    internal const string DefaultModelId = "scribe_v2";
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

    private static readonly char[] InvalidKeytermCharacters = ['<', '>', '{', '}', '[', ']', '\\'];
    private static readonly char[] KeytermSeparators = [',', '\r', '\n'];
    private static readonly DictionaryTermsBudget DictionaryBudget = new(
        MaxTerms: 1000,
        MaxCharsPerTerm: 49,
        MaxWordsPerTerm: 5);

    private static readonly IReadOnlyList<ElevenLabsModelEntry> ModelEntries =
    [
        new(DefaultModelId, "Scribe v2", "scribe_v2", "scribe_v2_realtime"),
    ];

    private static readonly IReadOnlyList<string> Languages =
    [
        "af", "am", "ar", "as", "az", "ba", "be", "bg", "bn", "bo",
        "br", "bs", "ca", "cs", "cy", "da", "de", "el", "en", "es",
        "et", "eu", "fa", "fi", "fo", "fr", "gl", "gu", "ha", "haw",
        "he", "hi", "hr", "ht", "hu", "hy", "id", "is", "it", "ja",
        "jw", "ka", "kk", "km", "kn", "ko", "la", "lb", "ln", "lo",
        "lt", "lv", "mg", "mi", "mk", "ml", "mn", "mr", "ms", "mt",
        "my", "ne", "nl", "nn", "no", "oc", "pa", "pl", "ps", "pt",
        "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so", "sq",
        "sr", "su", "sv", "sw", "ta", "te", "tg", "th", "tk", "tl",
        "tr", "tt", "uk", "ur", "uz", "vi", "vo", "yi", "yo", "yue",
        "zh",
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
    public string PluginVersion => "1.1.0";

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
        ModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = true, LanguageCodes = Languages, LanguageCount = Languages.Count }).ToList();

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
    public bool SupportsStreaming => _transcriptionMode == ElevenLabsTranscriptionMode.Automatic && !_tagAudioEvents && _speakerCount == 1;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
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
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => Languages;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var entry = ResolveModelEntry(modelId);
        _host?.SetSetting(SelectedModelSettingName, entry.Id);
        _selectedModelId = entry.Id;
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

        form.Add(new StringContent(FormatBoolean(_tagAudioEvents)), "tag_audio_events");
        form.Add(new StringContent(FormatBoolean(_noVerbatim)), "no_verbatim");

        if (_speakerCount != AutomaticSpeakerCount)
            form.Add(new StringContent(_speakerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)), "num_speakers");

        foreach (var term in ExtractKeyterms(prompt))
            form.Add(new StringContent(term), "keyterms");

        request.Content = form;

        using var response = await SendAsync(request, ct);
        ThrowIfRejected(response);
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

        var entry = ResolveModelEntry(_selectedModelId);
        return await ElevenLabsStreamingSession.ConnectAsync(
            _apiKey!,
            entry.RealtimeModelId,
            NormalizeLanguage(language),
            _noVerbatim,
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
            if (IsUserReadPermissionOnly(body)) return;
        }
        ThrowIfRejected(response);
    }

    internal static bool IsUserReadPermissionOnly(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var detail = doc.RootElement.GetProperty("detail");
            return detail.GetProperty("status").GetString() == "missing_permissions"
                && string.Equals(detail.GetProperty("message").GetString()?.Trim(),
                    "The API key you used is missing the permission user_read to execute this operation.", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await _httpClient.SendAsync(request, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("ElevenLabs timed out.", PluginRequestFailureKind.Timeout); }
        catch (HttpRequestException)
        { throw new PluginRequestException("Could not reach ElevenLabs.", PluginRequestFailureKind.Network); }
    }

    private static void ThrowIfRejected(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
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
        var detectedLanguage = root.TryGetProperty("language_code", out var langEl)
            ? langEl.GetString()
            : fallbackLanguage;

        var duration = 0.0;
        var segments = new List<PluginTranscriptionSegment>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                if (wordEl.TryGetProperty("type", out var typeEl)
                    && !string.Equals(typeEl.GetString(), "word", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var wordText = wordEl.TryGetProperty("text", out var wordTextEl)
                    ? wordTextEl.GetString() ?? ""
                    : "";

                if (string.IsNullOrWhiteSpace(wordText)
                    || !TryGetDouble(wordEl, "start", out var start)
                    || !TryGetDouble(wordEl, "end", out var end)
                    || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
                {
                    continue;
                }

                segments.Add(new PluginTranscriptionSegment(wordText, start, end));
                duration = Math.Max(duration, end);
            }
        }

        return new PluginTranscriptionResult(text, detectedLanguage, duration, NoSpeechProbability: null)
        {
            Segments = segments
        };
    }

    internal static IReadOnlyList<string> ExtractKeyterms(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var part in prompt.Split(
            KeytermSeparators,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
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
        string RealtimeModelId);
}
