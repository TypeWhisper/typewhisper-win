using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// Provides reson8 plugin behavior.
/// </summary>
public sealed partial class Reson8Plugin : ITranscriptionEnginePlugin
{
    internal const string DefaultModelId = "__default__";
    internal const string DefaultBaseUrl = "https://api.reson8.dev";
    internal const string DefaultAuthHeader = "Authorization";

    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string CustomBaseUrlSettingName = "customBaseURL";
    private const string CustomAuthHeaderSettingName = "customAuthHeader";
    private const string FetchedCustomModelsSettingName = "fetchedCustomModels";
    internal const string FetchedModelsSettingName = "fetchedModels";
    internal const string FillerModeSettingName = "fillerMode";
    internal const string BiasStrengthSettingName = "biasStrength";
    internal const string DictionaryModelEnabledSettingName = "dictionaryModelEnabled";
    internal const string DictionaryModelSettingName = "dictionaryModel";
    internal const string PatternSetsSettingName = "patternSets";

    internal const double DefaultBiasStrength = 0.45;

    /// <summary>
    /// The prerecorded endpoint rejects bodies above an undocumented size and has to answer within its request
    /// timeout, so the host splits recordings into five-minute requests like the macOS plugin (16 kHz PCM16).
    /// </summary>
    internal const int MaximumUploadBytes = 300 * 16_000 * 2 + 44;

    /// <summary>Reson8 answers 414 once the query passes about 4,000 characters, so the encoded phrases stay well below.</summary>
    internal const int MaximumEncodedPhrasesLength = 2_500;

    /// <summary>How long a transcription waits for the dictionary's custom model before it sends phrases instead.</summary>
    internal static readonly TimeSpan DictionaryModelTimeout = TimeSpan.FromSeconds(5);

    private static readonly DictionaryTermsBudget PhrasesBudget = new(MaxTerms: 250, MaxCharsPerTerm: 255, MaxTotalChars: MaximumEncodedPhrasesLength);
    internal static readonly DictionaryTermsBudget CustomModelBudget = new(MaxTerms: 50_000, MaxCharsPerTerm: 255);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string?, CancellationToken, Task<IStreamingSession>>? _streamingFactory;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private readonly object _backgroundLock = new();
    private readonly List<Task> _backgroundTasks = [];
    private CancellationTokenSource _backgroundCancellation = new();
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _selectedModelId = DefaultModelId;
    private string _customBaseUrl = DefaultBaseUrl;
    private string _customAuthHeader = DefaultAuthHeader;
    private IReadOnlyList<Reson8CustomModel> _fetchedCustomModels = [];
    private IReadOnlyList<Reson8Model> _fetchedModels = [];
    private string _fillerMode = Reson8FillerModes.Natural;
    private double? _biasStrength;
    private bool _dictionaryModelEnabled;
    private IReadOnlyList<Reson8PatternSet> _patternSets = [];
    private Reson8DictionaryModelSync? _dictionarySync;
    private IReson8CustomModelApi? _customModelApiOverride;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the Reson8Plugin class.
    /// </summary>
    public Reson8Plugin()
        : this(CreateHttpClient())
    {
    }

    internal Reson8Plugin(
        HttpClient httpClient,
        Func<string?, CancellationToken, Task<IStreamingSession>>? streamingFactory = null,
        IReson8CustomModelApi? customModelApi = null)
    {
        _httpClient = httpClient;
        _streamingFactory = streamingFactory;
        _customModelApiOverride = customModelApi;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.reson8";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Reson8";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.3.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        if (_backgroundCancellation.IsCancellationRequested)
        {
            _backgroundCancellation.Dispose();
            _backgroundCancellation = new CancellationTokenSource();
        }

        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _customBaseUrl = NormalizeBaseUrl(host.GetSetting<string>(CustomBaseUrlSettingName));
        _customAuthHeader = NormalizeAuthHeader(host.GetSetting<string>(CustomAuthHeaderSettingName));
        _fetchedCustomModels = host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName) ?? [];
        _fetchedModels = Reson8Catalog.Sanitize(ReadSetting<List<Reson8Model>>(host, FetchedModelsSettingName));
        _selectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        _fillerMode = Reson8FillerModes.Normalize(ReadSetting<string>(host, FillerModeSettingName));
        _biasStrength = NormalizeBiasStrength(ReadSetting<double?>(host, BiasStrengthSettingName));
        _dictionaryModelEnabled = ReadSetting<bool?>(host, DictionaryModelEnabledSettingName) ?? false;
        _patternSets = Reson8Catalog.SanitizePatternSets(ReadSetting<List<Reson8PatternSet>>(host, PatternSetsSettingName));
        var dictionaryState = ReadSetting<Reson8DictionaryModelState>(host, DictionaryModelSettingName);
        _dictionarySync?.Dispose();
        _dictionarySync = new Reson8DictionaryModelSync(
            dictionaryState is { } stored && IsUsableModelId(stored.ModelId)
                ? stored with { Terms = stored.Terms ?? [] } : Reson8DictionaryModelState.Empty,
            _customModelApiOverride ?? new Reson8HttpCustomModelApi(_httpClient, CreateCustomModelRequest),
            SaveDictionaryModelState);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured}, model={_selectedModelId})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public async Task DeactivateAsync()
    {
        _backgroundCancellation.Cancel();
        await BackgroundWork.ConfigureAwait(false);
        _host = null;
    }

    /// <summary>All background dictionary syncs, so tests and shutdown can wait for them.</summary>
    internal Task BackgroundWork
    {
        get { lock (_backgroundLock) return Task.WhenAll(_backgroundTasks); }
    }

    // A damaged or outdated settings value must not keep the plugin from activating.
    private static T? ReadSetting<T>(IPluginHostServices host, string key)
    {
        try { return host.GetSetting<T>(key); }
        catch (JsonException) { return default; }
        catch (NotSupportedException) { return default; }
        catch (InvalidOperationException) { return default; }
    }

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "reson8";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Reson8";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// The default model keeps its id so saved selections stay valid. Then come the other models Reson8 lists,
    /// the custom models and the pattern sets, which run on the default model. The custom model that holds the
    /// dictionary is left out.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var defaultLanguages = LanguagesFor(DefaultModelId);
            var models = new List<PluginModelInfo>
            {
                ModelInfo(DefaultModelId, DefaultListedModel?.DisplayName ?? L("Default model", "Standardmodell"), defaultLanguages)
            };
            models.AddRange(_fetchedModels.Where(model => model.IsDefault != true)
                .Select(model => ModelInfo(model.Id, model.DisplayName, LanguagesFor(model.Id))));
            models.AddRange(CustomModels.Select(model => ModelInfo(model.Id, model.Name, defaultLanguages)));
            models.AddRange(_patternSets.Select(set => ModelInfo(
                set.ModelId, string.Format(CultureInfo.CurrentCulture, L("{0} (patterns)", "{0} (Muster)"), set.Name), defaultLanguages)));
            return models;
        }
    }

    private static PluginModelInfo ModelInfo(string id, string name, IReadOnlyList<string> languages) =>
        new(id, name) { LanguageCount = languages.Count, LanguageCodes = languages };

    /// <summary>The custom models to choose from, without the one that holds the dictionary.</summary>
    private IEnumerable<Reson8CustomModel> CustomModels
    {
        get
        {
            var dictionaryModelId = _dictionarySync?.State.ModelId;
            return _fetchedCustomModels.Where(model => model.Id != dictionaryModelId);
        }
    }

    private Reson8Model? DefaultListedModel => _fetchedModels.FirstOrDefault(model => model.IsDefault == true);

    /// <summary>The listed model a selection transcribes with. The default, custom models and pattern sets use the default model.</summary>
    private Reson8Model? ModelFor(string? modelId) =>
        _fetchedModels.FirstOrDefault(model => model.IsDefault != true && model.Id == modelId) ?? DefaultListedModel;

    private bool IsListedModel(string? modelId) =>
        modelId is not null && modelId != DefaultModelId && _fetchedModels.Any(model => model.IsDefault != true && model.Id == modelId);

    private Reson8PatternSet? PatternSetFor(string? modelId) =>
        modelId is not null && modelId.StartsWith(Reson8PatternSet.ModelIdPrefix, StringComparison.Ordinal)
            ? _patternSets.FirstOrDefault(set => set.ModelId == modelId) : null;

    private bool Takes(string parameter, string? modelId = null) =>
        Reson8Catalog.TakesParameter(ModelFor(modelId ?? _selectedModelId), parameter);

    private IReadOnlyList<string> LanguagesFor(string? modelId) =>
        ModelFor(modelId)?.Languages is { Count: > 0 } languages ? languages : Reson8Catalog.FallbackLanguages;

    /// <summary>
    /// Gets the selected transcription model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => false;
    /// <summary>
    /// Gets whether the provider supports live streaming transcription.
    /// </summary>
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;

    /// <summary>Resonant-1 streams; Rumble-2-Flash transcribes prerecorded audio only. An unknown model is assumed to stream.</summary>
    public bool SupportsStreaming => ModelFor(_selectedModelId)?.Endpoints is not { } endpoints || endpoints.Contains("realtime");

    /// <summary>Several hints go as <c>language=nl,en</c>, which limits Reson8's per-utterance detection to them.</summary>
    public bool SupportsLanguageHints => true;

    /// <summary>The languages of the selected model.</summary>
    public IReadOnlyList<string> SupportedLanguages => LanguagesFor(_selectedModelId);

    /// <summary>Dictionary terms go as phrases or into the dictionary's custom model, except with patterns or Rumble-2-Flash.</summary>
    public bool SupportsDictionaryTerms => TakesDictionaryTerms(_selectedModelId);

    /// <summary>Terms arrive as a structured list, so their boundaries survive.</summary>
    public bool SupportsStructuredDictionaryTerms => true;

    /// <summary>250 phrases per request, or 50,000 terms in the dictionary's custom model.</summary>
    public DictionaryTermsBudget DictionaryTermsBudget => _dictionaryModelEnabled ? CustomModelBudget : PhrasesBudget;

    /// <summary>Recordings longer than five minutes are split by the host before upload.</summary>
    public int MaximumAudioUploadBytes => MaximumUploadBytes;

    internal string? ApiKey => _apiKey;
    internal string CustomBaseUrl => _customBaseUrl;
    internal string CustomAuthHeader => _customAuthHeader;
    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels => _fetchedCustomModels;
    internal IReadOnlyList<Reson8Model> FetchedModels => _fetchedModels;
    internal IReadOnlyList<Reson8PatternSet> PatternSets => _patternSets;
    internal string FillerMode => _fillerMode;
    internal double? BiasStrength => _biasStrength;
    internal bool DictionaryModelEnabled => _dictionaryModelEnabled;
    internal Reson8DictionaryModelState DictionaryModelState => _dictionarySync?.State ?? Reson8DictionaryModelState.Empty;
    /// <summary>The wait for the dictionary's custom model; tests shorten it.</summary>
    internal TimeSpan DictionaryModelWait { get; set; } = DictionaryModelTimeout;
    internal IPluginLocalization? Loc => PortableLocalization.TryGet(_host);

    private bool TakesDictionaryTerms(string? modelId) => Takes("phrases", modelId) && PatternSetFor(modelId) is null;

    /// <summary>
    /// The dictionary goes into its custom model when the user turned that on and the request has no custom model
    /// of its own: the default model or another listed model that takes one.
    /// </summary>
    private bool UsesDictionaryModel(string? modelId) =>
        _dictionaryModelEnabled && TakesDictionaryTerms(modelId) && Takes("custom_model_id", modelId)
        && (modelId is null or DefaultModelId || IsListedModel(modelId));

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        var before = CapabilitySnapshot();
        _host?.SetSetting(SelectedModelSettingName, normalized);
        _selectedModelId = normalized;
        // Rumble-2-Flash neither streams nor takes dictionary terms, and its languages differ.
        if (CapabilitySnapshot() != before)
            _host?.NotifyCapabilitiesChanged();
    }

    private (bool Streaming, bool Dictionary, string Languages) CapabilitySnapshot() =>
        (SupportsStreaming, SupportsDictionaryTerms, string.Join(",", SupportedLanguages));

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(wavAudio, NormalizeLanguage(language) is { } normalized ? [normalized] : [], translate, prompt, ct);

    /// <summary>
    /// Transcribes PCM audio with ordered language hints through the prerecorded endpoint.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
        var language = ResolveLanguage(languageHints);
        var dictionaryModelId = await DictionaryModelIdAsync(prompt, ct).ConfigureAwait(false);
        try
        {
            return await TranscribePrerecordedAsync(pcm16, language, prompt, dictionaryModelId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnknownModelError(ex, dictionaryModelId))
        {
            // The dictionary's custom model was deleted, for example in the Reson8 console.
            _host?.Log(PluginLogLevel.Warning, "Reson8 no longer has the dictionary's custom model; creating it again.");
            await _dictionarySync!.ForgetAsync(dictionaryModelId!, ct).ConfigureAwait(false);
            return await TranscribePrerecordedAsync(
                pcm16, language, prompt, await DictionaryModelIdAsync(prompt, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    private async Task<PluginTranscriptionResult> TranscribePrerecordedAsync(
        byte[] pcm16, string? language, string? prompt, string? dictionaryModelId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildPrerecordedUri(language, prompt, dictionaryModelId));
        AddAuthHeader(request, _apiKey!, _customAuthHeader);
        request.Content = new ByteArrayContent(pcm16);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await TranscriptionHttpErrors.SendAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            ThrowForApiError(response, json, request.RequestUri);

        return ParseTranscriptionResponse(json, language, pcm16.Length);
    }

    /// <summary>
    /// Transcribes streaming asynchronously.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");
        if (!SupportsStreaming)
            return await TranscribeAsync(wavAudio, language, translate, prompt, ct);

        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamToken = progressCancellation.Token;
        try
        {
            var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
            var hints = NormalizeLanguage(language) is { } normalized ? new[] { normalized } : [];
            await using var session = await StartStreamingWithLanguageHintsAndPromptAsync(hints, prompt, streamToken);
            var collector = new Reson8TranscriptCollector();

            session.TranscriptReceived += evt =>
            {
                var text = collector.ApplyEvent(evt);
                if (!streamToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(text) && !onProgress(text))
                    progressCancellation.Cancel();
            };

            const int chunkSize = 8192;
            for (var offset = 0; offset < pcm16.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, pcm16.Length - offset);
                streamToken.ThrowIfCancellationRequested();
                await session.SendAudioAsync(pcm16.AsMemory(offset, count), streamToken);
            }

            streamToken.ThrowIfCancellationRequested();
            await session.FinalizeAsync(streamToken);
            streamToken.ThrowIfCancellationRequested();

            var text = collector.FinalText;
            return string.IsNullOrWhiteSpace(text)
                ? await TranscribeAsync(wavAudio, language, translate, prompt, streamToken)
                : new PluginTranscriptionResult(text, Reson8Transcript.DetectedLanguage(collector.DetectedLanguage, NormalizeLanguage(language)),
                    PcmDurationSeconds(pcm16.Length), NoSpeechProbability: null);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            streamToken.ThrowIfCancellationRequested();
            return await TranscribeAsync(wavAudio, language, translate, prompt, streamToken);
        }
    }

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(NormalizeLanguage(language) is { } normalized ? [normalized] : [], prompt: null, ct);

    /// <summary>Opens a streaming session with ordered language hints.</summary>
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, prompt: null, ct);

    /// <summary>Opens a streaming session that sends dictionary terms, the dictionary's custom model or patterns.</summary>
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");
        if (!SupportsStreaming)
            throw new NotSupportedException("The selected Reson8 model does not stream.");
        var language = ResolveLanguage(languageHints);
        if (_streamingFactory is not null) return await _streamingFactory(language, ct);

        var dictionaryModelId = await DictionaryModelIdAsync(prompt, ct).ConfigureAwait(false);
        try
        {
            return await ConnectRealtimeAsync(language, prompt, dictionaryModelId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnknownModelError(ex, dictionaryModelId))
        {
            // Reson8 rejects the connection and names the model in X-Error-Message.
            _host?.Log(PluginLogLevel.Warning, "Reson8 no longer has the dictionary's custom model; creating it again.");
            await _dictionarySync!.ForgetAsync(dictionaryModelId!, ct).ConfigureAwait(false);
            return await ConnectRealtimeAsync(
                language, prompt, await DictionaryModelIdAsync(prompt, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    private async Task<IStreamingSession> ConnectRealtimeAsync(string? language, string? prompt, string? dictionaryModelId, CancellationToken ct) =>
        await Reson8StreamingSession.ConnectAsync(
            _apiKey!,
            _customAuthHeader,
            Reson8StreamingSession.BuildRealtimeUri(_customBaseUrl, BuildQuery(language, prompt, dictionaryModelId, realtime: true)),
            language,
            ct).ConfigureAwait(false);

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        IPluginHostServices? hostToNotify = null;

        await _apiKeyWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

            if (_host is not null)
            {
                var oldSelection = _host.GetSetting<string>(SelectedModelSettingName);
                var oldModels = _host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName);
                var selectionWritten = false;
                var modelsWritten = false;
                try
                {
                    if (changed)
                    {
                        // Write dependent settings first; restore them if a later
                        // write fails so the old credential keeps its model state.
                        _host.SetSetting(SelectedModelSettingName, DefaultModelId);
                        selectionWritten = true;
                        _host.SetSetting(FetchedCustomModelsSettingName, Array.Empty<Reson8CustomModel>());
                        modelsWritten = true;
                    }
                    if (normalized is null)
                        await _host.DeleteSecretAsync(ApiKeySecretName).ConfigureAwait(false);
                    else
                        await _host.StoreSecretAsync(ApiKeySecretName, normalized).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    var errors = new List<Exception> { failure };
                    try { if (modelsWritten) _host.SetSetting(FetchedCustomModelsSettingName, oldModels); }
                    catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                    try { if (selectionWritten) _host.SetSetting(SelectedModelSettingName, oldSelection); }
                    catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                    if (errors.Count > 1) throw new AggregateException("Credential update failed and model settings could not be fully restored.", errors);
                    throw;
                }
            }
            _apiKey = normalized;
            if (changed)
            {
                _fetchedCustomModels = [];
                _selectedModelId = DefaultModelId;
                // The next key may belong to another account, which does not have the dictionary's custom model.
                ForgetDictionaryModel();
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

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_customBaseUrl}/v1/custom-model");
        AddAuthHeader(request, normalized, _customAuthHeader);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal async Task<IReadOnlyList<Reson8CustomModel>> FetchCustomModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_customBaseUrl}/v1/custom-model");
        AddAuthHeader(request, _apiKey!, _customAuthHeader);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var models = JsonSerializer.Deserialize<List<Reson8CustomModel>>(json, JsonOptions)
            ?? throw new JsonException("The custom model catalog was empty or invalid.");
        if (models.Any(model => model is null || string.IsNullOrWhiteSpace(model.Id) || model.Id == DefaultModelId || string.IsNullOrWhiteSpace(model.Name)) ||
            models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != models.Count)
            throw new JsonException("The custom model catalog contains invalid or duplicate entries.");
        return models;
    }

    /// <summary>The models Reson8 lists (<c>GET /v1/model</c>), such as Resonant-1 and Rumble-2-Flash.</summary>
    internal async Task<IReadOnlyList<Reson8Model>> FetchModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_customBaseUrl}/v1/model");
        AddAuthHeader(request, _apiKey!, _customAuthHeader);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var models = Reson8Catalog.ParseModels(await response.Content.ReadAsStringAsync(ct));
        return models.Count == 0 ? throw new JsonException("Reson8 listed no models.") : models;
    }

    internal void SetFetchedModels(IReadOnlyList<Reson8Model> models)
    {
        var before = CapabilitySnapshot();
        var sanitized = Reson8Catalog.Sanitize(models);
        _host?.SetSetting(FetchedModelsSettingName, sanitized);
        _fetchedModels = sanitized;
        if (_selectedModelId != DefaultModelId && !IsKnownModel(_selectedModelId))
            SelectModel(DefaultModelId);
        if (CapabilitySnapshot() != before)
            _host?.NotifyCapabilitiesChanged();
    }

    private bool IsKnownModel(string modelId) =>
        IsListedModel(modelId) || PatternSetFor(modelId) is not null || _fetchedCustomModels.Any(model => model.Id == modelId);

    internal void SetFetchedCustomModels(IReadOnlyList<Reson8CustomModel> models)
    {
        var nextModels = models.ToArray();
        var nextSelection = _selectedModelId != DefaultModelId && !IsListedModel(_selectedModelId)
            && PatternSetFor(_selectedModelId) is null && nextModels.All(m => m.Id != _selectedModelId)
            ? DefaultModelId : _selectedModelId;
        if (_host is { } host)
        {
            var previousSelection = host.GetSetting<string>(SelectedModelSettingName);
            host.SetSetting(SelectedModelSettingName, nextSelection);
            try { host.SetSetting(FetchedCustomModelsSettingName, nextModels); }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                try { host.SetSetting(SelectedModelSettingName, previousSelection); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException)
                { throw new AggregateException("Model refresh failed and its selection could not be restored.", failure, rollback); }
                throw;
            }
        }
        _selectedModelId = nextSelection;
        _fetchedCustomModels = nextModels;
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetCustomBaseUrl(string? url)
    {
        var normalized = NormalizeBaseUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || string.IsNullOrEmpty(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Enter an absolute HTTP(S) server URL without credentials, query or fragment.", nameof(url));
        if (string.Equals(normalized, _customBaseUrl, StringComparison.Ordinal)) return;
        PersistConnectionChange(CustomBaseUrlSettingName, normalized == DefaultBaseUrl ? null : normalized);
        _customBaseUrl = normalized;
        _host?.NotifyCapabilitiesChanged();
    }

    private void PersistConnectionChange(string settingName, string? value)
    {
        if (_host is { } host)
        {
            var oldSelection = host.GetSetting<string>(SelectedModelSettingName);
            var oldModels = host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName);
            var selectionWritten = false;
            var modelsWritten = false;
            try
            {
                host.SetSetting(SelectedModelSettingName, DefaultModelId);
                selectionWritten = true;
                host.SetSetting(FetchedCustomModelsSettingName, Array.Empty<Reson8CustomModel>());
                modelsWritten = true;
                // Commit connection identity only after dependent settings succeed.
                host.SetSetting(settingName, value);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                var errors = new List<Exception> { failure };
                try { if (modelsWritten) host.SetSetting(FetchedCustomModelsSettingName, oldModels); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                try { if (selectionWritten) host.SetSetting(SelectedModelSettingName, oldSelection); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException) { errors.Add(rollback); }
                if (errors.Count > 1) throw new AggregateException("Connection update failed and model settings could not be fully restored.", errors);
                throw;
            }
        }
        _selectedModelId = DefaultModelId;
        _fetchedCustomModels = [];
        // Another server or account does not have the dictionary's custom model.
        ForgetDictionaryModel();
    }

    internal void SetCustomAuthHeader(string? header)
    {
        var normalized = NormalizeAuthHeader(header);
        if (normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
            throw new ArgumentException("Enter a valid HTTP header name.", nameof(header));
        if (string.Equals(normalized, _customAuthHeader, StringComparison.OrdinalIgnoreCase)) return;
        PersistConnectionChange(CustomAuthHeaderSettingName, normalized == DefaultAuthHeader ? null : normalized);
        _customAuthHeader = normalized;
        _host?.NotifyCapabilitiesChanged();
    }

    // Dictionary custom model

    /// <summary>
    /// The dictionary's custom model for a request, created or updated first when the dictionary changed. Null sends
    /// the terms as phrases: when the custom model is off or does not apply, the prompt holds no terms, or Reson8 did
    /// not update it in time. A sync that takes longer goes on in the background for the next request.
    /// </summary>
    private async Task<string?> DictionaryModelIdAsync(string? prompt, CancellationToken ct)
    {
        if (!UsesDictionaryModel(_selectedModelId) || _dictionarySync is not { } sync)
            return null;
        var terms = PluginDictionaryTerms.Clip(PluginDictionaryTerms.ParsePrompt(prompt), CustomModelBudget);
        if (terms.Count == 0)
            return null;

        var syncTask = TrackBackground(sync.ModelIdForAsync(terms, _backgroundCancellation.Token));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = await Task.WhenAny(syncTask, Task.Delay(DictionaryModelWait, wait.Token)).ConfigureAwait(false);
        wait.Cancel();
        ct.ThrowIfCancellationRequested();
        if (completed != syncTask)
        {
            _host?.Log(PluginLogLevel.Warning, "Reson8 did not update the dictionary's custom model in time; sending phrases.");
            return null;
        }

        try
        {
            return await syncTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host?.Log(PluginLogLevel.Warning, $"Reson8 dictionary custom model unavailable; sending phrases: {ex.Message}");
            return null;
        }
    }

    private T TrackBackground<T>(T task) where T : Task
    {
        lock (_backgroundLock)
        {
            _backgroundTasks.RemoveAll(existing => existing.IsCompleted);
            // Observes the outcome, so a sync that fails after the request moved on raises no unobserved exception.
            _backgroundTasks.Add(task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        return task;
    }

    /// <summary>Reson8 answers a request for a custom model it no longer has with "Unknown vocabulary: &lt;id&gt;".</summary>
    private static bool IsUnknownModelError(Exception ex, string? modelId) =>
        modelId is not null && ex is not OperationCanceledException && ex.Message.Contains(modelId, StringComparison.OrdinalIgnoreCase);

    private void ForgetDictionaryModel()
    {
        if (_dictionarySync?.State.ModelId is { } modelId)
            TrackBackground(_dictionarySync.ForgetAsync(modelId, CancellationToken.None));
    }

    private void SaveDictionaryModelState(Reson8DictionaryModelState state)
    {
        var host = _host;
        if (host is null)
            return;
        var hadModel = _fetchedCustomModels.Any(model => model.Id == state.ModelId);
        host.SetSetting(DictionaryModelSettingName, state);
        // The dictionary's model leaves the model list once it is known.
        if (hadModel)
            host.NotifyCapabilitiesChanged();
    }

    private HttpRequestMessage? CreateCustomModelRequest(string path)
    {
        var key = _apiKey;
        if (string.IsNullOrEmpty(key))
            return null;
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_customBaseUrl}{path}");
        AddAuthHeader(request, key, _customAuthHeader);
        return request;
    }

    internal static bool IsUsableModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    // Query

    internal Uri BuildPrerecordedUri(string? language, string? prompt, string? dictionaryModelId) =>
        new($"{NormalizeBaseUrl(_customBaseUrl)}/v1/speech-to-text/prerecorded?{BuildQuery(language, prompt, dictionaryModelId, realtime: false)}");

    /// <summary>
    /// The parameters both endpoints take. A listed model goes in <c>model</c>, a custom model in
    /// <c>custom_model_id</c>; patterns, the dictionary's custom model and phrases exclude each other.
    /// </summary>
    internal Reson8Query BuildQuery(string? language, string? prompt, string? dictionaryModelId, bool realtime)
    {
        var query = new Reson8Query()
            .Add("encoding", "pcm_s16le")
            .Add("sample_rate", "16000")
            .Add("channels", "1")
            .Add("include_language", "true");
        if (!string.IsNullOrWhiteSpace(language))
            query.Add("language", language);

        var selected = _selectedModelId;
        var patternSet = PatternSetFor(selected);
        if (IsListedModel(selected))
            query.Add("model", selected);
        else if (selected != DefaultModelId && patternSet is null && !selected.StartsWith(Reson8PatternSet.ModelIdPrefix, StringComparison.Ordinal))
            query.Add("custom_model_id", selected);

        if (_fillerMode != Reson8FillerModes.Natural && Takes("filler_mode"))
            query.Add("filler_mode", _fillerMode);

        if (patternSet?.QueryValue is { } patterns && Takes("patterns"))
            query.Add("patterns", patterns);
        else if (dictionaryModelId is not null)
            query.Add("custom_model_id", dictionaryModelId);
        else if (PhrasesValue(prompt) is { } phrases)
            query.Add("phrases", phrases);

        // Only phrases and custom models bias the transcription, so the strength goes along with them.
        if (_biasStrength is { } bias && (query.Contains("custom_model_id") || query.Contains("phrases")))
            query.Add("bias_strength", bias.ToString("0.00", CultureInfo.InvariantCulture));

        if (Takes("include_timestamps") && Takes("include_words"))
        {
            query.Add("include_timestamps", "true");
            query.Add("include_words", "true");
        }

        if (realtime)
            query.Add("include_interim", "true");
        return query;
    }

    /// <summary>
    /// Reson8 takes phrases only in the query string. Terms are added while their encoded list stays within the
    /// URL budget; a term with a comma is left out, because Reson8 splits the list at commas.
    /// </summary>
    private string? PhrasesValue(string? prompt)
    {
        if (!TakesDictionaryTerms(_selectedModelId))
            return null;
        var phrases = new List<string>();
        var encodedLength = 0;
        foreach (var term in PluginDictionaryTerms.Clip(PluginDictionaryTerms.ParsePrompt(prompt), PhrasesBudget))
        {
            if (term.Contains(','))
                continue;
            var length = encodedLength + (phrases.Count == 0 ? 0 : 1) + Reson8Query.Escape(term).Length;
            if (length > MaximumEncodedPhrasesLength)
                break;
            phrases.Add(term);
            encodedLength = length;
        }

        return phrases.Count == 0 ? null : string.Join(",", phrases);
    }

    /// <summary>One language pins the transcription; several go as a list that limits Reson8's detection to them.</summary>
    internal static string? ResolveLanguage(IReadOnlyList<string> languageHints)
    {
        var languages = languageHints.Select(NormalizeLanguage).Where(language => language is not null).Select(language => language!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return languages.Length == 0 ? null : string.Join(",", languages);
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? fallbackLanguage,
        int pcm16ByteLength) =>
        Reson8Transcript.Parse(json, fallbackLanguage, PcmDurationSeconds(pcm16ByteLength));

    internal static string ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (GetString(root, "code") is { } code && GetString(root, "message") is { } codeMessage)
                return $"{code}: {codeMessage}";

            return GetString(root, "message")
                ?? GetString(root, "error")
                ?? GetString(root, "detail")
                ?? GetString(root, "title")
                ?? "Unknown error";
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json;
        }
    }

    internal static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    internal static void AddAuthHeader(HttpRequestMessage request, string apiKey, string authHeader)
    {
        var normalizedHeader = NormalizeAuthHeader(authHeader);
        if (string.Equals(normalizedHeader, DefaultAuthHeader, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
            return;
        }

        request.Headers.TryAddWithoutValidation(normalizedHeader, apiKey);
    }

    internal static string AuthHeaderValue(string apiKey, string authHeader) =>
        string.Equals(NormalizeAuthHeader(authHeader), DefaultAuthHeader, StringComparison.OrdinalIgnoreCase)
            ? $"ApiKey {apiKey}"
            : apiKey;

    private static string NormalizeModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();

    internal static double? NormalizeBiasStrength(double? strength) =>
        strength is { } value && double.IsFinite(value) && value is >= 0 and <= 1 && Math.Abs(value - DefaultBiasStrength) >= 0.001
            ? Math.Round(value, 2) : null;

    private static string? NormalizeApiKey(string? apiKey)
    {
        var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (normalized?.Any(char.IsControl) == true)
            throw new ArgumentException("API keys must not contain control characters.", nameof(apiKey));
        return normalized;
    }

    private static string NormalizeBaseUrl(string? url)
    {
        var normalized = string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.Trim();
        while (normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];

        return string.IsNullOrWhiteSpace(normalized) ? DefaultBaseUrl : normalized;
    }

    private static string NormalizeAuthHeader(string? header) =>
        string.IsNullOrWhiteSpace(header) ? DefaultAuthHeader : header.Trim();

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void ThrowForApiError(HttpResponseMessage response, string json, Uri? requestUri)
    {
        var detail = ExtractApiError(json);
        var subject = requestUri?.Query.Contains("custom_model_id=", StringComparison.Ordinal) == true ? "custom model" : "model";
        var message = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => $"Reson8 rejected the request: {detail}",
            HttpStatusCode.Unauthorized => "Invalid Reson8 API key.",
            HttpStatusCode.PaymentRequired => $"Reson8 credit limit reached: {detail}",
            HttpStatusCode.NotFound => $"Reson8 {subject} not found: {detail}",
            HttpStatusCode.RequestEntityTooLarge => $"Reson8 file too large: {detail}",
            // Reson8 answers 429 when the account's concurrent connection limit is reached.
            HttpStatusCode.TooManyRequests => $"Reson8 rate limit exceeded: {detail}",
            HttpStatusCode.InternalServerError => $"Reson8 server error: {detail}",
            _ => $"Reson8 API error {(int)response.StatusCode}: {detail}"
        };
        throw TranscriptionHttpErrors.Create(response, message);
    }

    private static double PcmDurationSeconds(int pcm16ByteLength) =>
        pcm16ByteLength <= 0 ? 0 : pcm16ByteLength / 2.0 / 16000.0;

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _backgroundCancellation.Cancel();
        try { BackgroundWork.GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Syncs only update a custom model. */ }
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
        _dictionarySync?.Dispose();
    }
}

internal static class WavPcm16Extractor
{
    /// <summary>
    /// Performs extract pcm16.
    /// </summary>
    public static byte[] ExtractPcm16(byte[] wavAudio)
    {
        if (wavAudio.Length < 44
            || !HasAscii(wavAudio, 0, "RIFF")
            || !HasAscii(wavAudio, 8, "WAVE"))
        {
            throw new ArgumentException("Audio must be a PCM16 mono 16 kHz WAV file.", nameof(wavAudio));
        }

        var offset = 12;
        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (offset + 8 <= wavAudio.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BitConverter.ToInt32(wavAudio, offset + 4);
            offset += 8;
            if (chunkSize < 0 || (long)offset + chunkSize > wavAudio.Length)
                throw new ArgumentException("The WAV contains a truncated or invalid chunk.", nameof(wavAudio));

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToInt16(wavAudio, offset);
                channels = BitConverter.ToInt16(wavAudio, offset + 2);
                sampleRate = BitConverter.ToInt32(wavAudio, offset + 4);
                bitsPerSample = BitConverter.ToInt16(wavAudio, offset + 14);
            }
            else if (chunkId == "data")
            {
                data = wavAudio.Skip(offset).Take(chunkSize).ToArray();
            }

            offset += chunkSize + (chunkSize % 2);
        }

        if (data is null)
            throw new ArgumentException("The WAV contains no audio data.", nameof(wavAudio));

        if (audioFormat == 1 && channels == 1 && sampleRate == 16000 && bitsPerSample == 16 && data.Length % 2 == 0)
            return data;

        throw new ArgumentException("Audio must be PCM16 mono at 16 kHz.", nameof(wavAudio));
    }

    private static bool HasAscii(byte[] bytes, int offset, string value)
    {
        if (offset + value.Length > bytes.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (bytes[offset + i] != value[i])
                return false;
        }

        return true;
    }
}
