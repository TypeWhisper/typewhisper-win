using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Meta;

/// <summary>
/// Provides Meta Model API transcription and language-model capabilities.
/// </summary>
public sealed class MetaPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ILlmRequestHedgingSupport
{
    internal const string BaseUrl = "https://api.meta.ai";
    internal const string DefaultTranscriptionModelId = "muse-voice-transcribe-1.0";
    internal const string DefaultLlmModelId = "muse-spark-1.2";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedTranscriptionModelSettingName = "selectedModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels";
    private const string ReasoningEffortSettingName = "reasoningEffort";
    private static readonly DictionaryTermsBudget KeywordBudget = new(
        MaxTerms: 100,
        MaxCharsPerTerm: 100,
        MaxWordsPerTerm: 8,
        MaxTotalChars: 600);

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new(DefaultLlmModelId, "Muse Spark 1.2"),
        new("muse-spark-1.1", "Muse Spark 1.1"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackTranscriptionModels =
    [
        new(DefaultTranscriptionModelId, "Muse Voice Transcribe 1.0"),
    ];

    private static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ar"] = "Arabic",
            ["bn"] = "Bengali",
            ["nl"] = "Dutch",
            ["en"] = "English",
            ["fr"] = "French",
            ["de"] = "German",
            ["he"] = "Hebrew",
            ["iw"] = "Hebrew",
            ["hi"] = "Hindi",
            ["id"] = "Indonesian",
            ["it"] = "Italian",
            ["ja"] = "Japanese",
            ["kn"] = "Kannada",
            ["ko"] = "Korean",
            ["ms"] = "Malay",
            ["zh"] = "Mandarin Chinese",
            ["cmn"] = "Mandarin Chinese",
            ["mr"] = "Marathi",
            ["pl"] = "Polish",
            ["pt"] = "Portuguese",
            ["es"] = "Spanish",
            ["tl"] = "Tagalog",
            ["fil"] = "Tagalog",
            ["ta"] = "Tamil",
            ["te"] = "Telugu",
            ["th"] = "Thai",
            ["tr"] = "Turkish",
            ["vi"] = "Vietnamese",
        };

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedLlmModelId;
    private string _reasoningEffort = "medium";
    private List<MetaFetchedModel> _fetchedLlmModels = [];
    private List<MetaFetchedModel> _fetchedTranscriptionModels = [];

    /// <summary>
    /// Initializes a new Meta Model API plugin instance.
    /// </summary>
    public MetaPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
    {
    }

    internal MetaPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.meta";

    /// <inheritdoc />
    public string PluginName => "Meta Model API";

    /// <inheritdoc />
    public string PluginVersion => "1.0.0";

    /// <inheritdoc />
    public bool SupportsRequestHedging => true;

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _fetchedLlmModels = NormalizeModels(
            host.GetSetting<List<MetaFetchedModel>>(FetchedLlmModelsSettingName) ?? [],
            IsLlmModel);
        _fetchedTranscriptionModels = NormalizeModels(
            host.GetSetting<List<MetaFetchedModel>>(FetchedTranscriptionModelsSettingName) ?? [],
            IsTranscriptionModel);
        _selectedModelId = NormalizeSelection(
            host.GetSetting<string>(SelectedTranscriptionModelSettingName),
            TranscriptionModels,
            DefaultTranscriptionModelId);
        _selectedLlmModelId = NormalizeSelection(
            host.GetSetting<string>(SelectedLlmModelSettingName),
            SupportedModels,
            DefaultLlmModelId);
        _reasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingName));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public UserControl? CreateSettingsView() => new MetaSettingsView(this);

    /// <inheritdoc />
    public string ProviderId => "meta";

    /// <inheritdoc />
    public string ProviderDisplayName => "Meta Model API";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels
                .Select(model => new PluginModelInfo(model.Id, DisplayName(model.Id)))
                .ToList()
            : FallbackTranscriptionModels;

    /// <inheritdoc />
    public string? SelectedModelId => _selectedModelId;

    /// <inheritdoc />
    public bool SupportsTranslation => false;

    /// <inheritdoc />
    public bool SupportsStreaming => IsConfigured && _selectedModelId is not null;

    /// <inheritdoc />
    public bool SupportsDictionaryTerms => IsConfigured;

    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => KeywordBudget;

    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt) =>
        SupportsStreaming && string.IsNullOrWhiteSpace(prompt);

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => LanguageNames.Keys
        .Where(code => code.Length == 2 && code != "iw")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(code => code, StringComparer.Ordinal)
        .ToList();

    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        if (TranscriptionModels.All(model =>
                !string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown transcription model: {modelId}", nameof(modelId));
        }

        _selectedModelId = modelId;
        _host?.SetSetting(SelectedTranscriptionModelSettingName, modelId);
    }

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(
            wavAudio,
            string.IsNullOrWhiteSpace(language) ? [] : [language],
            translate,
            prompt,
            ct);

    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        EnsureTranscriptionConfigured();
        if (translate)
            throw new NotSupportedException("Muse Voice Transcribe does not support translation.");
        if (wavAudio.Length == 0)
            throw new ArgumentException("No WAV audio bytes were provided.", nameof(wavAudio));

        var normalizedLanguageHints = NormalizeLanguageHints(languageHints);
        var keywords = ParseKeywords(prompt);
        var requestJson = CreateTranscriptionRequestJson(
            _selectedModelId!,
            normalizedLanguageHints,
            keywords);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(requestJson, Encoding.UTF8, "application/json"), "request");
        var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "audio", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/asr/transcribe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json, FirstLanguageCode(languageHints));
    }

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(
            string.IsNullOrWhiteSpace(language) ? [] : [language],
            ct);

    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct)
    {
        EnsureTranscriptionConfigured();
        return await MetaRealtimeStreamingSession.ConnectAsync(
            _apiKey!,
            _selectedModelId!,
            NormalizeLanguageHints(languageHints),
            ct);
    }

    /// <inheritdoc />
    public string ProviderName => "Meta Model API";

    /// <inheritdoc />
    public bool IsAvailable => IsConfigured;

    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels
                .Select(model => new PluginModelInfo(model.Id, DisplayName(model.Id)))
                .ToList()
            : FallbackLlmModels;

    /// <inheritdoc />
    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);
        }

        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels[0].Id
            : model;
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            maxOutputTokenParameter: "max_completion_tokens",
            reasoningEffort: _reasoningEffort,
            temperature: null);
    }

    internal string? ApiKey => _apiKey;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal string ReasoningEffort => _reasoningEffort;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal bool IsUiAutomation => _host?.IsUiAutomation == true;
    internal int FetchedLlmModelCount => _fetchedLlmModels.Count;
    internal int FetchedTranscriptionModelCount => _fetchedTranscriptionModels.Count;

    internal async Task SetApiKeyAsync(string? apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);
        _apiKey = normalized;

        if (_host is null)
            return;

        if (normalized is null)
            await _host.DeleteSecretAsync(ApiKeySecretName);
        else
            await _host.StoreSecretAsync(ApiKeySecretName, normalized);

        if (changed)
        {
            _fetchedLlmModels = [];
            _fetchedTranscriptionModels = [];
            _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
            _host.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
            NormalizeSelections(persist: true);
            _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model =>
                !string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown language model: {modelId}", nameof(modelId));
        }

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
    }

    internal void SetReasoningEffort(string reasoningEffort)
    {
        _reasoningEffort = NormalizeReasoningEffort(reasoningEffort);
        _host?.SetSetting(ReasoningEffortSettingName, _reasoningEffort);
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = CreateModelsRequest(apiKey);
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

    internal async Task<MetaModelCatalog?> RefreshAvailableModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;

        using var request = CreateModelsRequest(_apiKey!);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var models = data.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new MetaFetchedModel(
                    element.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    element.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList();
            var llmModels = NormalizeModels(models, IsLlmModel);
            var transcriptionModels = NormalizeModels(models, IsTranscriptionModel);
            if (llmModels.Count > 0)
                _fetchedLlmModels = llmModels;
            if (transcriptionModels.Count > 0)
                _fetchedTranscriptionModels = transcriptionModels;

            if (_host is not null)
            {
                _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
                _host.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
            }

            NormalizeSelections(persist: true);
            _host?.NotifyCapabilitiesChanged();
            return new MetaModelCatalog(_fetchedLlmModels, _fetchedTranscriptionModels);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Could not refresh Meta model catalog: {ex.Message}");
            return null;
        }
    }

    internal static bool IsLlmModel(string modelId) =>
        modelId.StartsWith("muse-spark-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTranscriptionModel(string modelId) =>
        modelId.StartsWith("muse-voice-transcribe-", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> NormalizeLanguageHints(IEnumerable<string>? languageHints)
    {
        if (languageHints is null)
            return [];

        var normalized = new List<string>();
        foreach (var rawHint in languageHints)
        {
            var hint = rawHint?.Trim();
            if (string.IsNullOrWhiteSpace(hint)
                || hint.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseCode = hint.Split('-', '_')[0];
            var languageName = LanguageNames.GetValueOrDefault(baseCode)
                ?? LanguageNames.Values.FirstOrDefault(name =>
                    name.Equals(hint, StringComparison.OrdinalIgnoreCase));
            if (languageName is not null
                && !normalized.Contains(languageName, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(languageName);
            }
        }

        return normalized;
    }

    internal static IReadOnlyList<string> ParseKeywords(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? []
            : prompt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    internal static string CreateTranscriptionRequestJson(
        string modelId,
        IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["audioEncoding"] = "WAV",
            ["mode"] = "PUSH_TO_TALK",
        };
        if (languageBias.Count > 0)
            body["languageBias"] = languageBias;
        if (keywords.Count > 0)
            body["keywords"] = keywords;

        return JsonSerializer.Serialize(body);
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? requestedLanguage)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var transcript = root.TryGetProperty("transcript", out var transcriptElement)
            ? transcriptElement.GetString()?.Trim() ?? ""
            : "";
        var durationSeconds = root.TryGetProperty("audioDurationMs", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            ? durationElement.GetDouble() / 1000d
            : 0d;
        var segments = new List<PluginTranscriptionSegment>();
        if (root.TryGetProperty("turns", out var turnsElement)
            && turnsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turnsElement.EnumerateArray())
            {
                var text = turn.TryGetProperty("transcript", out var textElement)
                    ? textElement.GetString() ?? ""
                    : "";
                var start = turn.TryGetProperty("startMs", out var startElement)
                    && startElement.ValueKind == JsonValueKind.Number
                    ? startElement.GetDouble() / 1000d
                    : 0d;
                var end = turn.TryGetProperty("endMs", out var endElement)
                    && endElement.ValueKind == JsonValueKind.Number
                    ? endElement.GetDouble() / 1000d
                    : 0d;
                segments.Add(new PluginTranscriptionSegment(text, start, end));
            }
        }

        return new PluginTranscriptionResult(
            transcript,
            requestedLanguage,
            durationSeconds,
            NoSpeechProbability: null)
        {
            Segments = segments,
        };
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private static HttpRequestMessage CreateModelsRequest(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private void EnsureTranscriptionConfigured()
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(_selectedModelId))
        {
            throw new PluginRequestException(
                "API key and transcription model are required",
                PluginRequestFailureKind.Configuration);
        }
    }

    private void NormalizeSelections(bool persist)
    {
        _selectedModelId = NormalizeSelection(
            _selectedModelId,
            TranscriptionModels,
            DefaultTranscriptionModelId);
        _selectedLlmModelId = NormalizeSelection(
            _selectedLlmModelId,
            SupportedModels,
            DefaultLlmModelId);
        if (persist && _host is not null)
        {
            _host.SetSetting(SelectedTranscriptionModelSettingName, _selectedModelId);
            _host.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
        }
    }

    private static string? NormalizeSelection(
        string? selection,
        IReadOnlyList<PluginModelInfo> models,
        string preferredDefault)
    {
        if (!string.IsNullOrWhiteSpace(selection)
            && models.Any(model => model.Id.Equals(selection, StringComparison.OrdinalIgnoreCase)))
        {
            return models.First(model =>
                model.Id.Equals(selection, StringComparison.OrdinalIgnoreCase)).Id;
        }

        return models.FirstOrDefault(model =>
                model.Id.Equals(preferredDefault, StringComparison.OrdinalIgnoreCase))?.Id
            ?? models.FirstOrDefault()?.Id;
    }

    private static List<MetaFetchedModel> NormalizeModels(
        IEnumerable<MetaFetchedModel> models,
        Func<string, bool> predicate) =>
        models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id) && predicate(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DisplayName(string modelId) =>
        modelId switch
        {
            DefaultLlmModelId => "Muse Spark 1.2",
            "muse-spark-1.1" => "Muse Spark 1.1",
            DefaultTranscriptionModelId => "Muse Voice Transcribe 1.0",
            _ => modelId,
        };

    private static string NormalizeReasoningEffort(string? reasoningEffort) =>
        reasoningEffort is "minimal" or "low" or "medium" or "high" or "xhigh"
            ? reasoningEffort
            : "medium";

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? FirstLanguageCode(IEnumerable<string> languageHints)
    {
        var hint = languageHints.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value)
            && !value.Equals("auto", StringComparison.OrdinalIgnoreCase));
        return hint?.Split('-', '_')[0].ToLowerInvariant();
    }
}
