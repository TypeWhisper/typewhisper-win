using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gemini;

/// <summary>
/// Provides Gemini language-model and transcription capabilities.
/// </summary>
public sealed class GeminiPlugin :
    ITranscriptionEnginePlugin,
    ILlmProviderPlugin,
    ILlmRequestHedgingSupport
{
    private const string NativeBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private const string OpenAiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    private const string ApiKeySecretName = "api-key";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels.v2";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels.v1";
    private const string ModelCatalogFetchedAtSettingName = "modelCatalogFetchedAtUtc";
    private const string SelectedTranscriptionModelSettingName = "selectedTranscriptionModel";
    private const string TranscriptionModeSettingName = "transcriptionMode";
    private const string PluginVersionValue = "1.3.0";
    private const string SmartModeSettingValue = "smart";
    private const string VerbatimModeSettingValue = "verbatim";

    internal const string DefaultModel = "gemini-flash-lite-latest";
    internal const string DefaultTranscriptionModel = "gemini-3.5-transcribe";
    internal const string DefaultLiveTranscriptionModel = "gemini-3.5-transcribe-live";
    internal static readonly TimeSpan ModelCatalogRefreshInterval = TimeSpan.FromHours(24);

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new(DefaultModel, "Gemini Flash Lite Latest") { IsRecommended = true },
        new("gemini-flash-latest", "Gemini Flash Latest"),
        new("gemini-pro-latest", "Gemini Pro Latest"),
    ];

    private static readonly IReadOnlyList<GeminiFetchedTranscriptionModel> FallbackTranscriptionModels =
    [
        new(
            DefaultTranscriptionModel,
            "Gemini 3.5 Transcribe",
            DefaultLiveTranscriptionModel),
    ];

    private static readonly string[] ExcludedLlmModelTokens =
    [
        "embedding",
        "-image",
        "tts",
        "live",
        "audio",
        "transcribe",
        "robotics",
        "computer-use",
        "deep-research",
        "omni",
    ];

    private static readonly DictionaryTermsBudget DictionaryBudget = new(
        MaxTerms: 100,
        MaxTotalChars: 4_000);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private List<GeminiFetchedModel> _fetchedLlmModels = [];
    private List<GeminiFetchedTranscriptionModel> _fetchedTranscriptionModels = [];
    private DateTimeOffset? _modelCatalogFetchedAt;
    private string _selectedTranscriptionModelId = DefaultTranscriptionModel;
    private GeminiTranscriptionMode _transcriptionMode = GeminiTranscriptionMode.Smart;

    /// <summary>
    /// Initializes a new instance of the Gemini plugin.
    /// </summary>
    public GeminiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
    }

    internal GeminiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public bool SupportsRequestHedging => true;

    // ITypeWhisperPlugin

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.gemini";

    /// <inheritdoc />
    public string PluginName => "Google Gemini";

    /// <inheritdoc />
    public string PluginVersion => PluginVersionValue;

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<GeminiFetchedModel>>(FetchedLlmModelsSettingName) ?? []);
        _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(
            host.GetSetting<List<GeminiFetchedTranscriptionModel>>(FetchedTranscriptionModelsSettingName) ?? []);
        _modelCatalogFetchedAt = LoadCatalogFetchedAt(host);
        _transcriptionMode = ParseTranscriptionMode(
            host.GetSetting<string>(TranscriptionModeSettingName));
        _selectedTranscriptionModelId = NormalizeSelectedTranscriptionModelId(
            host.GetSetting<string>(SelectedTranscriptionModelSettingName));

        host.Log(
            PluginLogLevel.Info,
            $"Activated (configured={IsAvailable}, llmModels={_fetchedLlmModels.Count}, " +
            $"transcriptionModels={_fetchedTranscriptionModels.Count})");
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public UserControl? CreateSettingsView() => new GeminiSettingsView(this);

    // ITranscriptionEnginePlugin

    /// <inheritdoc />
    public string ProviderId => "gemini";

    /// <inheritdoc />
    public string ProviderDisplayName => "Google Gemini";

    /// <inheritdoc />
    public bool IsConfigured => IsAvailable;

    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var models = AvailableTranscriptionModels;
            var defaultId = ResolveDefaultTranscriptionModelId(models);
            return models
                .OrderByDescending(model => string.Equals(
                    model.Id,
                    defaultId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new PluginModelInfo(
                    model.Id,
                    model.DisplayName ?? FormatModelDisplayName(model.Id))
                {
                    IsRecommended = string.Equals(
                        model.Id,
                        defaultId,
                        StringComparison.OrdinalIgnoreCase),
                    LanguageCount = 85,
                })
                .ToList();
        }
    }

    /// <inheritdoc />
    public string? SelectedModelId => _selectedTranscriptionModelId;

    /// <inheritdoc />
    public bool SupportsTranslation => false;

    /// <inheritdoc />
    public bool SupportsStreaming => SelectedTranscriptionModel?.LiveModelId is not null;

    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;

    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => DictionaryBudget;

    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt) =>
        SupportsStreaming && string.IsNullOrWhiteSpace(prompt);

    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        if (AvailableTranscriptionModels.All(model =>
            !string.Equals(model.Id, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown transcription model: {modelId}", nameof(modelId));
        }

        if (string.Equals(
            _selectedTranscriptionModelId,
            normalized,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedTranscriptionModelId = normalized;
        _host?.SetSetting(SelectedTranscriptionModelSettingName, normalized);
        _host?.NotifyCapabilitiesChanged();
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
            NormalizeLanguage(language) is { } normalizedLanguage ? [normalizedLanguage] : [],
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
        if (translate)
            throw new InvalidOperationException("Gemini Transcribe does not support translation.");
        if (!IsConfigured || SelectedTranscriptionModel is not { } model)
        {
            throw new PluginRequestException(
                "API key and transcription model required",
                PluginRequestFailureKind.Configuration);
        }

        return await GeminiTranscriptionClient.TranscribeAsync(
            _httpClient,
            NativeBaseUrl,
            _apiKey!,
            model.Id,
            wavAudio,
            NormalizeLanguageHints(languageHints),
            ExtractVocabulary(prompt),
            _transcriptionMode,
            (level, message) => _host?.Log(level, message),
            ct);
    }

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(
            NormalizeLanguage(language) is { } normalizedLanguage ? [normalizedLanguage] : [],
            ct);

    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);
        }

        if (SelectedTranscriptionModel?.LiveModelId is not { } liveModelId)
            throw new NotSupportedException("The selected Gemini transcription model does not support live streaming.");

        return await GeminiStreamingSession.ConnectAsync(
            _apiKey!,
            liveModelId,
            NormalizeLanguageHints(languageHints),
            customVocabulary: [],
            _transcriptionMode,
            ct);
    }

    // ILlmProviderPlugin

    /// <inheritdoc />
    public string ProviderName => "Google Gemini";

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            if (_fetchedLlmModels.Count == 0)
                return FallbackLlmModels;

            var defaultModelId = ResolveDefaultLlmModelId(_fetchedLlmModels);
            return _fetchedLlmModels
                .OrderByDescending(model => string.Equals(
                    model.Id,
                    defaultModelId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new PluginModelInfo(model.Id, model.DisplayName ?? FormatModelDisplayName(model.Id))
                {
                    IsRecommended = string.Equals(
                        model.Id,
                        defaultModelId,
                        StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
        }
    }

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
            ? SupportedModels.First().Id
            : NormalizeModelId(model);
        return await SendChatCompletionAsync(modelId, systemPrompt, userText, ct);
    }

    // Settings and model catalog management

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal IReadOnlyList<GeminiFetchedModel> FetchedLlmModels => _fetchedLlmModels;
    internal IReadOnlyList<GeminiFetchedTranscriptionModel> FetchedTranscriptionModels =>
        _fetchedTranscriptionModels;
    internal DateTimeOffset? ModelCatalogFetchedAt => _modelCatalogFetchedAt;
    internal GeminiTranscriptionMode TranscriptionMode => _transcriptionMode;

    internal bool ShouldRefreshModelCatalog(DateTimeOffset now) =>
        IsAvailable
        && (_modelCatalogFetchedAt is not { } fetchedAt
            || now - fetchedAt >= ModelCatalogRefreshInterval
            || _fetchedLlmModels.Count == 0
            || _fetchedTranscriptionModels.Count == 0);

    internal async Task SetApiKeyAsync(string apiKey)
    {
        await _configurationGate.WaitAsync();
        try
        {
            var normalized = NormalizeApiKey(apiKey);
            var previousApiKey = _apiKey;
            var wasAvailable = IsAvailable;
            var changed = !string.Equals(previousApiKey, normalized, StringComparison.Ordinal);
            if (!changed)
                return;

            var previousLlmModels = _fetchedLlmModels;
            var previousTranscriptionModels = _fetchedTranscriptionModels;
            var previousFetchedAt = _modelCatalogFetchedAt;
            var previousSelectedModelId = _selectedTranscriptionModelId;
            var nextSelectedModelId = NormalizeSelectedTranscriptionModelId(
                previousSelectedModelId,
                FallbackTranscriptionModels);
            var selectionChanged = !string.Equals(
                previousSelectedModelId,
                nextSelectedModelId,
                StringComparison.OrdinalIgnoreCase);
            var catalogChanged = previousLlmModels.Count > 0
                || previousTranscriptionModels.Count > 0
                || previousFetchedAt is not null;

            if (_host is not null)
            {
                var secretPersisted = false;
                try
                {
                    await PersistApiKeyAsync(_host, normalized);
                    secretPersisted = true;
                    if (catalogChanged)
                    {
                        PersistCatalogSettings(
                            _host,
                            llmModels: [],
                            transcriptionModels: [],
                            fetchedAt: null);
                    }
                    if (selectionChanged)
                    {
                        _host.SetSetting(
                            SelectedTranscriptionModelSettingName,
                            nextSelectedModelId);
                    }
                }
                catch (Exception writeException)
                {
                    var rollbackFailures = new List<Exception>();
                    if (secretPersisted)
                    {
                        try { await PersistApiKeyAsync(_host, previousApiKey); }
                        catch (Exception rollbackException) { rollbackFailures.Add(rollbackException); }
                    }

                    if (catalogChanged)
                    {
                        try
                        {
                            PersistCatalogSettings(
                                _host,
                                previousLlmModels,
                                previousTranscriptionModels,
                                previousFetchedAt);
                            if (selectionChanged)
                            {
                                _host.SetSetting(
                                    SelectedTranscriptionModelSettingName,
                                    previousSelectedModelId);
                            }
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (rollbackFailures.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Failed to persist the API key and restore the previous state.",
                            new AggregateException([writeException, .. rollbackFailures]));
                    }

                    throw;
                }
            }

            _apiKey = normalized;
            _fetchedLlmModels = [];
            _fetchedTranscriptionModels = [];
            _modelCatalogFetchedAt = null;
            _selectedTranscriptionModelId = nextSelectedModelId;

            if (_host is not null && (wasAvailable != IsAvailable || catalogChanged))
                _host.NotifyCapabilitiesChanged();
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    internal void SetTranscriptionMode(GeminiTranscriptionMode mode)
    {
        if (_transcriptionMode == mode)
            return;

        _transcriptionMode = mode;
        _host?.SetSetting(TranscriptionModeSettingName, FormatTranscriptionMode(mode));
    }

    internal async Task<GeminiModelCatalog?> FetchModelCatalogAsync(CancellationToken ct = default)
    {
        var apiKey = _apiKey;
        if (apiKey is null)
            return null;

        try
        {
            var nativeModels = new List<GeminiNativeModel>();
            string? pageToken = null;
            do
            {
                var url = $"{NativeBaseUrl}/models?pageSize=1000";
                if (!string.IsNullOrWhiteSpace(pageToken))
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                using var request = CreateNativeRequest(HttpMethod.Get, url, apiKey);
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"Model catalog request failed with HTTP status {(int)response.StatusCode}.");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var page = JsonSerializer.Deserialize<GeminiNativeModelsResponse>(json, JsonOptions);
                if (page?.Models is null)
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        "Model catalog response did not contain a models array.");
                    return null;
                }

                nativeModels.AddRange(page.Models.OfType<GeminiNativeModel>());
                pageToken = string.IsNullOrWhiteSpace(page.NextPageToken)
                    ? null
                    : page.NextPageToken;
            }
            while (pageToken is not null);

            if (!string.Equals(_apiKey, apiKey, StringComparison.Ordinal))
            {
                _host?.Log(
                    PluginLogLevel.Debug,
                    "Discarded a model catalog fetched for a previous API key.");
                return null;
            }

            return new GeminiModelCatalog(
                NormalizeFetchedLlmModels(nativeModels),
                NormalizeFetchedTranscriptionModels(nativeModels),
                DateTimeOffset.UtcNow);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _host?.Log(PluginLogLevel.Warning, "Model catalog request timed out.");
            return null;
        }
        catch (OperationCanceledException)
        {
            _host?.Log(PluginLogLevel.Debug, "Model catalog request was canceled by the caller.");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog request failed with {ex.GetType().Name}.");
            return null;
        }
        catch (JsonException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog parsing failed with {ex.GetType().Name}.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog request failed with {ex.GetType().Name}.");
            return null;
        }
    }

    internal async Task<List<GeminiFetchedModel>?> FetchLlmModelsAsync(CancellationToken ct = default) =>
        (await FetchModelCatalogAsync(ct))?.LlmModels.ToList();

    internal async Task<bool> SetFetchedModelCatalogAsync(
        GeminiModelCatalog catalog,
        string? expectedApiKey = null)
    {
        var normalizedLlmModels = NormalizeFetchedLlmModels(catalog.LlmModels);
        var normalizedTranscriptionModels = NormalizeFetchedTranscriptionModels(
            catalog.TranscriptionModels);
        var normalizedFetchedAt = catalog.FetchedAtUtc.ToUniversalTime();
        var availableTranscriptionModels = normalizedTranscriptionModels.Count > 0
            ? normalizedTranscriptionModels
            : FallbackTranscriptionModels;
        var nextSelectedModelId = NormalizeSelectedTranscriptionModelId(
            _selectedTranscriptionModelId,
            availableTranscriptionModels);
        var selectionChanged = !string.Equals(
            _selectedTranscriptionModelId,
            nextSelectedModelId,
            StringComparison.OrdinalIgnoreCase);

        await _configurationGate.WaitAsync();
        try
        {
            if (expectedApiKey is not null
                && !string.Equals(_apiKey, expectedApiKey, StringComparison.Ordinal))
            {
                _host?.Log(
                    PluginLogLevel.Debug,
                    "Discarded a model catalog fetched for a previous API key.");
                return false;
            }

            var catalogsChanged = !ModelCatalogsEqual(_fetchedLlmModels, normalizedLlmModels)
                || !TranscriptionModelCatalogsEqual(
                    _fetchedTranscriptionModels,
                    normalizedTranscriptionModels);

            if (_host is not null)
            {
                var previousLlmModels = _fetchedLlmModels;
                var previousTranscriptionModels = _fetchedTranscriptionModels;
                var previousFetchedAt = _modelCatalogFetchedAt;
                try
                {
                    PersistCatalogSettings(
                        _host,
                        normalizedLlmModels,
                        normalizedTranscriptionModels,
                        normalizedFetchedAt);
                    if (selectionChanged)
                    {
                        _host.SetSetting(
                            SelectedTranscriptionModelSettingName,
                            nextSelectedModelId);
                    }
                }
                catch (Exception writeException)
                {
                    try
                    {
                        PersistCatalogSettings(
                            _host,
                            previousLlmModels,
                            previousTranscriptionModels,
                            previousFetchedAt);
                        if (selectionChanged)
                        {
                            _host.SetSetting(
                                SelectedTranscriptionModelSettingName,
                                _selectedTranscriptionModelId);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "Failed to persist the model catalog and restore the previous value.",
                            new AggregateException(writeException, rollbackException));
                    }

                    throw;
                }
            }

            _fetchedLlmModels = normalizedLlmModels;
            _fetchedTranscriptionModels = normalizedTranscriptionModels;
            _modelCatalogFetchedAt = normalizedFetchedAt;

            _selectedTranscriptionModelId = nextSelectedModelId;

            if (catalogsChanged || selectionChanged)
                _host?.NotifyCapabilitiesChanged();
            return true;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    internal async Task<bool> SetFetchedLlmModelsAsync(
        IEnumerable<GeminiFetchedModel> models,
        string? expectedApiKey = null)
    {
        var catalog = new GeminiModelCatalog(
            models.ToList(),
            _fetchedTranscriptionModels,
            DateTimeOffset.UtcNow);
        return await SetFetchedModelCatalogAsync(catalog, expectedApiKey);
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        using var request = CreateNativeRequest(
            HttpMethod.Get,
            $"{NativeBaseUrl}/models?pageSize=1",
            normalized);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsCompatibleChatModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var normalized = NormalizeModelId(id);
        if (!normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ExcludedLlmModelTokens.Any(token =>
            normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsCompatibleTranscriptionModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var normalized = NormalizeModelId(id);
        return normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("-transcribe", StringComparison.OrdinalIgnoreCase)
            && !IsLiveTranscriptionModelId(normalized);
    }

    internal static IReadOnlyList<string> ExtractVocabulary(string? prompt) =>
        PluginDictionaryTerms.Clip(
            string.IsNullOrWhiteSpace(prompt)
                ? []
                : prompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            DictionaryBudget);

    private async Task<string> SendChatCompletionAsync(
        string model,
        string systemPrompt,
        string userText,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            },
            ["max_tokens"] = LlmOutputTokenBudget.Calculate(systemPrompt, userText),
        };
        if (model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            body["reasoning_effort"] = "low";

        using var request = CreateOpenAiCompatibleRequest(
            HttpMethod.Post,
            $"{OpenAiBaseUrl}/chat/completions",
            _apiKey!);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json);
    }

    private static string ParseChatCompletionResponse(string json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new PluginRequestException(
                    "The provider returned a malformed response.",
                    PluginRequestFailureKind.EmptyResponse,
                    innerException: ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "Gemini");
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        throw new PluginRequestException(
            "The provider returned an empty response.",
            PluginRequestFailureKind.EmptyResponse);
    }

    private IReadOnlyList<GeminiFetchedTranscriptionModel> AvailableTranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels
            : FallbackTranscriptionModels;

    private GeminiFetchedTranscriptionModel? SelectedTranscriptionModel =>
        AvailableTranscriptionModels.FirstOrDefault(model => string.Equals(
            model.Id,
            _selectedTranscriptionModelId,
            StringComparison.OrdinalIgnoreCase));

    private string NormalizeSelectedTranscriptionModelId(string? modelId)
        => NormalizeSelectedTranscriptionModelId(modelId, AvailableTranscriptionModels);

    private static string NormalizeSelectedTranscriptionModelId(
        string? modelId,
        IReadOnlyList<GeminiFetchedTranscriptionModel> available)
    {
        var normalized = string.IsNullOrWhiteSpace(modelId)
            ? null
            : NormalizeModelId(modelId);
        return available.FirstOrDefault(model => string.Equals(
                model.Id,
                normalized,
                StringComparison.OrdinalIgnoreCase))?.Id
            ?? ResolveDefaultTranscriptionModelId(available);
    }

    private static Task PersistApiKeyAsync(IPluginHostServices host, string? apiKey) =>
        apiKey is null
            ? host.DeleteSecretAsync(ApiKeySecretName)
            : host.StoreSecretAsync(ApiKeySecretName, apiKey);

    private static void PersistCatalogSettings(
        IPluginHostServices host,
        IReadOnlyList<GeminiFetchedModel> llmModels,
        IReadOnlyList<GeminiFetchedTranscriptionModel> transcriptionModels,
        DateTimeOffset? fetchedAt)
    {
        host.SetSetting(FetchedLlmModelsSettingName, llmModels.ToList());
        host.SetSetting(FetchedTranscriptionModelsSettingName, transcriptionModels.ToList());
        host.SetSetting(ModelCatalogFetchedAtSettingName, fetchedAt);
    }

    private static HttpRequestMessage CreateOpenAiCompatibleRequest(
        HttpMethod method,
        string url,
        string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    internal static HttpRequestMessage CreateNativeRequest(
        HttpMethod method,
        string url,
        string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        return request;
    }

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiNativeModel> models) =>
        NormalizeFetchedLlmModels(models
            .Select(model => new GeminiFetchedModel(
                ResolveNativeModelId(model),
                model.DisplayName)));

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiFetchedModel> models) =>
        models
            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                NormalizeModelId(model.Id),
                string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim()))
            .Where(model => IsCompatibleChatModelId(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(model => string.Equals(
                model.Id,
                DefaultModel,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<GeminiFetchedTranscriptionModel> NormalizeFetchedTranscriptionModels(
        IEnumerable<GeminiNativeModel> models)
    {
        var available = models
            .Select(model => new GeminiFetchedModel(
                ResolveNativeModelId(model),
                model.DisplayName))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var liveModelIds = available
            .Where(model => IsLiveTranscriptionModelId(model.Id))
            .Select(model => model.Id)
            .ToList();

        return NormalizeFetchedTranscriptionModels(available
            .Where(model => IsCompatibleTranscriptionModelId(model.Id))
            .Select(model => new GeminiFetchedTranscriptionModel(
                model.Id,
                model.DisplayName,
                ResolveLiveSibling(model.Id, liveModelIds))));
    }

    private static List<GeminiFetchedTranscriptionModel> NormalizeFetchedTranscriptionModels(
        IEnumerable<GeminiFetchedTranscriptionModel> models) =>
        models
            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedTranscriptionModel(
                NormalizeModelId(model.Id),
                string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(model.LiveModelId)
                    ? null
                    : NormalizeModelId(model.LiveModelId)))
            .Where(model => IsCompatibleTranscriptionModelId(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? ResolveLiveSibling(
        string unaryModelId,
        IReadOnlyList<string> liveModelIds) =>
        liveModelIds.FirstOrDefault(liveModelId => string.Equals(
                liveModelId,
                unaryModelId + "-live",
                StringComparison.OrdinalIgnoreCase));

    private static string ResolveDefaultLlmModelId(IReadOnlyList<GeminiFetchedModel> models)
    {
        var alias = models.FirstOrDefault(model => string.Equals(
            model.Id,
            DefaultModel,
            StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
            return alias.Id;

        return models
            .Where(model => model.Id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                && model.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)
                && !model.Id.Contains("lite", StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .FirstOrDefault()
            ?? models[0].Id;
    }

    private static string ResolveDefaultTranscriptionModelId(
        IReadOnlyList<GeminiFetchedTranscriptionModel> models) =>
        models
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .First();

    private static Version GetModelVersion(string id)
    {
        const string prefix = "gemini-";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new Version(0, 0);

        var versionEnd = id.IndexOf('-', prefix.Length);
        if (versionEnd <= prefix.Length)
            return new Version(0, 0);

        var versionText = id[prefix.Length..versionEnd];
        if (!versionText.Contains('.'))
            versionText += ".0";

        return Version.TryParse(versionText, out var version)
            ? version
            : new Version(0, 0);
    }

    private static bool ModelCatalogsEqual(
        IReadOnlyList<GeminiFetchedModel> first,
        IReadOnlyList<GeminiFetchedModel> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal));

    private static bool TranscriptionModelCatalogsEqual(
        IReadOnlyList<GeminiFetchedTranscriptionModel> first,
        IReadOnlyList<GeminiFetchedTranscriptionModel> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
            && string.Equals(pair.First.LiveModelId, pair.Second.LiveModelId, StringComparison.OrdinalIgnoreCase));

    private static string ResolveNativeModelId(GeminiNativeModel model) =>
        !string.IsNullOrWhiteSpace(model.BaseModelId)
            ? model.BaseModelId!
            : model.Name ?? "";

    private static string NormalizeModelId(string id)
    {
        var normalized = id.Trim();
        return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? normalized["models/".Length..]
            : normalized;
    }

    private static bool IsLiveTranscriptionModelId(string id)
    {
        var normalized = NormalizeModelId(id);
        return normalized.Contains("-transcribe-live", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            || normalized.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : normalized;
    }

    private static IReadOnlyList<string> NormalizeLanguageHints(
        IReadOnlyList<string> languageHints)
    {
        var normalized = new List<string>();
        foreach (var languageHint in languageHints)
        {
            if (NormalizeLanguage(languageHint) is not { } value
                || normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static string FormatModelDisplayName(string modelId) =>
        string.Join(' ', NormalizeModelId(modelId)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0
                ? part
                : char.ToUpperInvariant(part[0]) + part[1..]));

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static GeminiTranscriptionMode ParseTranscriptionMode(string? mode) =>
        string.Equals(mode, VerbatimModeSettingValue, StringComparison.OrdinalIgnoreCase)
            ? GeminiTranscriptionMode.Verbatim
            : GeminiTranscriptionMode.Smart;

    private static string FormatTranscriptionMode(GeminiTranscriptionMode mode) =>
        mode == GeminiTranscriptionMode.Verbatim
            ? VerbatimModeSettingValue
            : SmartModeSettingValue;

    private static DateTimeOffset? LoadCatalogFetchedAt(IPluginHostServices host)
    {
        try
        {
            var value = host.GetSetting<DateTimeOffset?>(ModelCatalogFetchedAtSettingName);
            return value == default ? null : value?.ToUniversalTime();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _configurationGate.Dispose();
        _httpClient.Dispose();
    }
}

internal enum GeminiTranscriptionMode
{
    Smart,
    Verbatim,
}

internal sealed record GeminiFetchedModel(string Id, string? DisplayName);

internal sealed record GeminiFetchedTranscriptionModel(
    string Id,
    string? DisplayName,
    string? LiveModelId);

internal sealed record GeminiModelCatalog(
    IReadOnlyList<GeminiFetchedModel> LlmModels,
    IReadOnlyList<GeminiFetchedTranscriptionModel> TranscriptionModels,
    DateTimeOffset FetchedAtUtc);

internal sealed record GeminiNativeModelsResponse(
    [property: JsonPropertyName("models")] List<GeminiNativeModel?>? Models,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

internal sealed record GeminiNativeModel(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("baseModelId")] string? BaseModelId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("supportedGenerationMethods")]
    IReadOnlyList<string>? SupportedGenerationMethods);
