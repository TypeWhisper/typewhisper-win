using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Meta;

/// <summary>
/// Provides Meta transcription and language-model capabilities.
/// </summary>
public sealed partial class MetaPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ILlmRequestHedgingSupport
{
    internal const string BaseUrl = "https://api.meta.ai";
    internal const string DefaultTranscriptionModelId = "muse-voice-transcribe-1.0";
    internal const string DefaultLlmModelId = "muse-spark-1.2";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedTranscriptionModelSettingName = "selectedModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels";
    private const string FetchedModelsAtSettingName = "fetchedModelsAtUtc";
    private const string ReasoningEffortSettingName = "reasoningEffort";
    private const string SpeakerDiarizationSettingName = "speakerDiarizationEnabled";
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

    private static readonly IReadOnlyDictionary<string, string> CanonicalLanguageCodes =
        LanguageNames
            .Where(pair => pair.Key.Length == 2 && pair.Key != "iw")
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly Func<MetaRealtimeConnectionOptions, CancellationToken, Task<IStreamingSession>>
        _streamingSessionFactory;
    private readonly ModelCatalogAutoRefresh _modelRefresh;
    // Guards the key, the catalog and the selections against a refresh that finishes on another thread.
    private readonly object _catalogLock = new();
    // Bumped on a key change and on deactivation, so a refresh started before either is dropped.
    private int _catalogVersion;
    private bool _keyChangePending;
    private IPluginHostServices? _host;
    private string? _apiKey;
    // The saved choices. A list that lacks one uses the default in memory and keeps the saved value.
    private string? _preferredModelId;
    private string? _preferredLlmModelId;
    private string? _selectedModelId;
    private string? _selectedLlmModelId;
    private string _reasoningEffort = "medium";
    private bool _speakerDiarizationEnabled;
    private List<MetaFetchedModel> _fetchedLlmModels = [];
    private List<MetaFetchedModel> _fetchedTranscriptionModels = [];
    private DateTimeOffset? _fetchedModelsAt;

    /// <summary>
    /// Initializes a new Meta plugin instance.
    /// </summary>
    public MetaPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, refreshModelsAutomatically: ModelCatalogAutoRefresh.AllowedInThisProcess)
    {
    }

    internal MetaPlugin(
        HttpClient httpClient,
        Func<MetaRealtimeConnectionOptions, CancellationToken, Task<IStreamingSession>>?
            streamingSessionFactory = null,
        bool refreshModelsAutomatically = false,
        TimeProvider? clock = null)
    {
        _httpClient = httpClient;
        _modelRefresh = new ModelCatalogAutoRefresh(refreshModelsAutomatically, clock);
        _streamingSessionFactory = streamingSessionFactory
            ?? (async (options, ct) => await MetaRealtimeStreamingSession.ConnectAsync(
                options.ApiKey,
                options.ModelId,
                options.Mode,
                options.LanguageBias,
                options.Keywords,
                ct));
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.meta";

    /// <inheritdoc />
    public string PluginName => "Meta";

    /// <inheritdoc />
    public string PluginVersion => "1.3.0";

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
        // Lists saved before 1.3.0 have no fetch date. They stay until the refresh replaces them.
        _fetchedModelsAt = LoadFetchedModelsAt(host);
        _preferredModelId = host.GetSetting<string>(SelectedTranscriptionModelSettingName);
        _preferredLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName);
        ResolveSelections();
        _reasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingName));
        _speakerDiarizationEnabled = host.GetSetting<bool>(SpeakerDiarizationSettingName);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
        StartModelRefreshIfStale();
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        lock (_catalogLock)
        {
            _catalogVersion++;
            _host = null;
        }

        _modelRefresh.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />


    /// <inheritdoc />
    public string ProviderId => "meta";

    /// <summary>Muse Voice transcription consumes multiple preferred languages in its request.</summary>
    public bool SupportsLanguageHints => true;

    /// <inheritdoc />
    public string ProviderDisplayName => "Meta";

    /// <inheritdoc />
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
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsStreaming => IsConfigured && _selectedModelId is not null;

    /// <inheritdoc />
    public bool SupportsStructuredDictionaryTerms => true;
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => IsConfigured;

    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => KeywordBudget;

    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt) => SupportsStreaming;

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

        lock (_catalogLock)
        {
            _host?.SetSetting(SelectedTranscriptionModelSettingName, modelId);
            _preferredModelId = modelId;
            _selectedModelId = modelId;
        }
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
            TranscriptionMode,
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
        return ParseTranscriptionResponse(
            json,
            null, // Language bias is not detected-language evidence.
            _speakerDiarizationEnabled);
    }

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(
            string.IsNullOrWhiteSpace(language) ? [] : [language],
            ct);

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct)
    {
        EnsureTranscriptionConfigured();
        return await _streamingSessionFactory(
            new MetaRealtimeConnectionOptions(
                _apiKey!,
                _selectedModelId!,
                TranscriptionMode,
                NormalizeLanguageHints(languageHints),
                ParseKeywords(prompt)),
            ct);
    }

    /// <inheritdoc />
    public string ProviderName => "Meta";

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
    internal bool SpeakerDiarizationEnabled => _speakerDiarizationEnabled;
    internal string TranscriptionMode => _speakerDiarizationEnabled ? "DIARIZATION" : "PUSH_TO_TALK";
    internal IPluginLocalization? Loc => PortableLocalization.TryGet(_host);
    internal bool IsUiAutomation => _host?.IsUiAutomation == true;
    internal int FetchedLlmModelCount => _fetchedLlmModels.Count;
    internal int FetchedTranscriptionModelCount => _fetchedTranscriptionModels.Count;
    internal DateTimeOffset? FetchedModelsAt => _fetchedModelsAt;
    internal Task ModelRefreshTask => _modelRefresh.Current;

    internal async Task SetApiKeyAsync(string? apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

        if (_host is not { } host)
        {
            _apiKey = normalized;
            return;
        }

        if (!changed)
        {
            if (normalized is null) await host.DeleteSecretAsync(ApiKeySecretName);
            else await host.StoreSecretAsync(ApiKeySecretName, normalized);
            return;
        }

        // The catalog belongs to the old account, and a refresh still running for it must not land. Until the
        // new key is in place, no refresh may save a list either: one started now still sends the old key.
        lock (_catalogLock) { _catalogVersion++; _keyChangePending = true; }
        try
        {
            _modelRefresh.Cancel();
            // Persist dependent settings first. A failed reset must never replace the secret.
            var undo = WriteCatalog(host, [], [], fetchedAt: null);
            try
            {
                if (normalized is null) await host.DeleteSecretAsync(ApiKeySecretName);
                else await host.StoreSecretAsync(ApiKeySecretName, normalized);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Rollback(undo, error);
                throw;
            }

            lock (_catalogLock)
            {
                ApplyCatalog([], [], fetchedAt: null);
                _apiKey = normalized;
                // A refresh that started during the secret write captured the old key with the new version.
                _catalogVersion++;
            }
        }
        finally
        {
            // Also after a failed write, so later refreshes can save again.
            lock (_catalogLock) _keyChangePending = false;
        }

        host.NotifyCapabilitiesChanged();
        StartModelRefreshIfStale();
    }

    // Writes the catalog and its fetch date. A failed write restores the values written before it.
    private static Stack<Action> WriteCatalog(IPluginHostServices host, List<MetaFetchedModel> llm,
        List<MetaFetchedModel> transcription, DateTimeOffset? fetchedAt)
    {
        var undo = new Stack<Action>();
        void Write<T>(string key, T previous, T value)
        {
            undo.Push(() => host.SetSetting(key, previous));
            host.SetSetting(key, value);
        }
        try
        {
            Write(FetchedLlmModelsSettingName, host.GetSetting<List<MetaFetchedModel>>(FetchedLlmModelsSettingName), llm);
            Write(FetchedTranscriptionModelsSettingName,
                host.GetSetting<List<MetaFetchedModel>>(FetchedTranscriptionModelsSettingName), transcription);
            Write(FetchedModelsAtSettingName, LoadFetchedModelsAt(host), fetchedAt);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Rollback(undo, error);
            throw;
        }

        return undo;
    }

    private static void Rollback(Stack<Action> undo, Exception error)
    {
        var failures = new List<Exception> { error };
        while (undo.TryPop(out var rollback))
            try { rollback(); }
            catch (Exception failure) when (failure is not OutOfMemoryException) { failures.Add(failure); }
        if (failures.Count > 1) throw new AggregateException("Meta settings rollback failed.", failures);
    }

    private void ApplyCatalog(List<MetaFetchedModel> llm, List<MetaFetchedModel> transcription, DateTimeOffset? fetchedAt)
    {
        _fetchedLlmModels = llm;
        _fetchedTranscriptionModels = transcription;
        _fetchedModelsAt = fetchedAt;
        ResolveSelections();
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model =>
                !string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown language model: {modelId}", nameof(modelId));
        }

        lock (_catalogLock)
        {
            _host?.SetSetting(SelectedLlmModelSettingName, modelId);
            _preferredLlmModelId = modelId;
            _selectedLlmModelId = modelId;
        }
    }

    internal void SetReasoningEffort(string reasoningEffort)
    {
        _host?.SetSetting(ReasoningEffortSettingName, NormalizeReasoningEffort(reasoningEffort));
        _reasoningEffort = NormalizeReasoningEffort(reasoningEffort);
    }

    internal void SetSpeakerDiarizationEnabled(bool enabled)
    {
        if (_speakerDiarizationEnabled == enabled)
            return;

        _host?.SetSetting(SpeakerDiarizationSettingName, enabled);
        _speakerDiarizationEnabled = enabled;
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey)
            ?? throw new PluginRequestException("API key is required", PluginRequestFailureKind.Configuration);
        using var request = CreateModelsRequest(normalized);
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        return true;
    }

    internal Task<MetaModelCatalog?> RefreshAvailableModelsAsync(CancellationToken ct = default)
    {
        string? apiKey;
        int version;
        lock (_catalogLock) (apiKey, version) = (_apiKey, _catalogVersion);
        if (apiKey is null)
            return Task.FromResult<MetaModelCatalog?>(null);

        // The button supersedes a background refresh, which would otherwise land after it.
        _modelRefresh.Cancel();
        return RefreshModelsAsync(apiKey, version, automatic: false, ct);
    }

    /// <summary>
    /// Meta retires models without notice. Reloading the list once a day drops a retired model
    /// instead of failing every request with HTTP 404.
    /// </summary>
    private void StartModelRefreshIfStale()
    {
        string apiKey;
        int version;
        lock (_catalogLock)
        {
            if (_host is null || _apiKey is not { } key || !_modelRefresh.IsStale(_fetchedModelsAt))
                return;
            (apiKey, version) = (key, _catalogVersion);
        }

        _modelRefresh.Start(
            ct => RefreshModelsAsync(apiKey, version, automatic: true, ct),
            error => _host?.Log(PluginLogLevel.Warning, $"Meta model refresh failed: {error.GetType().Name}"));
    }

    private async Task<MetaModelCatalog?> RefreshModelsAsync(string apiKey, int version, bool automatic, CancellationToken ct)
    {
        using var request = CreateModelsRequest(apiKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return ModelRefreshFailed(new HttpRequestException($"HTTP {(int)response.StatusCode}."));

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return ModelRefreshFailed(new JsonException("Model response must contain a data array."));
            }

            var models = new List<MetaFetchedModel>();
            foreach (var element in data.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return ModelRefreshFailed(new JsonException("Model entries must contain a nonblank string id."));
                }

                models.Add(new MetaFetchedModel(id.GetString()!,
                    element.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null));
            }
            var llmModels = NormalizeModels(models, IsLlmModel);
            var transcriptionModels = NormalizeModels(models, IsTranscriptionModel);
            return CommitRefreshedCatalog(version, automatic, llmModels, transcriptionModels, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (HttpRequestException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (IOException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (JsonException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (InvalidOperationException ex)
        {
            return ModelRefreshFailed(ex);
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

        return languageHints
            .Select(hint => TryNormalizeLanguageHint(hint, out var languageName, out _)
                ? languageName
                : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> ParseKeywords(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? []
            : PluginDictionaryTerms.ParsePrompt(prompt)
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    internal static string CreateTranscriptionRequestJson(
        string modelId,
        string mode,
        IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["audioEncoding"] = "WAV",
            ["mode"] = mode,
        };
        if (languageBias.Count > 0)
            body["languageBias"] = languageBias;
        if (keywords.Count > 0)
            body["keywords"] = keywords;

        return JsonSerializer.Serialize(body);
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? requestedLanguage,
        bool includeSpeakerLabels = false)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("transcript", out var transcriptElement)
            || transcriptElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Meta returned no string transcript.");
        var transcript = transcriptElement.GetString()!.Trim();
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
                if (turn.ValueKind != JsonValueKind.Object
                    || !turn.TryGetProperty("transcript", out var textElement)
                    || textElement.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("Meta returned no string transcript in a diarization turn.");
                }
                var text = textElement.GetString()!.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (includeSpeakerLabels
                    && turn.TryGetProperty("speaker", out var speakerElement)
                    && NormalizeSpeakerLabel(speakerElement.GetString()) is { } speaker)
                {
                    text = $"{speaker}: {text}";
                }
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

        if (includeSpeakerLabels && segments.Count > 0)
            transcript = string.Join("\n", segments.Select(segment => segment.Text));

        return new PluginTranscriptionResult(
            transcript,
            requestedLanguage,
            durationSeconds,
            NoSpeechProbability: null)
        {
            Segments = segments,
        };
    }

    internal static string? NormalizeSpeakerLabel(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;
        return trimmed.Contains("speaker", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"Speaker {trimmed}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _modelRefresh.Dispose();
        _httpClient.Dispose();
    }

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

    /// <summary>
    /// Uses a saved model only while the current list offers it, so a retired model falls back to the
    /// default instead of failing every request with HTTP 404. The saved value stays and applies again
    /// once a later list offers it.
    /// </summary>
    private void ResolveSelections()
    {
        _selectedModelId = NormalizeSelection(
            _preferredModelId,
            TranscriptionModels,
            DefaultTranscriptionModelId);
        _selectedLlmModelId = NormalizeSelection(
            _preferredLlmModelId,
            SupportedModels,
            DefaultLlmModelId);
    }

    private MetaModelCatalog? CommitRefreshedCatalog(int version, bool automatic,
        List<MetaFetchedModel> llm, List<MetaFetchedModel> transcription, CancellationToken ct)
    {
        IPluginHostServices host;
        lock (_catalogLock)
        {
            // A result that arrives after deactivation or a key change belongs to an old connection.
            if ((automatic && ct.IsCancellationRequested) || version != _catalogVersion || _keyChangePending || _host is null)
                return null;
            host = _host;
            if (automatic && WouldDropFetchedModels(llm, transcription))
            {
                host.Log(PluginLogLevel.Warning, "Meta returned no usable model list. Keeping the current list.");
                return null;
            }

            var fetchedAt = _modelRefresh.UtcNow;
            WriteCatalog(host, llm, transcription, fetchedAt);
            ApplyCatalog(llm, transcription, fetchedAt);
        }

        host.NotifyCapabilitiesChanged();
        return new MetaModelCatalog(llm, transcription);
    }

    // The background refresh never trades a fetched list for an empty one: an empty answer is more
    // likely a provider hiccup than a retired model family, and the button can still apply it.
    private bool WouldDropFetchedModels(List<MetaFetchedModel> llm, List<MetaFetchedModel> transcription) =>
        (llm.Count == 0 && transcription.Count == 0)
        || (llm.Count == 0 && _fetchedLlmModels.Count > 0)
        || (transcription.Count == 0 && _fetchedTranscriptionModels.Count > 0);

    private static DateTimeOffset? LoadFetchedModelsAt(IPluginHostServices host)
    {
        try
        {
            return host.GetSetting<DateTimeOffset?>(FetchedModelsAtSettingName)?.ToUniversalTime();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A damaged date reads as "no date", which refreshes the list.
            return null;
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

    internal static string? FirstLanguageCode(IEnumerable<string> languageHints) =>
        languageHints
            .Select(hint => TryNormalizeLanguageHint(hint, out _, out var canonicalCode)
                ? canonicalCode
                : null)
            .FirstOrDefault(code => code is not null);

    private static bool TryNormalizeLanguageHint(
        string? rawHint,
        out string languageName,
        out string canonicalCode)
    {
        languageName = "";
        canonicalCode = "";
        var hint = rawHint?.Trim();
        if (string.IsNullOrWhiteSpace(hint)
            || hint.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseCode = hint.Split('-', '_')[0];
        if (LanguageNames.TryGetValue(baseCode, out var matchedName)
            && CanonicalLanguageCodes.TryGetValue(matchedName, out var matchedCode))
        {
            languageName = matchedName;
            canonicalCode = matchedCode;
            return true;
        }

        if (!CanonicalLanguageCodes.TryGetValue(hint, out var namedCode))
            return false;

        canonicalCode = namedCode;
        languageName = LanguageNames[namedCode];
        return true;
    }

    private MetaModelCatalog? ModelRefreshFailed(Exception ex)
    {
        _host?.Log(PluginLogLevel.Warning, $"Could not refresh Meta model catalog: {ex.Message}");
        return null;
    }
}

internal sealed record MetaRealtimeConnectionOptions(
    string ApiKey,
    string ModelId,
    string Mode,
    IReadOnlyList<string> LanguageBias,
    IReadOnlyList<string> Keywords);
