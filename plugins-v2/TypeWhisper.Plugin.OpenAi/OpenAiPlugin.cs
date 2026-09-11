using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.MediaFoundation;
using NAudio.Wave;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

/// <summary>
/// Provides open ai plugin behavior.
/// </summary>
public sealed partial class OpenAiPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ILlmRequestHedgingSupport, ITtsProviderPlugin, IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    private const string BaseUrl = "https://api.openai.com";
    private const string ChatGptModelsEndpoint = "https://chatgpt.com/backend-api/codex/models";
    private const string PluginVersionValue = "1.1.4";
    private const int TranscriptionUploadBitRate = 48_000;
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string SelectedVoiceSettingName = "selectedVoice";
    private const string TtsInstructionsSettingName = "ttsInstructions";
    private const string ReasoningEffortSettingName = "reasoningEffort";
    private const string FetchedLlmModelsSettingName = "fetchedLLMModels";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels";
    private const string FetchedChatGptModelsSettingName = "fetchedChatGPTModels";
    private const string AuthModeSettingName = "authMode";
    private const string SelectedLlmModelSettingName = "selectedLLMModel";
    private const string TemperatureModeSettingName = "llmTemperatureMode";
    private const string TemperatureValueSettingName = "llmTemperatureValue";
    private const string TemperatureModeProviderDefault = "providerDefault";
    private const string TemperatureModeCustom = "custom";
    private const string OAuthSessionSecretName = "chatgpt-session";
    private sealed record SavedOAuthSession(string AccessToken, string RefreshToken, string? IdToken, string? AccountId, string? PlanType, DateTimeOffset? ExpiresAt);
    private const string OAuthAccessTokenSecretName = "oauth-access-token";

    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    private const string OAuthRefreshTokenSecretName = "oauth-refresh-token";
    private const string OAuthIdTokenSecretName = "oauth-id-token";
    private const string OAuthAccountIdSettingName = "oauthAccountID";
    private const string OAuthPlanTypeSettingName = "oauthPlanType";
    private const string OAuthExpiresAtSettingName = "oauthExpiresAt";
    private static readonly DictionaryTermsBudget DictionaryBudget = new(MaxTotalChars: 600);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _oauthRefreshLock = new(1, 1);
    private readonly Func<byte[], ITtsPlaybackSession>? _ttsPlaybackFactory;
    private readonly Func<byte[], OpenAiTranscriptionUpload> _compressedUploadFactory;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedApiModelName;
    private string? _selectedVoiceId;
    private string _ttsInstructions = "";
    private string _reasoningEffort = "medium";
    private string _temperatureMode = TemperatureModeProviderDefault;
    private double _temperatureValue = 0.3;
    private List<OpenAiFetchedModel> _fetchedLlmModels = [];
    private List<OpenAiFetchedModel> _fetchedTranscriptionModels = [];
    private List<OpenAiChatGptModel> _fetchedChatGptModels = [];
    private IReadOnlyList<TranscriptionModelEntry> _availableTranscriptionModelEntries = [];
    private OpenAiAuthMode _authMode = OpenAiAuthMode.ApiKey;
    private string? _selectedLlmModelId;
    private string? _oauthAccessToken;
    private string? _oauthRefreshToken;
    private string? _oauthIdToken;
    private string? _oauthAccountId;
    private string? _oauthPlanType;
    private DateTimeOffset? _oauthExpiresAt;

    private static readonly IReadOnlyList<TranscriptionModelEntry> FallbackTranscriptionModelEntries =
    [
        new(
            "gpt-transcribe",
            "GPT Transcribe",
            "gpt-transcribe",
            ResponseFormat: null,
            SupportsTranslation: false,
            LanguageFormat: TranscriptionLanguageFormat.Plural,
            SupportsDictionaryTerms: true),
        new("whisper-1", "Whisper 1", "whisper-1", "verbose_json", SupportsTranslation: true),
        new("gpt-4o-transcribe", "GPT-4o Transcribe", "gpt-4o-transcribe", "json", SupportsTranslation: false),
        new("gpt-4o-mini-transcribe", "GPT-4o Mini Transcribe", "gpt-4o-mini-transcribe", "json", SupportsTranslation: false),
        new(
            OpenAiRealtimeStreamingSession.LiveModelId,
            "GPT Live Transcribe",
            OpenAiRealtimeStreamingSession.LiveModelId,
            "json",
            SupportsTranslation: false,
            Transport: TranscriptionTransport.Realtime,
            LanguageFormat: TranscriptionLanguageFormat.Plural, SupportsDictionaryTerms: true),
        new(
            OpenAiRealtimeStreamingSession.LegacyModelId,
            "GPT Realtime Whisper",
            OpenAiRealtimeStreamingSession.LegacyModelId,
            "json",
            SupportsTranslation: false,
            Transport: TranscriptionTransport.Realtime),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new("gpt-5.5", "GPT-5.5"),
        new("gpt-4.1-nano", "GPT-4.1 Nano"),
        new("gpt-4.1-mini", "GPT-4.1 Mini"),
        new("gpt-4.1", "GPT-4.1"),
        new("gpt-4o", "GPT-4o"),
        new("gpt-4o-mini", "GPT-4o Mini"),
        new("o4-mini", "o4-mini"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackChatGptModels =
    [
        new("gpt-5.5", "GPT-5.5"),
        new("gpt-5.4", "GPT-5.4"),
        new("gpt-5.4-mini", "GPT-5.4 Mini"),
        new("gpt-5.4-nano", "GPT-5.4 Nano"),
        new("gpt-5.3-codex", "GPT-5.3 Codex"),
        new("gpt-5.3-codex-spark", "GPT-5.3 Codex Spark"),
        new("gpt-5.2", "GPT-5.2"),
        new("gpt-5.2-codex", "GPT-5.2 Codex"),
        new("gpt-5.1-codex", "GPT-5.1 Codex"),
        new("gpt-5.1-codex-max", "GPT-5.1 Codex Max"),
        new("gpt-5.1-codex-mini", "GPT-5.1 Codex Mini"),
    ];

    /// <summary>
    /// Initializes a new instance of the OpenAiPlugin class.
    /// </summary>
    public OpenAiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
    {
    }

    internal OpenAiPlugin(
        HttpClient httpClient,
        Func<byte[], ITtsPlaybackSession>? ttsPlaybackFactory = null,
        Func<byte[], OpenAiTranscriptionUpload>? compressedUploadFactory = null)
    {
        _httpClient = httpClient;
        _ttsPlaybackFactory = ttsPlaybackFactory;
        _compressedUploadFactory = compressedUploadFactory ?? CreateCompressedUpload;
    }

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.openai";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "OpenAI / ChatGPT";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => PluginVersionValue;

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _oauthAccessToken = NormalizeApiKey(await host.LoadSecretAsync(OAuthAccessTokenSecretName));
        _oauthRefreshToken = NormalizeApiKey(await host.LoadSecretAsync(OAuthRefreshTokenSecretName));
        _oauthIdToken = NormalizeApiKey(await host.LoadSecretAsync(OAuthIdTokenSecretName));
        _authMode = OpenAiAuthModeExtensions.Parse(host.GetSetting<string>(AuthModeSettingName));
        _selectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName);
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        _ttsInstructions = host.GetSetting<string>(TtsInstructionsSettingName) ?? "";
        _transcriptionContext = host.GetSetting<string>("transcriptionContext") ?? "";
        var delay = host.GetSetting<string>("liveDelay");
        _liveDelay = LiveDelays.Contains(delay) ? delay! : "low";
        _reasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingName));
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingName));
        _temperatureValue = NormalizeTemperatureValue(host.GetSetting<double?>(TemperatureValueSettingName));
        _fetchedLlmModels = host.GetSetting<List<OpenAiFetchedModel>>(FetchedLlmModelsSettingName) ?? [];
        _fetchedTranscriptionModels =
            host.GetSetting<List<OpenAiFetchedModel>>(FetchedTranscriptionModelsSettingName) ?? [];
        _fetchedChatGptModels =
            host.GetSetting<List<OpenAiChatGptModel>>(FetchedChatGptModelsSettingName) ?? [];
        _oauthAccountId = host.GetSetting<string>(OAuthAccountIdSettingName);
        _oauthPlanType = host.GetSetting<string>(OAuthPlanTypeSettingName);
        _oauthExpiresAt = LoadExpiresAt(host);
        if (await host.LoadSecretAsync(OAuthSessionSecretName) is { } savedSession)
        {
            // The bundled session is authoritative, even if corrupt: never revive old split credentials.
            ApplyOAuthSession(new("", "", null, null, null, null));
            try
            {
                var session = JsonSerializer.Deserialize<SavedOAuthSession>(savedSession);
                if (session is not null && !string.IsNullOrWhiteSpace(session.AccessToken) && !string.IsNullOrWhiteSpace(session.RefreshToken))
                    ApplyOAuthSession(session);
            }
            catch (JsonException) { host.Log(PluginLogLevel.Warning, "Stored ChatGPT login is invalid; sign in again."); }
        }

        ApplyTranscriptionCatalog(_fetchedTranscriptionModels, persist: false);
        SelectModelCore(
            host.GetSetting<string>(SelectedModelSettingName)
                ?? FallbackTranscriptionModelEntries[0].Id,
            persist: false);
        NormalizeSelectedLlmModel(
            persist: false,
            preserveUnknownWhenCatalogUnavailable: true);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        _apiKey = _oauthAccessToken = _oauthRefreshToken = _oauthIdToken = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "openai";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "OpenAI / ChatGPT";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets whether the host is running the isolated UI automation fixture.
    /// </summary>
    internal bool IsUiAutomation => _host?.IsUiAutomation == true;

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        AvailableTranscriptionModelEntries
            .Select(model => new PluginModelInfo(model.Id, model.DisplayName))
            .ToList();

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation =>
        IsConfigured
        && SelectedModelEntry is { SupportsTranslation: true };

    /// <summary>
    /// Gets whether the provider supports live streaming transcription.
    /// </summary>
    public bool SupportsStreaming =>
        IsConfigured
        && SelectedModelEntry is { SupportsStreaming: true };

    /// <summary>
    /// Gets whether active dictionary terms can be passed to the selected transcription model.
    /// </summary>
    public bool SupportsDictionaryTerms =>
        IsConfigured
        && SelectedModelEntry is { SupportsDictionaryTerms: true };

    /// <summary>
    /// Gets the safe conditioning-prompt budget used for active dictionary terms.
    /// </summary>
    public DictionaryTermsBudget DictionaryTermsBudget => DictionaryBudget;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId) => SelectModelCore(modelId, persist: true);

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(
            wavAudio,
            NormalizeLanguage(language) is { } normalizedLanguage ? [normalizedLanguage] : [],
            translate,
            prompt,
            ct);

    /// <summary>
    /// Transcribes PCM audio using ordered language hints.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (!IsConfigured || _selectedApiModelName is null || SelectedModelEntry is not { } entry)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        if (translate && !entry.SupportsTranslation)
            throw new InvalidOperationException($"{entry.DisplayName} does not support translation.");

        var normalizedLanguageHints = NormalizeLanguageHints(languageHints);
        if (entry.Transport == TranscriptionTransport.Realtime)
        {
            return await OpenAiRealtimeStreamingSession.TranscribeWavAsync(
                _apiKey!,
                entry.ApiModelName,
                wavAudio,
                normalizedLanguageHints,
                _transcriptionContext,
                ct, DictionaryKeywords(prompt), _liveDelay);
        }

        var preferredUpload = await Task.Run(() => CreatePreferredUpload(wavAudio, ct), ct);

        async Task<PluginTranscriptionResult> TranscribeUploadAsync(OpenAiTranscriptionUpload upload)
        {
            if (entry.LanguageFormat == TranscriptionLanguageFormat.Plural)
            {
                return await OpenAiTranscriptionClient.TranscribeAsync(
                    _httpClient,
                    BaseUrl,
                    _apiKey!,
                    entry.ApiModelName,
                    upload,
                    normalizedLanguageHints,
                    entry.ResponseFormat,
                    _transcriptionContext,
                    ct, DictionaryKeywords(prompt));
            }

            return await OpenAiTranscriptionTransport.TranscribeAsync(
                _httpClient,
                BaseUrl,
                _apiKey!,
                entry.ApiModelName,
                upload,
                normalizedLanguageHints.FirstOrDefault(),
                translate,
                entry.ResponseFormat ?? "json",
                ct,
                prompt);
        }

        try
        {
            return await TranscribeUploadAsync(preferredUpload);
        }
        catch (PluginRequestException ex) when (
            IsM4aUpload(preferredUpload)
            && ShouldRetryWithWav(ex))
        {
            ct.ThrowIfCancellationRequested();
            _host?.Log(
                PluginLogLevel.Warning,
                "OpenAI rejected the M4A upload; retrying with WAV.");
            return await TranscribeUploadAsync(CreateWavUpload(wavAudio));
        }
    }

    private OpenAiTranscriptionUpload CreatePreferredUpload(byte[] wavAudio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return _compressedUploadFactory(wavAudio);
        }
        catch (Exception ex) when (IsExpectedCompressionFailure(ex))
        {
            _host?.Log(
                PluginLogLevel.Warning,
                "OpenAI M4A encoding failed; falling back to WAV.");
            return CreateWavUpload(wavAudio);
        }
    }

    private static bool IsExpectedCompressionFailure(Exception ex) =>
        ex is InvalidDataException
        or FormatException
        or IOException
        or InvalidOperationException
        or COMException
        or NotSupportedException;

    internal static OpenAiTranscriptionUpload CreateCompressedUpload(byte[] wavAudio)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AAC encoding requires Windows Media Foundation; use WAV on this platform.");
        ArgumentNullException.ThrowIfNull(wavAudio);
        if (wavAudio.Length == 0)
            throw new InvalidOperationException("No WAV audio bytes were provided.");

        using var input = new MemoryStream(wavAudio, writable: false);
        using var reader = new WaveFileReader(input);
        using var output = new MemoryStream();
        MediaFoundationEncoder.EncodeToAac(reader, output, TranscriptionUploadBitRate);

        var bytes = output.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("Media Foundation produced an empty AAC upload.");

        return new OpenAiTranscriptionUpload(bytes, "audio.m4a", "audio/mp4");
    }

    private static OpenAiTranscriptionUpload CreateWavUpload(byte[] wavAudio) =>
        new(wavAudio, "audio.wav", "audio/wav");

    private static bool IsM4aUpload(OpenAiTranscriptionUpload upload) =>
        upload.FileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldRetryWithWav(PluginRequestException exception)
    {
        if (exception.HttpStatusCode == 415)
            return true;

        var message = exception.Message;
        if (exception.HttpStatusCode is 400 or 422)
            return IndicatesUnsupportedAudioUpload(message);

        return exception.HttpStatusCode == 500 && IndicatesMediaProbeFailure(message);
    }

    private static bool IndicatesUnsupportedAudioUpload(string message)
    {
        string[] rejectionTerms =
        [
            "unsupported", "not supported", "does not support", "invalid", "unrecognized",
            "unknown", "could not process", "failed to process", "corrupt"
        ];
        string[] mediaTerms =
        [
            "format", "media", "mime", "content-type", "content type", "codec", "container",
            "file type", "audio", "m4a", "mp4", "aac", "wav"
        ];

        return rejectionTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase))
            && mediaTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IndicatesMediaProbeFailure(string message)
    {
        string[] probeFailures =
        [
            "ffprobe failed", "ffprobe error", "ffprobe returned", "ffprobe exited",
            "ffmpeg failed", "ffmpeg error", "ffmpeg returned", "ffmpeg exited",
            "moov atom not found", "invalid data found when processing input"
        ];

        return probeFailures.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(
            NormalizeLanguage(language) is { } normalizedLanguage ? [normalizedLanguage] : [],
            ct);

    /// <summary>
    /// Opens a streaming transcription session with ordered language hints.
    /// </summary>
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);
        if (SelectedModelEntry is not { Transport: TranscriptionTransport.Realtime } entry)
            throw new NotSupportedException("Select an OpenAI realtime transcription model to use streaming.");

        return await OpenAiRealtimeStreamingSession.ConnectAsync(
            _apiKey!,
            entry.ApiModelName,
            NormalizeLanguageHints(languageHints),
            _transcriptionContext,
            ct, DictionaryKeywords(prompt), _liveDelay);
    }

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "OpenAI";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => _authMode switch
    {
        OpenAiAuthMode.ChatGpt => HasChatGptCredentials,
        _ => IsConfigured,
    };

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _authMode == OpenAiAuthMode.ChatGpt
            ? AvailableChatGptModels
            : _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(model => new PluginModelInfo(model.Id, model.Id)).ToList()
            : FallbackLlmModels;

    internal OpenAiAuthMode AuthMode => _authMode;
    internal bool HasChatGptCredentials =>
        !string.IsNullOrWhiteSpace(_oauthRefreshToken)
        || !string.IsNullOrWhiteSpace(_oauthAccessToken);
    internal string? ChatGptPlanType => _oauthPlanType;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal string ReasoningEffort => _reasoningEffort;
    internal string TemperatureMode => _temperatureMode;
    internal double TemperatureValue => _temperatureValue;

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels.First().Id
            : model;

        if (_authMode == OpenAiAuthMode.ChatGpt)
        {
            var accessToken = await ValidOAuthAccessTokenAsync(ct);
            var client = new OpenAiChatGptClient(_httpClient, accessToken, _oauthAccountId);
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                EffectiveReasoningEffort(modelId),
                ct);
        }

        if (!IsConfigured)
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);

        if (UsesResponsesApi(modelId))
        {
            var client = new OpenAiResponsesClient(_httpClient, BaseUrl, _apiKey!);
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                EffectiveReasoningEffort(modelId),
                ct, ResolvedTemperature(modelId));
        }

        return await OpenAiChatTransport.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: SupportsReasoningEffort(modelId)
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            maxOutputTokenParameter: OutputTokenParameter(modelId),
            reasoningEffort: EffectiveReasoningEffort(modelId),
            temperature: ResolvedTemperature(modelId));
    }

    internal static bool UsesResponsesApi(string modelId) =>
        modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || modelId.Equals("gpt-6-astra", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("gpt-6-astra-", StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsReasoningEffort(string modelId) => SupportedReasoningEfforts(modelId).Length > 0;

    // Matches the macOS plugin's per-model reasoning capabilities.
    internal static string[] SupportedReasoningEfforts(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("-chat")) return [];
        if (id == "gpt-6-astra" || id.StartsWith("gpt-6-astra-")) return ["low", "medium", "high", "xhigh", "max"];
        if (id.StartsWith("gpt-5.6")) return ["none", "low", "medium", "high", "xhigh", "max"];
        if (id.StartsWith("gpt-5.5-pro") || id.StartsWith("gpt-5.4-pro") || id.StartsWith("gpt-5.2-pro")) return ["medium", "high", "xhigh"];
        if (id.StartsWith("gpt-5-pro")) return ["high"];
        if (id.StartsWith("gpt-5.3-codex") || id.StartsWith("gpt-5.2-codex") || id.StartsWith("gpt-5.1-codex-max")) return ["low", "medium", "high", "xhigh"];
        if (id.Contains("codex")) return ["low", "medium", "high"];
        if (id.StartsWith("gpt-5.5") || id.StartsWith("gpt-5.4") || id.StartsWith("gpt-5.2")) return ["none", "low", "medium", "high", "xhigh"];
        if (id.StartsWith("gpt-5.1")) return ["none", "low", "medium", "high"];
        if (id.StartsWith("gpt-5")) return ["minimal", "low", "medium", "high"];
        if (id.StartsWith("o1-mini") || id.StartsWith("o1-preview") || id.StartsWith("o1-pro") || id.StartsWith("o3-pro")) return [];
        if (id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) return ["low", "medium", "high"];
        return [];
    }

    private string? EffectiveReasoningEffort(string modelId)
    {
        var supported = SupportedReasoningEfforts(modelId);
        if (supported.Contains(_reasoningEffort)) return _reasoningEffort;
        if (modelId.StartsWith("gpt-5.5-pro", StringComparison.OrdinalIgnoreCase) || modelId.StartsWith("gpt-5-pro", StringComparison.OrdinalIgnoreCase)) return "high";
        if (supported.Contains("none") && !modelId.StartsWith("gpt-5.5", StringComparison.OrdinalIgnoreCase)) return "none";
        return supported.Contains("medium") ? "medium" : supported.FirstOrDefault();
    }

    internal static string OutputTokenParameter(string modelId)
    {
        var lowered = modelId.ToLowerInvariant();
        return UsesResponsesApi(modelId)
            || lowered.StartsWith("o1", StringComparison.Ordinal)
            || lowered.StartsWith("o3", StringComparison.Ordinal)
            || lowered.StartsWith("o4", StringComparison.Ordinal)
            ? "max_completion_tokens"
            : "max_tokens";
    }

    internal static bool SupportsCustomTemperature(string modelId, string? reasoningEffort = null) =>
        ChatCompletionTemperature(modelId, reasoningEffort) is not null;

    internal static double? ChatCompletionTemperature(string modelId, string? reasoningEffort)
    {
        var lowered = modelId.ToLowerInvariant();
        if (lowered == "gpt-6-astra" || lowered.StartsWith("gpt-6-astra-")) return null;
        if (lowered.StartsWith("gpt-5", StringComparison.Ordinal))
            return (lowered.StartsWith("gpt-5.1") || lowered.StartsWith("gpt-5.2")) && reasoningEffort == "none" ? 0.3 : null;
        if (lowered.StartsWith("o1") || lowered.StartsWith("o3") || lowered.StartsWith("o4")) return null;

        return 0.3;
    }

    internal async Task<IReadOnlyList<PluginModelInfo>> RefreshAvailableLlmModelsAsync(CancellationToken ct = default)
    {
        if (_authMode == OpenAiAuthMode.ChatGpt)
        {
            var chatGptModels = await FetchChatGptModelsAsync(ct);
            if (chatGptModels is null || chatGptModels.Count == 0)
                return [];

            _fetchedChatGptModels = chatGptModels.ToList();
            _host?.SetSetting(FetchedChatGptModelsSettingName, _fetchedChatGptModels);
            NormalizeSelectedLlmModel(persist: true);
            _host?.NotifyCapabilitiesChanged();
            return SupportedModels;
        }

        var models = await FetchApiModelsAsync(ct);
        if (models is null)
            return [];

        _fetchedLlmModels = models
            .Where(model => IsChatModel(model.Id))
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ToList();
        _fetchedTranscriptionModels = models
            .Where(model => CreateDiscoveredTranscriptionModelEntry(model.Id) is not null)
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
        _host?.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
        ApplyTranscriptionCatalog(_fetchedTranscriptionModels, persist: true);
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
        return SupportedModels;
    }

    internal async Task<IReadOnlyList<OpenAiFetchedModel>?> FetchApiModelsAsync(
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenAiModelsResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (decoded?.Data is not { } apiModels)
                return null;

            return apiModels
                .OfType<OpenAiFetchedModel>()
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal async Task<IReadOnlyList<OpenAiChatGptModel>?> FetchChatGptModelsAsync(
        CancellationToken ct = default)
    {
        if (!HasChatGptCredentials)
            return null;

        try
        {
            var accessToken = await ValidOAuthAccessTokenAsync(ct);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ChatGptModelsEndpoint}?client_version={Uri.EscapeDataString(PluginVersionValue)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd($"TypeWhisper-OpenAI-Plugin/{PluginVersionValue}");
            request.Headers.TryAddWithoutValidation("originator", "typewhisper");
            if (!string.IsNullOrWhiteSpace(_oauthAccountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", _oauthAccountId);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenAiChatGptModelsResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (decoded?.Models is not { } catalogModels)
                return null;

            var visibleModels = catalogModels
                .OfType<OpenAiChatGptModel>()
                .Where(IsVisibleChatGptModel)
                .DistinctBy(model => model.Slug, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Priority ?? int.MaxValue)
                .ThenBy(model => model.Slug, StringComparer.Ordinal)
                .ToList();
            if (visibleModels.Count == 0)
                return null;

            if (string.IsNullOrWhiteSpace(_oauthPlanType))
                return visibleModels;

            var planModels = visibleModels
                .Where(model => model.AvailableInPlans is not { Count: > 0 }
                    || model.AvailableInPlans.Contains(_oauthPlanType, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return planModels.Count > 0 ? planModels : visibleModels;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
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

    internal static bool IsChatModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        var hasChatPrefix = lowered.StartsWith("gpt-", StringComparison.Ordinal)
            || lowered is "o1" or "o3"
            || lowered.StartsWith("o1-", StringComparison.Ordinal)
            || lowered.StartsWith("o3-", StringComparison.Ordinal)
            || lowered.StartsWith("o4-", StringComparison.Ordinal)
            || lowered.StartsWith("chatgpt-", StringComparison.Ordinal);
        if (!hasChatPrefix)
            return false;

        string[] excludeSuffixes = ["-tts", "-embedding"];
        string[] excludeContains =
        [
            "dall-e",
            "whisper",
            "transcribe",
            "tts-",
            "text-embedding",
            "audio",
            "realtime",
            "gpt-live",
            "-instruct",
            "gpt-image",
            "-search"
        ];
        return !excludeSuffixes.Any(suffix => lowered.EndsWith(suffix, StringComparison.Ordinal))
            && !excludeContains.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal));
    }

    internal static bool IsVisibleChatGptModel(OpenAiChatGptModel model) =>
        !string.IsNullOrWhiteSpace(model.Slug)
        && !string.Equals(model.Visibility, "hide", StringComparison.OrdinalIgnoreCase);

    internal void SetAuthMode(OpenAiAuthMode mode)
    {
        if (_authMode == mode)
            return;

        _host?.SetSetting(AuthModeSettingName, mode.ToStorageValue());
        _authMode = mode;
        NormalizeSelectedLlmModel(
            persist: true,
            preserveUnknownWhenCatalogUnavailable: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = SupportedModels.FirstOrDefault()?.Id ?? modelId;

        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
        _selectedLlmModelId = modelId;
    }

    internal void SetReasoningEffort(string effort)
    {
        _host?.SetSetting(ReasoningEffortSettingName, NormalizeReasoningEffort(effort));
        _reasoningEffort = NormalizeReasoningEffort(effort);
    }

    internal void SetTemperatureMode(string mode)
    {
        _host?.SetSetting(TemperatureModeSettingName, NormalizeTemperatureMode(mode));
        _temperatureMode = NormalizeTemperatureMode(mode);
    }

    internal void SetTemperatureValue(double value)
    {
        _host?.SetSetting(TemperatureValueSettingName, NormalizeTemperatureValue(value));
        _temperatureValue = NormalizeTemperatureValue(value);
    }

    internal async Task LoginWithChatGptInBrowserAsync(CancellationToken ct = default)
    {
        var state = OpenAiOAuthClient.RandomState();
        var pkce = OpenAiOAuthClient.GeneratePkceCodes();
        await using var server = new OpenAiLoopbackOAuthServer(state);
        server.Start();

        var authUri = OpenAiOAuthClient.BuildAuthorizeUri(state, pkce);
        Process.Start(new ProcessStartInfo
        {
            FileName = authUri.ToString(),
            UseShellExecute = true
        });

        var code = await server.WaitForCodeAsync(ct);
        var tokens = await OpenAiOAuthClient.ExchangeAuthorizationCodeAsync(_httpClient, code, pkce, ct);
        await StoreOAuthTokensAsync(tokens, preferredAccountId: null);
        SetAuthMode(OpenAiAuthMode.ChatGpt);
        await RefreshAvailableLlmModelsAsync(ct);
    }

    internal async Task ImportExistingLoginAsync(string? authFilePath = null, CancellationToken ct = default)
    {
        authFilePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "auth.json");

        if (!File.Exists(authFilePath))
            throw new FileNotFoundException("No existing login file was found.", authFilePath);

        var json = await File.ReadAllTextAsync(authFilePath, ct);
        var store = JsonSerializer.Deserialize<OpenAiExistingLoginStore>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Existing login file could not be parsed.");

        if (store.Tokens is null)
            throw new InvalidOperationException("Existing login file could not be parsed.");
        var tokens = new OpenAiOAuthTokenResponse(
            store.Tokens.IdToken,
            store.Tokens.AccessToken,
            store.Tokens.RefreshToken,
            ExpiresIn: null);
        await StoreOAuthTokensAsync(tokens, store.Tokens.AccountId);
        SetAuthMode(OpenAiAuthMode.ChatGpt);
        await RefreshAvailableLlmModelsAsync(ct);
    }

    internal async Task ClearChatGptLoginAsync()
    {
        if (_host is not null)
        {
            // A single protected record is authoritative. An empty record also prevents
            // interrupted cleanup from restoring obsolete split credentials on restart.
            await _host.StoreSecretAsync(OAuthSessionSecretName, JsonSerializer.Serialize(new SavedOAuthSession("", "", null, null, null, null)));
        }
        ApplyOAuthSession(new("", "", null, null, null, null));
        _fetchedChatGptModels = [];
        _host?.SetSetting(FetchedChatGptModelsSettingName, _fetchedChatGptModels);
        _host?.NotifyCapabilitiesChanged();
        if (_host is not null)
        {
            await _host.DeleteSecretAsync(OAuthAccessTokenSecretName);
            await _host.DeleteSecretAsync(OAuthRefreshTokenSecretName);
            await _host.DeleteSecretAsync(OAuthIdTokenSecretName);
        }
    }

    // ITtsProviderPlugin

    IReadOnlyList<PluginVoiceInfo> ITtsProviderPlugin.AvailableVoices => OpenAiTtsConfiguration.AvailableVoices;
    /// <summary>
    /// Gets the voices exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => OpenAiTtsConfiguration.AvailableVoices;
    /// <summary>
    /// Gets the currently selected provider voice identifier.
    /// </summary>
    public string? SelectedVoiceId => _selectedVoiceId ?? OpenAiTtsConfiguration.DefaultVoiceId;

    /// <summary>
    /// Gets the user-facing summary of the current settings.
    /// </summary>
    public string? SettingsSummary
    {
        get
        {
            var voice = AvailableVoices.FirstOrDefault(v => v.Id == SelectedVoiceId)?.DisplayName
                ?? OpenAiTtsConfiguration.DefaultVoiceId;
            return $"Voice: {voice}; OpenAI";
        }
    }

    /// <summary>
    /// Selects the provider voice used for subsequent speech output.
    /// </summary>
    public void SelectVoice(string? voiceId)
    {
        _host?.SetSetting(SelectedVoiceSettingName, NormalizeVoiceId(voiceId));
        _selectedVoiceId = NormalizeVoiceId(voiceId);
    }

    /// <summary>
    /// Synthesizes speech and returns a playback session.
    /// </summary>
    public bool SupportsPlaybackSelection => true;

    /// <inheritdoc />
    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return OpenAiInactiveTtsPlaybackSession.Instance;

        if (text.Length > 4000) throw new ArgumentException("Speech is limited to 4,000 characters.");
        if (request.VoiceId is { } voice && !AvailableVoices.Any(v => v.Id == voice)) throw new ArgumentException("Unknown speech voice.");
        using var httpRequest = CreateTtsRequest(text, request.VoiceId);
        using var response = await OpenAiApiTransport.SendWithErrorHandlingAsync(_httpClient, httpRequest, ct);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        if (pcm.Length == 0 || pcm.Length > OpenAiTtsConfiguration.SampleRate * 2 * 120 || pcm.Length % 2 != 0)
            throw new InvalidDataException("Speech audio is empty, malformed, or exceeds two minutes.");
        ct.ThrowIfCancellationRequested();
        return _ttsPlaybackFactory is not null ? _ttsPlaybackFactory(pcm)
            : new OpenAiPcmTtsPlaybackSession(pcm, OpenAiTtsConfiguration.SampleRate, request.OutputDeviceId);
    }

    // API key/settings management for settings view

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string TtsInstructions => _ttsInstructions;

    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var wasConfigured = IsConfigured;
        var hadFetchedModels = _fetchedLlmModels.Count > 0 || _fetchedTranscriptionModels.Count > 0;
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);

            _apiKey = normalized;

            if (changed)
            {
                _fetchedLlmModels = [];
                _fetchedTranscriptionModels = [];
                _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
                _host.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
                ApplyTranscriptionCatalog(_fetchedTranscriptionModels, persist: true);
            }

            if (changed && (wasConfigured != IsConfigured || hadFetchedModels))
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SetTtsInstructions(string instructions)
    {
        _host?.SetSetting(TtsInstructionsSettingName, instructions.Trim());
        _ttsInstructions = instructions.Trim();
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

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _oauthRefreshLock.Dispose();
        _httpClient.Dispose();
    }

    private IReadOnlyList<TranscriptionModelEntry> AvailableTranscriptionModelEntries =>
        _availableTranscriptionModelEntries.Count > 0
            ? _availableTranscriptionModelEntries
            : FallbackTranscriptionModelEntries;

    private IReadOnlyList<PluginModelInfo> AvailableChatGptModels =>
        _fetchedChatGptModels.Count > 0
            ? _fetchedChatGptModels
                .Select(model => new PluginModelInfo(
                    model.Slug,
                    string.IsNullOrWhiteSpace(model.DisplayName)
                        ? model.Slug
                        : model.DisplayName))
                .ToList()
            : FallbackChatGptModels;

    private TranscriptionModelEntry? SelectedModelEntry =>
        AvailableTranscriptionModelEntries.FirstOrDefault(model =>
            string.Equals(model.Id, _selectedModelId, StringComparison.OrdinalIgnoreCase));

    private void SelectModelCore(string modelId, bool persist)
    {
        var entry = AvailableTranscriptionModelEntries.FirstOrDefault(model =>
                string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableTranscriptionModelEntries.FirstOrDefault(model =>
                string.Equals(
                    model.Id,
                    FallbackTranscriptionModelEntries[0].Id,
                    StringComparison.OrdinalIgnoreCase))
            ?? AvailableTranscriptionModelEntries[0];
        _selectedModelId = entry.Id;
        _selectedApiModelName = entry.ApiModelName;

        if (persist)
            _host?.SetSetting(SelectedModelSettingName, entry.Id);
    }

    private void ApplyTranscriptionCatalog(
        IReadOnlyList<OpenAiFetchedModel> models,
        bool persist)
    {
        var discoveredModels = models
            .Select(model => CreateDiscoveredTranscriptionModelEntry(model.Id))
            .Where(model => model is not null)
            .Select(model => model!)
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => TranscriptionModelFamilyOrder(model.Id))
            .ThenBy(model => IsBaseTranscriptionModel(model.Id) ? 0 : 1)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToList();
        _availableTranscriptionModelEntries = discoveredModels.Count > 0
            ? discoveredModels
            : FallbackTranscriptionModelEntries;

        if (_selectedModelId is not null)
            SelectModelCore(_selectedModelId, persist);
    }

    private static TranscriptionModelEntry? CreateDiscoveredTranscriptionModelEntry(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)
            || modelId.Contains("diarize", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var template = FallbackTranscriptionModelEntries.FirstOrDefault(candidate =>
            MatchesModelFamily(modelId, candidate.Id));
        return template is null
            ? null
            : template with
            {
                Id = modelId,
                DisplayName = string.Equals(modelId, template.Id, StringComparison.OrdinalIgnoreCase)
                    ? template.DisplayName
                    : modelId,
                ApiModelName = modelId,
            };
    }

    private static bool MatchesModelFamily(string modelId, string baseModelId) =>
        string.Equals(modelId, baseModelId, StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith($"{baseModelId}-", StringComparison.OrdinalIgnoreCase);

    private static int TranscriptionModelFamilyOrder(string modelId)
    {
        for (var index = 0; index < FallbackTranscriptionModelEntries.Count; index++)
        {
            if (MatchesModelFamily(modelId, FallbackTranscriptionModelEntries[index].Id))
                return index;
        }

        return int.MaxValue;
    }

    private static bool IsBaseTranscriptionModel(string modelId) =>
        FallbackTranscriptionModelEntries.Any(model =>
            string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));

    private HttpRequestMessage CreateTtsRequest(string text, string? voiceId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = OpenAiJson.CreateJsonContent(
            OpenAiTtsConfiguration.CreateRequestBody(text, voiceId ?? SelectedVoiceId, _ttsInstructions));
        return request;
    }

    private async Task<string> ValidOAuthAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_oauthAccessToken)
            && _oauthExpiresAt is { } expiresAt
            && expiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
        {
            return _oauthAccessToken;
        }

        await _oauthRefreshLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_oauthAccessToken)
                && _oauthExpiresAt is { } refreshedExpiresAt
                && refreshedExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                return _oauthAccessToken;
            }

            if (string.IsNullOrWhiteSpace(_oauthRefreshToken))
                throw new PluginRequestException(
                    "ChatGPT login is not configured.",
                    PluginRequestFailureKind.Configuration);

            var refreshed = await OpenAiOAuthClient.RefreshTokenAsync(_httpClient, _oauthRefreshToken, ct);
            await StoreOAuthTokensAsync(refreshed, _oauthAccountId);
            return refreshed.AccessToken;
        }
        finally
        {
            _oauthRefreshLock.Release();
        }
    }

    private async Task StoreOAuthTokensAsync(OpenAiOAuthTokenResponse tokens, string? preferredAccountId)
    {
        var metadata = OpenAiOAuthClient.ExtractMetadata(tokens, preferredAccountId);
        var accountChanged = !string.Equals(
            _oauthAccountId,
            metadata.AccountId,
            StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new InvalidDataException("ChatGPT did not return a complete login.");
        var session = new SavedOAuthSession(tokens.AccessToken, tokens.RefreshToken, tokens.IdToken,
            metadata.AccountId, metadata.PlanType, metadata.ExpiresAt);
        if (_host is null) throw new InvalidOperationException("The plugin is not active.");
        await _host.StoreSecretAsync(OAuthSessionSecretName, JsonSerializer.Serialize(session));
        ApplyOAuthSession(session);
        if (accountChanged)
        {
            _fetchedChatGptModels = [];
            _host.SetSetting(FetchedChatGptModelsSettingName, _fetchedChatGptModels);
        }
        NormalizeSelectedLlmModel(persist: true);
        _host.NotifyCapabilitiesChanged();
    }

    private void ApplyOAuthSession(SavedOAuthSession session)
    {
        _oauthAccessToken = session.AccessToken;
        _oauthRefreshToken = session.RefreshToken;
        _oauthIdToken = session.IdToken;
        _oauthAccountId = session.AccountId;
        _oauthPlanType = session.PlanType;
        _oauthExpiresAt = session.ExpiresAt;
    }

    private void NormalizeSelectedLlmModel(
        bool persist,
        bool preserveUnknownWhenCatalogUnavailable = false)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        var hasFetchedCatalog = _authMode == OpenAiAuthMode.ChatGpt
            ? _fetchedChatGptModels.Count > 0
            : _fetchedLlmModels.Count > 0;
        if (_selectedLlmModelId is null
            || (!preserveUnknownWhenCatalogUnavailable || hasFetchedCatalog)
            && available.All(model =>
                !string.Equals(model.Id, _selectedLlmModelId, StringComparison.Ordinal)))
        {
            _selectedLlmModelId = available.First().Id;
        }

        if (persist)
            _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
    }

    private double? ResolvedTemperature(string modelId)
    {
        if (_temperatureMode != TemperatureModeCustom)
            return null;

        return SupportsCustomTemperature(
            modelId,
            EffectiveReasoningEffort(modelId))
            ? _temperatureValue
            : null;
    }

    private static DateTimeOffset? LoadExpiresAt(IPluginHostServices host)
    {
        try
        {
            var value = host.GetSetting<DateTimeOffset?>(OAuthExpiresAtSettingName);
            return value == default ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? NormalizeLanguage(string? language)
    {
        var normalizedLanguage = language?.Trim();
        return string.IsNullOrWhiteSpace(normalizedLanguage)
            || normalizedLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalizedLanguage;
    }

    private static IReadOnlyList<string> NormalizeLanguageHints(IReadOnlyList<string> languageHints)
    {
        var normalizedLanguageHints = new List<string>();
        foreach (var languageHint in languageHints)
        {
            if (NormalizeLanguage(languageHint) is not { } normalizedLanguage
                || normalizedLanguageHints.Contains(normalizedLanguage, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            normalizedLanguageHints.Add(normalizedLanguage);
        }

        return normalizedLanguageHints;
    }

    private static string NormalizeReasoningEffort(string? effort) =>
        effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" ? effort : "medium";

    private static string NormalizeTemperatureMode(string? mode) =>
        string.Equals(mode, TemperatureModeCustom, StringComparison.OrdinalIgnoreCase)
            ? TemperatureModeCustom
            : TemperatureModeProviderDefault;

    private static double NormalizeTemperatureValue(double? value) =>
        Math.Clamp(value ?? 0.3, 0.0, 2.0);

    private static string NormalizeVoiceId(string? voiceId) =>
        !string.IsNullOrWhiteSpace(voiceId)
        && OpenAiTtsConfiguration.AvailableVoices.Any(v => v.Id == voiceId)
            ? voiceId
            : OpenAiTtsConfiguration.DefaultVoiceId;

    private sealed record TranscriptionModelEntry(
        string Id,
        string DisplayName,
        string ApiModelName,
        string? ResponseFormat,
        bool SupportsTranslation,
        TranscriptionTransport Transport = TranscriptionTransport.Rest,
        TranscriptionLanguageFormat LanguageFormat = TranscriptionLanguageFormat.Singular,
        bool SupportsDictionaryTerms = false)
    {
        public bool SupportsStreaming => Transport == TranscriptionTransport.Realtime;
    }

    private enum TranscriptionTransport
    {
        Rest,
        Realtime,
    }

    private enum TranscriptionLanguageFormat
    {
        Singular,
        Plural,
    }

    private sealed record OpenAiModelsResponse(List<OpenAiFetchedModel?> Data);
    private sealed record OpenAiChatGptModelsResponse(List<OpenAiChatGptModel?> Models);
}
