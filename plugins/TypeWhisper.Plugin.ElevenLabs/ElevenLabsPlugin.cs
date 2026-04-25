using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

public sealed class ElevenLabsPlugin : ITranscriptionEnginePlugin
{
    internal const string DefaultModelId = "scribe_v2";
    private const string BaseUrl = "https://api.elevenlabs.io";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";

    private static readonly char[] InvalidKeytermCharacters = ['<', '>', '{', '}', '[', ']', '\\'];

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

    public ElevenLabsPlugin()
        : this(CreateHttpClient())
    {
    }

    internal ElevenLabsPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.elevenlabs";
    public string PluginName => "ElevenLabs";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync(ApiKeySecretName);
        _selectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new ElevenLabsSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "elevenlabs";
    public string ProviderDisplayName => "ElevenLabs";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        ModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = true }).ToList();

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public IReadOnlyList<string> SupportedLanguages => Languages;

    public void SelectModel(string modelId)
    {
        var entry = ResolveModelEntry(modelId);
        _selectedModelId = entry.Id;
        _host?.SetSetting(SelectedModelSettingName, entry.Id);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

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

        foreach (var term in ExtractKeyterms(prompt))
            form.Add(new StringContent(term), "keyterms");

        request.Content = form;

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ElevenLabs API error {(int)response.StatusCode}: {json}");

        return ParseRestResponse(json, NormalizeLanguage(language));
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured || _selectedModelId is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        var entry = ResolveModelEntry(_selectedModelId);
        return await ElevenLabsStreamingSession.ConnectAsync(
            _apiKey!,
            entry.RealtimeModelId,
            NormalizeLanguage(language),
            ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
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
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/user");
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
                    || !TryGetDouble(wordEl, "end", out var end))
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
        foreach (var part in prompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var term = part.Trim();
            if (term.Length == 0
                || term.Length >= 50
                || term.IndexOfAny(InvalidKeytermCharacters) >= 0
                || term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5
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
