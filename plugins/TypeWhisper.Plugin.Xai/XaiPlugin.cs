using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

public sealed class XaiPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ITtsProviderPlugin
{
    private const string BaseUrl = "https://api.x.ai";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels";
    private const string SelectedVoiceSettingName = "selectedVoice";
    private const string FetchedVoicesSettingName = "fetchedVoices";
    private const string CustomVoiceIdSettingName = "customVoiceId";
    private const string TtsLowLatencySettingName = "ttsLowLatency";
    private const string TtsTextNormalizationSettingName = "ttsTextNormalization";

    internal const string DefaultLlmModelId = "grok-4.3";
    internal const string DefaultSttModelId = "grok-stt";

    private static readonly IReadOnlyList<PluginModelInfo> SttModels =
    [
        new(DefaultSttModelId, "Grok Speech to Text"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new(DefaultLlmModelId, "Grok 4.3"),
    ];

    private static readonly IReadOnlyList<string> Languages =
    [
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi",
    ];

    private readonly HttpClient _httpClient;
    private readonly Func<byte[], ITtsPlaybackSession> _ttsPlaybackFactory;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedLlmModelId;
    private List<XaiFetchedModel> _fetchedLlmModels = [];
    private string? _selectedVoiceId;
    private List<XaiFetchedVoice> _fetchedVoices = [];
    private string _customVoiceId = "";
    private bool _ttsLowLatency;
    private bool _ttsTextNormalization;

    public XaiPlugin()
        : this(CreateHttpClient())
    {
    }

    internal XaiPlugin(HttpClient httpClient, Func<byte[], ITtsPlaybackSession>? ttsPlaybackFactory = null)
    {
        _httpClient = httpClient;
        _ttsPlaybackFactory = ttsPlaybackFactory
            ?? (pcm => new XaiPcmTtsPlaybackSession(pcm, XaiTtsConfiguration.SampleRate));
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.xai";
    public string PluginName => "xAI / Grok";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _selectedModelId = NormalizeSttModelId(host.GetSetting<string>(SelectedModelSettingName));
        _selectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName) ?? DefaultLlmModelId;
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<XaiFetchedModel>>(FetchedLlmModelsSettingName) ?? []);
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        _fetchedVoices = NormalizeFetchedVoices(
            host.GetSetting<List<XaiFetchedVoice>>(FetchedVoicesSettingName) ?? []);
        _customVoiceId = host.GetSetting<string>(CustomVoiceIdSettingName)?.Trim() ?? "";
        _ttsLowLatency = host.GetSetting<bool?>(TtsLowLatencySettingName) ?? false;
        _ttsTextNormalization = host.GetSetting<bool?>(TtsTextNormalizationSettingName) ?? false;

        NormalizeSelectedLlmModel(persist: false);
        NormalizeSelectedVoice(persist: false);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new XaiSettingsView(this);

    // ITranscriptionEnginePlugin

    public string ProviderId => "xai";
    public string ProviderDisplayName => "xAI / Grok";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => SttModels;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public IReadOnlyList<string> SupportedLanguages => Languages;

    public void SelectModel(string modelId)
    {
        _selectedModelId = NormalizeSttModelId(modelId);
        _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("xAI STT does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        using var form = new MultipartFormDataContent();
        var normalizedLanguage = NormalizeLanguage(language);
        if (normalizedLanguage is not null)
        {
            form.Add(new StringContent("true"), "format");
            form.Add(new StringContent(normalizedLanguage), "language");
        }

        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/stt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = form;

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseSttResponse(json, normalizedLanguage);
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        return await XaiStreamingSession.ConnectAsync(_apiKey!, NormalizeLanguage(language), ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "xAI / Grok";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList()
            : FallbackLlmModels;

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels.First().Id
            : model;
        var client = new XaiResponsesClient(_httpClient, BaseUrl, _apiKey!);
        return await client.ProcessAsync(systemPrompt, userText, modelId, ct);
    }

    // ITtsProviderPlugin

    public IReadOnlyList<PluginVoiceInfo> AvailableVoices =>
        _fetchedVoices.Count > 0
            ? _fetchedVoices.Select(v => new PluginVoiceInfo(v.VoiceId, v.DisplayName, v.Language)).ToList()
            : XaiTtsConfiguration.FallbackVoices;

    public string? SelectedVoiceId =>
        !string.IsNullOrWhiteSpace(_customVoiceId)
            ? _customVoiceId
            : _selectedVoiceId ?? XaiTtsConfiguration.DefaultVoiceId;

    public string? SettingsSummary
    {
        get
        {
            var voice = AvailableVoices.FirstOrDefault(v => v.Id == SelectedVoiceId)?.DisplayName
                ?? SelectedVoiceId
                ?? XaiTtsConfiguration.DefaultVoiceId;
            var latency = _ttsLowLatency ? "low latency" : "quality";
            return $"Voice: {voice}; {latency}";
        }
    }

    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
        _host?.NotifyCapabilitiesChanged();
    }

    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return XaiInactiveTtsPlaybackSession.Instance;

        var body = XaiTtsConfiguration.CreateRequestBody(
            text,
            SelectedVoiceId,
            NormalizeTtsLanguage(request.Language),
            _ttsLowLatency,
            _ttsTextNormalization);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/tts");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Content = XaiJson.CreateJsonContent(body);

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, httpRequest, ct);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        return _ttsPlaybackFactory(pcm);
    }

    // Settings support

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<XaiFetchedModel> FetchedLlmModels => _fetchedLlmModels;
    internal IReadOnlyList<XaiFetchedVoice> FetchedVoices => _fetchedVoices;
    internal string CustomVoiceId => _customVoiceId;
    internal bool TtsLowLatency => _ttsLowLatency;
    internal bool TtsTextNormalization => _ttsTextNormalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
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

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = SupportedModels.FirstOrDefault()?.Id ?? modelId;

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetFetchedLlmModels(List<XaiFetchedModel> models)
    {
        _fetchedLlmModels = NormalizeFetchedLlmModels(models);
        _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<XaiFetchedModel>> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Select(e => new XaiFetchedModel(
                    GetString(e, "id") ?? "",
                    GetString(e, "owned_by")))
                .Where(model => IsLlmModel(model.Id))
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return [];
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal void SetFetchedVoices(List<XaiFetchedVoice> voices)
    {
        _fetchedVoices = NormalizeFetchedVoices(voices);
        _host?.SetSetting(FetchedVoicesSettingName, _fetchedVoices);
        NormalizeSelectedVoice(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<XaiFetchedVoice>> FetchVoicesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/tts/voices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("voices", out var voicesEl)
                || voicesEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return voicesEl.EnumerateArray()
                .Select(e => new XaiFetchedVoice(
                    GetString(e, "voice_id") ?? "",
                    GetString(e, "name"),
                    GetString(e, "language")))
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return [];
        }
    }

    internal void SetCustomVoiceId(string voiceId)
    {
        _customVoiceId = voiceId.Trim();
        _host?.SetSetting(CustomVoiceIdSettingName, _customVoiceId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTtsLowLatency(bool enabled)
    {
        _ttsLowLatency = enabled;
        _host?.SetSetting(TtsLowLatencySettingName, enabled);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTtsTextNormalization(bool enabled)
    {
        _ttsTextNormalization = enabled;
        _host?.SetSetting(TtsTextNormalizationSettingName, enabled);
        _host?.NotifyCapabilitiesChanged();
    }

    internal static PluginTranscriptionResult ParseSttResponse(string json, string? fallbackLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = GetString(root, "text")?.Trim() ?? "";
        var language = GetString(root, "language");
        var duration = TryGetDouble(root, "duration", out var durationValue) ? durationValue : 0;
        var segments = new List<PluginTranscriptionSegment>();

        if (root.TryGetProperty("words", out var wordsEl)
            && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                var wordText = GetString(wordEl, "text") ?? "";
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

        return new PluginTranscriptionResult(text, language ?? fallbackLanguage ?? "", duration)
        {
            Segments = segments
        };
    }

    internal static bool IsLlmModel(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var lowered = id.ToLowerInvariant();
        var excluded = new[] { "stt", "tts", "voice", "image", "embedding" };
        return !excluded.Any(lowered.Contains);
    }

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        if (_selectedLlmModelId is null
            || available.All(model => !string.Equals(model.Id, _selectedLlmModelId, StringComparison.Ordinal)))
        {
            _selectedLlmModelId = available.First().Id;
            if (persist)
                _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
        }
    }

    private void NormalizeSelectedVoice(bool persist)
    {
        var available = AvailableVoices;
        if (available.Count == 0)
            return;

        if (_selectedVoiceId is null
            || available.All(voice => !string.Equals(voice.Id, _selectedVoiceId, StringComparison.Ordinal)))
        {
            _selectedVoiceId = available.First().Id;
            if (persist)
                _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
        }
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string NormalizeSttModelId(string? modelId) =>
        SttModels.Any(model => model.Id == modelId) ? modelId! : DefaultSttModelId;

    private static string? NormalizeVoiceId(string? voiceId) =>
        string.IsNullOrWhiteSpace(voiceId) ? XaiTtsConfiguration.DefaultVoiceId : voiceId.Trim();

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    private static string NormalizeTtsLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();

    private static List<XaiFetchedModel> NormalizeFetchedLlmModels(IEnumerable<XaiFetchedModel> models) =>
        models
            .Where(model => IsLlmModel(model.Id))
            .DistinctBy(model => model.Id)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<XaiFetchedVoice> NormalizeFetchedVoices(IEnumerable<XaiFetchedVoice> voices) =>
        voices
            .Where(voice => !string.IsNullOrWhiteSpace(voice.VoiceId))
            .DistinctBy(voice => voice.VoiceId)
            .OrderBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed record XaiFetchedModel(string Id, string? OwnedBy);

internal sealed record XaiFetchedVoice(string VoiceId, string? Name, string? Language)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? VoiceId : Name.Trim();
}
