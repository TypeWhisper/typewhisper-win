using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Groq;

/// <summary>
/// Provides groq plugin behavior.
/// </summary>
public sealed class GroqPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ILlmRequestHedgingSupport, IApiKeyPlugin, IPluginInstallationLifecycle, IPluginSettingsActions
{
    private const string BaseUrl = "https://api.groq.com/openai";
    private const int TranscriptionUploadBitRate = 48_000;
    private const float TerminalHallucinationNoSpeechThreshold = 0.8f;
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTranscriptionHttpTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient _httpClient;
    private readonly HttpClient _transcriptionHttpClient;
    private readonly Func<byte[], GroqTranscriptionUpload> _compressedUploadFactory;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedApiModelName;
    private string? _selectedLlmModelId;
    private string? _preferredLlmModelId;
    private List<FetchedLlmModel> _fetchedLlmModels = [];
    private DateTimeOffset? _fetchedLlmModelsAt;
    private readonly TimeProvider _clock;
    private readonly bool _refreshLlmModelsAutomatically;
    private readonly object _llmModelsLock = new();
    private CancellationTokenSource? _llmModelRefreshCts;

    private const string FetchedLlmModelsSetting = "fetchedLlmModels";
    private const string FetchedLlmModelsAtSetting = "fetchedLlmModelsAtUtc";
    private const string RefreshLlmModelsActionId = "refreshModels";

    /// <summary>Groq's featured production model and its named replacement for the retired Llama models.</summary>
    internal const string DefaultLlmModelId = "openai/gpt-oss-120b";

    /// <summary>How long a fetched model list counts as current before it is reloaded in the background.</summary>
    internal static readonly TimeSpan LlmModelRefreshInterval = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public bool SupportsRequestHedging => true;

    private static readonly IReadOnlyList<TranscriptionModelEntry> TranscriptionModelEntries =
    [
        new("whisper-large-v3", "Whisper Large V3", "whisper-large-v3", SupportsTranslation: true),
        new("whisper-large-v3-turbo", "Whisper Large V3 Turbo", "whisper-large-v3-turbo", SupportsTranslation: false),
    ];

    // Groq's production chat models come first, so a fresh install defaults to GPT-OSS 120B.
    // Llama 3.3 70B, Llama 3.1 8B and Kimi K2 were retired for free and developer accounts.
    // Refresh fetches the full list, including preview models.
    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new("openai/gpt-oss-120b", "GPT-OSS 120B"),
        new("openai/gpt-oss-20b", "GPT-OSS 20B"),
        new("qwen/qwen3.8-27b", "Qwen 3.8 27B"),
    ];

    /// <summary>
    /// Initializes a new instance of the GroqPlugin class.
    /// </summary>
    public GroqPlugin()
        : this(CreateHttpClient(), CreateTranscriptionHttpClient(), refreshLlmModelsAutomatically: true)
    {
    }

    internal GroqPlugin(HttpClient httpClient)
        : this(httpClient, httpClient)
    {
    }

    internal GroqPlugin(
        HttpClient httpClient,
        HttpClient transcriptionHttpClient,
        Func<byte[], GroqTranscriptionUpload>? compressedUploadFactory = null,
        bool refreshLlmModelsAutomatically = false,
        TimeProvider? clock = null)
    {
        _httpClient = httpClient;
        _transcriptionHttpClient = transcriptionHttpClient;
        _compressedUploadFactory = compressedUploadFactory ?? CreateCompressedUpload;
        _refreshLlmModelsAutomatically = refreshLlmModelsAutomatically;
        _clock = clock ?? TimeProvider.System;
    }

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.groq";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Groq";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.2.0";

    /// <inheritdoc />
    public Task OnInstallAsync(PluginInstallationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Progress?.Report(new("Groq uses cloud models. Add or review your API key in plugin settings."));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnUninstallAsync(PluginInstallationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Progress?.Report(new("Keeping your Groq API key and preferences for reinstallation."));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? TranscriptionModelEntries[0].Id;
        _preferredLlmModelId = host.GetSetting<string>("selectedLlmModel");
        _fetchedLlmModels = NormalizeFetchedLlmModels(host.GetSetting<List<FetchedLlmModel>>(FetchedLlmModelsSetting) ?? []);
        _fetchedLlmModelsAt = LoadFetchedLlmModelsAt(host);
        if (_fetchedLlmModels.Count > 0 && _fetchedLlmModelsAt is null)
        {
            // Lists saved before 1.2.0 have no fetch date and can still offer models Groq has retired since.
            _fetchedLlmModels = [];
            host.SetSetting(FetchedLlmModelsSetting, _fetchedLlmModels);
        }

        var selectedTranscription = TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId)
            ?? TranscriptionModelEntries[0];
        _selectedModelId = selectedTranscription.Id;
        _selectedApiModelName = selectedTranscription.ApiModelName;

        NormalizeSelectedLlmModel();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
        StartLlmModelRefreshIfStale();
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        CancelLlmModelRefresh();
        _host = null;
        return Task.CompletedTask;
    }


    // Whisper language tokens shared by the two multilingual models.
    private static readonly IReadOnlyList<string> TranscriptionLanguageCodes =
        "en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue".Split(' ');

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => TranscriptionLanguageCodes;

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "groq";

    /// <summary>Portable WAV upload limit before provider-specific compression.</summary>
    public int MaximumAudioUploadBytes => 25_000_000;
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Groq";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        TranscriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName) { Publisher = "OpenAI", LanguageCodes = TranscriptionLanguageCodes, LanguageCount = TranscriptionLanguageCodes.Count }).ToList();

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation
    {
        get
        {
            if (!IsConfigured || _selectedModelId is null)
                return false;
            var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId);
            return entry?.SupportsTranslation ?? false;
        }
    }

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");
        _host?.SetSetting("selectedModel", modelId);
        _selectedModelId = modelId;
        _selectedApiModelName = entry.ApiModelName;
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedApiModelName is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        if (translate && !SupportsTranslation)
            throw new NotSupportedException("This Groq model does not support translation.");

        GroqTranscriptionUpload upload;
        try
        {
            ct.ThrowIfCancellationRequested();
            upload = _compressedUploadFactory(wavAudio);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw CreateCompressionException(ex);
        }
        catch (IOException)
        {
            upload = CreateWavUpload(wavAudio);
        }
        catch (InvalidOperationException)
        {
            upload = CreateWavUpload(wavAudio);
        }
        catch (COMException)
        {
            upload = CreateWavUpload(wavAudio);
        }

        return await TranscribeUploadAsync(
            _transcriptionHttpClient,
            BaseUrl,
            _apiKey!,
            _selectedApiModelName,
            upload,
            language,
            translate,
            "verbose_json",
            ct,
            prompt);
    }

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "Groq";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => IsConfigured;

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList()
            : FallbackLlmModels;

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);

        var modelId = ResolveLlmModelId(string.IsNullOrWhiteSpace(model) ? null : model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, modelId, systemPrompt, userText, ct,
            maxOutputTokens: LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            maxOutputTokenParameter: "max_tokens",
            reasoningEffort: null,
            temperature: 0.1,
            reasoningFormat: modelId.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ? "hidden" : null);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<FetchedLlmModel> FetchedLlmModels => _fetchedLlmModels;

    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        var normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(_apiKey, normalizedApiKey, StringComparison.Ordinal);

        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", normalizedApiKey!);

            _apiKey = normalizedApiKey;

            if (changed)
            {
                // A list fetched with another key can offer models this account cannot use.
                CancelLlmModelRefresh();
                if (_fetchedLlmModels.Count > 0 || _fetchedLlmModelsAt is not null)
                    ApplyFetchedLlmModels([], fetchedAt: null);
                StartLlmModelRefreshIfStale();
            }

            if (changed && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Save a Groq API key first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
    }

    internal void SelectLlmModel(string modelId)
    {
        _preferredLlmModelId = modelId;
        _selectedLlmModelId = modelId;
        _host?.SetSetting("selectedLlmModel", modelId);
    }

    internal void SetFetchedLlmModels(List<FetchedLlmModel> models) =>
        ApplyFetchedLlmModels(models, _clock.GetUtcNow());

    internal DateTimeOffset? FetchedLlmModelsAt => _fetchedLlmModelsAt;
    internal Task LlmModelRefreshTask { get; private set; } = Task.CompletedTask;

    private void ApplyFetchedLlmModels(IEnumerable<FetchedLlmModel> models, DateTimeOffset? fetchedAt)
    {
        lock (_llmModelsLock)
        {
            _fetchedLlmModels = NormalizeFetchedLlmModels(models);
            _fetchedLlmModelsAt = fetchedAt;
            _host?.SetSetting(FetchedLlmModelsSetting, _fetchedLlmModels);
            _host?.SetSetting(FetchedLlmModelsAtSetting, fetchedAt);
            NormalizeSelectedLlmModel();
        }

        _host?.NotifyCapabilitiesChanged();
    }

    internal bool IsLlmModelListStale(DateTimeOffset now) =>
        _fetchedLlmModelsAt is not { } fetchedAt
        || fetchedAt > now
        || now - fetchedAt >= LlmModelRefreshInterval;

    /// <summary>
    /// Groq retires models for free and developer accounts at short notice. Reloading the list
    /// once a day drops a retired model instead of failing every request with HTTP 404.
    /// </summary>
    private void StartLlmModelRefreshIfStale()
    {
        if (!_refreshLlmModelsAutomatically || _host is null || _apiKey is not { } apiKey
            || !IsLlmModelListStale(_clock.GetUtcNow()))
            return;

        CancelLlmModelRefresh();
        var cts = new CancellationTokenSource();
        // Read the token before publishing the source: a cancellation disposes it.
        var token = cts.Token;
        _llmModelRefreshCts = cts;
        LlmModelRefreshTask = Task.Run(() => RefreshLlmModelsInBackgroundAsync(apiKey, token));
    }

    private async Task RefreshLlmModelsInBackgroundAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            await RefreshLlmModelsAsync(apiKey, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Groq model refresh failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Returns the number of models now offered, or null when Groq returned no usable list.</summary>
    private async Task<int?> RefreshLlmModelsAsync(string apiKey, CancellationToken ct)
    {
        var models = await FetchLlmModelsAsync(apiKey, ct);
        if (models is not { Count: > 0 })
        {
            _host?.Log(PluginLogLevel.Warning, "Groq returned no usable model list. Keeping the current list.");
            return null;
        }

        lock (_llmModelsLock)
        {
            // Drop a result that arrives after deactivation or a key change.
            if (ct.IsCancellationRequested || _host is null || !string.Equals(_apiKey, apiKey, StringComparison.Ordinal))
                return null;

            ApplyFetchedLlmModels(models, _clock.GetUtcNow());
            return _fetchedLlmModels.Count;
        }
    }

    private void CancelLlmModelRefresh()
    {
        var cts = Interlocked.Exchange(ref _llmModelRefreshCts, null);
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new(RefreshLlmModelsActionId,
            Loc?.GetString("Settings.RefreshModels") ?? "Refresh models",
            Loc?.GetString("Settings.RefreshModelsHint") ?? "Load the text models your Groq account can use.")
        {
            Section = PluginSettingsSection.Connection,
        },
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != RefreshLlmModelsActionId)
            throw new ArgumentException("Unknown Groq action.", nameof(id));
        if (_apiKey is not { } apiKey)
            throw new PluginRequestException("API key not configured", PluginRequestFailureKind.Configuration);

        CancelLlmModelRefresh();
        int? count;
        try
        {
            count = await RefreshLlmModelsAsync(apiKey, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            count = null;
        }

        if (count is not { } modelCount)
            throw new PluginRequestException(
                Loc?.GetString("Settings.RefreshFailed") ?? "Could not fetch Groq LLM models. Keeping the saved/default list.",
                PluginRequestFailureKind.Network);

        return Loc?.GetString("Settings.ModelsFetched", modelCount) ?? $"Fetched {modelCount} Groq LLM model(s).";
    }

    internal Task<List<FetchedLlmModel>?> FetchLlmModelsAsync(CancellationToken ct = default) =>
        FetchLlmModelsAsync(_apiKey, ct);

    private async Task<List<FetchedLlmModel>?> FetchLlmModelsAsync(string? apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => new FetchedLlmModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null))
                .Where(m => IsLlmModel(m.Id))
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
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

    internal static bool IsLlmModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        var excluded = new[]
        {
            "whisper",
            "distil-whisper",
            "tool-use",
            "orpheus",
            "tts",
            "prompt-guard",
            "safeguard",
        };

        return !excluded.Any(lowered.Contains);
    }

    internal string ResolveLlmModelId(string? requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : _selectedLlmModelId ?? DefaultLlmModel();

    /// <summary>A fetched list is sorted by ID, so its first entry is not a sensible default.</summary>
    private string DefaultLlmModel()
    {
        var models = SupportedModels;
        return models.FirstOrDefault(m => string.Equals(m.Id, DefaultLlmModelId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? models[0].Id;
    }

    /// <summary>
    /// Uses the saved model only while the current list offers it, so a retired model falls back to the
    /// default instead of failing every request with HTTP 404. The saved value stays and applies again
    /// once a later list offers it, for example after a key change.
    /// </summary>
    private void NormalizeSelectedLlmModel()
    {
        var models = SupportedModels;
        _selectedLlmModelId = models
            .FirstOrDefault(m => string.Equals(m.Id, _preferredLlmModelId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? (models.Count > 0 ? DefaultLlmModel() : null);
    }

    private static List<FetchedLlmModel> NormalizeFetchedLlmModels(IEnumerable<FetchedLlmModel> models) =>
        models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && IsLlmModel(m.Id))
            .DistinctBy(m => m.Id)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DateTimeOffset? LoadFetchedLlmModelsAt(IPluginHostServices host)
    {
        try
        {
            var value = host.GetSetting<DateTimeOffset?>(FetchedLlmModelsAtSetting);
            return value == default ? null : value?.ToUniversalTime();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static HttpClient CreateTranscriptionHttpClient(HttpMessageHandler? handler = null) =>
        CreateHttpClient(DefaultTranscriptionHttpTimeout, handler);

    private static HttpClient CreateHttpClient() => CreateHttpClient(DefaultHttpTimeout);

    private static HttpClient CreateHttpClient(TimeSpan timeout, HttpMessageHandler? handler = null) =>
        handler is null
            ? new HttpClient { Timeout = timeout }
            : new HttpClient(handler) { Timeout = timeout };

    private static GroqTranscriptionUpload CreateCompressedUpload(byte[] wavAudio)
    {
        if (wavAudio.Length == 0)
            throw new InvalidOperationException("No WAV audio bytes were provided.");

        return CreateWavUpload(wavAudio);
    }

    private static GroqTranscriptionUpload CreateWavUpload(byte[] wavAudio)
    {
        if (wavAudio.Length == 0)
            throw new InvalidOperationException("No WAV audio bytes were provided.");

        return new GroqTranscriptionUpload(wavAudio, "audio.wav", "audio/wav");
    }

    private static async Task<PluginTranscriptionResult> TranscribeUploadAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        GroqTranscriptionUpload upload,
        string? language,
        bool translate,
        string responseFormat,
        CancellationToken ct,
        string? prompt)
    {
        var endpoint = translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(upload.Data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        content.Add(fileContent, "file", upload.FileName);
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(responseFormat), "response_format");

        // Translation targets English; Groq accepts no source-language hint here.
        // https://console.groq.com/docs/speech-to-text (optional translation language: en only)
        if (!translate && !string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    private static InvalidOperationException CreateCompressionException(Exception ex) =>
        new(
            $"Groq audio upload could not be compressed before transcription: {ex.Message}",
            ex);

    private static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var language = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
        var duration = root.TryGetProperty("duration", out var durEl) ? durEl.GetDouble() : 0;

        var parsedSegments = new List<ParsedTranscriptionSegment>();
        if (root.TryGetProperty("segments", out var segmentsEl)
            && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                var segmentText = seg.TryGetProperty("text", out var segTextEl)
                    ? segTextEl.GetString() ?? ""
                    : "";
                var start = seg.TryGetProperty("start", out var startEl)
                    ? startEl.GetDouble()
                    : 0;
                var end = seg.TryGetProperty("end", out var endEl)
                    ? endEl.GetDouble()
                    : 0;
                var noSpeechProbability = seg.TryGetProperty("no_speech_prob", out var nspEl)
                    && nspEl.ValueKind == JsonValueKind.Number
                    ? (float?)nspEl.GetDouble()
                    : null;
                parsedSegments.Add(new ParsedTranscriptionSegment(
                    new PluginTranscriptionSegment(segmentText, start, end),
                    noSpeechProbability));
            }
        }

        text = RemoveTerminalThankYouHallucination(text, parsedSegments);
        float? minNoSpeechProb = null;
        foreach (var segment in parsedSegments)
        {
            if (segment.NoSpeechProbability is not { } probability)
                continue;

            minNoSpeechProb = minNoSpeechProb is null
                ? probability
                : Math.Min(minNoSpeechProb.Value, probability);
        }

        return new PluginTranscriptionResult(
            text.Trim(),
            language,
            duration,
            minNoSpeechProb)
        {
            Segments = parsedSegments.Select(segment => segment.Segment).ToList()
        };
    }

    private static string RemoveTerminalThankYouHallucination(
        string text,
        List<ParsedTranscriptionSegment> segments)
    {
        if (segments.Count == 0)
            return text;

        var terminalSegment = segments[^1];
        if (terminalSegment.NoSpeechProbability is not > TerminalHallucinationNoSpeechThreshold
            || !IsThankYouOnly(terminalSegment.Segment.Text))
        {
            return text;
        }

        var trimmedText = text.Trim();
        var segmentText = terminalSegment.Segment.Text.Trim();
        if (segmentText.Length == 0
            || !trimmedText.EndsWith(segmentText, StringComparison.Ordinal))
        {
            return text;
        }

        var textWithoutSegment = trimmedText[..^segmentText.Length].TrimEnd();
        if (textWithoutSegment.Length == 0)
            return text;

        segments.RemoveAt(segments.Count - 1);
        return textWithoutSegment;
    }

    private static bool IsThankYouOnly(string text)
    {
        const string expected = "thankyou";
        var index = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;

            if (index >= expected.Length
                || char.ToLowerInvariant(ch) != expected[index])
            {
                return false;
            }

            index++;
        }

        return index == expected.Length;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        CancelLlmModelRefresh();
        _httpClient.Dispose();
        if (!ReferenceEquals(_httpClient, _transcriptionHttpClient))
            _transcriptionHttpClient.Dispose();
    }

    private sealed record TranscriptionModelEntry(
        string Id, string DisplayName, string ApiModelName, bool SupportsTranslation);

    private sealed record ParsedTranscriptionSegment(
        PluginTranscriptionSegment Segment,
        float? NoSpeechProbability);
}

internal sealed record GroqTranscriptionUpload(byte[] Data, string FileName, string ContentType);

internal sealed record FetchedLlmModel(string Id, string? OwnedBy);
