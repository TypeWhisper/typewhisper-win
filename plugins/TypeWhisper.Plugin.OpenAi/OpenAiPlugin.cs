using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

/// <summary>
/// Provides open ai plugin behavior.
/// </summary>
public sealed class OpenAiPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ITtsProviderPlugin
{
    private const string BaseUrl = "https://api.openai.com";
    private const string ChatGptModelsEndpoint = "https://chatgpt.com/backend-api/codex/models";
    private const string PluginVersionValue = "1.1.1";
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
    private const string OAuthAccessTokenSecretName = "oauth-access-token";
    private const string OAuthRefreshTokenSecretName = "oauth-refresh-token";
    private const string OAuthIdTokenSecretName = "oauth-id-token";
    private const string OAuthAccountIdSettingName = "oauthAccountID";
    private const string OAuthPlanTypeSettingName = "oauthPlanType";
    private const string OAuthExpiresAtSettingName = "oauthExpiresAt";

    private readonly HttpClient _httpClient;
    private readonly Func<byte[], ITtsPlaybackSession> _ttsPlaybackFactory;
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
            LanguageFormat: TranscriptionLanguageFormat.Plural),
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

    internal OpenAiPlugin(HttpClient httpClient, Func<byte[], ITtsPlaybackSession>? ttsPlaybackFactory = null)
    {
        _httpClient = httpClient;
        _ttsPlaybackFactory = ttsPlaybackFactory ?? (pcm => new OpenAiPcmTtsPlaybackSession(pcm, OpenAiTtsConfiguration.SampleRate));
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
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new OpenAiSettingsView(this);

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
                prompt,
                ct);
        }

        if (entry.LanguageFormat == TranscriptionLanguageFormat.Plural)
        {
            return await OpenAiTranscriptionClient.TranscribeAsync(
                _httpClient,
                BaseUrl,
                _apiKey!,
                entry.ApiModelName,
                wavAudio,
                normalizedLanguageHints,
                entry.ResponseFormat,
                prompt,
                ct);
        }

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            entry.ApiModelName,
            wavAudio,
            normalizedLanguageHints.FirstOrDefault(),
            translate,
            entry.ResponseFormat ?? "json",
            ct,
            prompt);
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
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");
        if (SelectedModelEntry is not { Transport: TranscriptionTransport.Realtime } entry)
            throw new NotSupportedException("Select an OpenAI realtime transcription model to use streaming.");

        return await OpenAiRealtimeStreamingSession.ConnectAsync(
            _apiKey!,
            entry.ApiModelName,
            NormalizeLanguageHints(languageHints),
            prompt: null,
            ct);
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
                SupportsReasoningEffort(modelId) ? _reasoningEffort : null,
                ct);
        }

        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        if (UsesResponsesApi(modelId))
        {
            var client = new OpenAiResponsesClient(_httpClient, BaseUrl, _apiKey!);
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                SupportsReasoningEffort(modelId) ? _reasoningEffort : null,
                ct);
        }

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: OutputTokenParameter(modelId),
            reasoningEffort: SupportsReasoningEffort(modelId) ? _reasoningEffort : null,
            temperature: ResolvedTemperature(modelId));
    }

    internal static bool UsesResponsesApi(string modelId) =>
        modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsReasoningEffort(string modelId)
    {
        var lowered = modelId.ToLowerInvariant();
        return lowered.StartsWith("gpt-5", StringComparison.Ordinal)
            || lowered.StartsWith("o1", StringComparison.Ordinal)
            || lowered.StartsWith("o3", StringComparison.Ordinal)
            || lowered.StartsWith("o4", StringComparison.Ordinal)
            || lowered.Contains("codex", StringComparison.Ordinal);
    }

    internal static string OutputTokenParameter(string modelId)
    {
        var lowered = modelId.ToLowerInvariant();
        return lowered.StartsWith("gpt-5", StringComparison.Ordinal)
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
        if (lowered.StartsWith("gpt-5", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

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

            return decoded?.Data
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList()
                ?? null;
        }
        catch
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

            var visibleModels = decoded?.Models
                .Where(IsVisibleChatGptModel)
                .DistinctBy(model => model.Slug, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Priority ?? int.MaxValue)
                .ThenBy(model => model.Slug, StringComparer.Ordinal)
                .ToList()
                ?? [];
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
        catch
        {
            return null;
        }
    }

    internal static bool IsChatModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        var hasChatPrefix = lowered.StartsWith("gpt-", StringComparison.Ordinal)
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

        _authMode = mode;
        _host?.SetSetting(AuthModeSettingName, mode.ToStorageValue());
        NormalizeSelectedLlmModel(
            persist: true,
            preserveUnknownWhenCatalogUnavailable: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = SupportedModels.FirstOrDefault()?.Id ?? modelId;

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
    }

    internal void SetReasoningEffort(string effort)
    {
        _reasoningEffort = NormalizeReasoningEffort(effort);
        _host?.SetSetting(ReasoningEffortSettingName, _reasoningEffort);
    }

    internal void SetTemperatureMode(string mode)
    {
        _temperatureMode = NormalizeTemperatureMode(mode);
        _host?.SetSetting(TemperatureModeSettingName, _temperatureMode);
    }

    internal void SetTemperatureValue(double value)
    {
        _temperatureValue = NormalizeTemperatureValue(value);
        _host?.SetSetting(TemperatureValueSettingName, _temperatureValue);
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

    internal async Task ImportExistingLoginAsync(string? authFilePath = null)
    {
        authFilePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "auth.json");

        if (!File.Exists(authFilePath))
            throw new FileNotFoundException("No existing login file was found.", authFilePath);

        var json = await File.ReadAllTextAsync(authFilePath);
        var store = JsonSerializer.Deserialize<OpenAiExistingLoginStore>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Existing login file could not be parsed.");

        var tokens = new OpenAiOAuthTokenResponse(
            store.Tokens.IdToken,
            store.Tokens.AccessToken,
            store.Tokens.RefreshToken,
            ExpiresIn: null);
        await StoreOAuthTokensAsync(tokens, store.Tokens.AccountId);
        SetAuthMode(OpenAiAuthMode.ChatGpt);
        await RefreshAvailableLlmModelsAsync();
    }

    internal async Task ClearChatGptLoginAsync()
    {
        _oauthAccessToken = null;
        _oauthRefreshToken = null;
        _oauthIdToken = null;
        _oauthAccountId = null;
        _oauthPlanType = null;
        _oauthExpiresAt = null;
        _fetchedChatGptModels = [];

        if (_host is not null)
        {
            await _host.DeleteSecretAsync(OAuthAccessTokenSecretName);
            await _host.DeleteSecretAsync(OAuthRefreshTokenSecretName);
            await _host.DeleteSecretAsync(OAuthIdTokenSecretName);
            _host.SetSetting<string?>(OAuthAccountIdSettingName, null);
            _host.SetSetting<string?>(OAuthPlanTypeSettingName, null);
            _host.SetSetting<DateTimeOffset?>(OAuthExpiresAtSettingName, null);
            _host.SetSetting(FetchedChatGptModelsSettingName, _fetchedChatGptModels);
            NormalizeSelectedLlmModel(persist: true);
            _host.NotifyCapabilitiesChanged();
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
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
    }

    /// <summary>
    /// Synthesizes speech and returns a playback session.
    /// </summary>
    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return OpenAiInactiveTtsPlaybackSession.Instance;

        using var httpRequest = CreateTtsRequest(text);
        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, httpRequest, ct);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        return _ttsPlaybackFactory(pcm);
    }

    // API key/settings management for settings view

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string TtsInstructions => _ttsInstructions;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var wasConfigured = IsConfigured;
        var hadFetchedModels = _fetchedLlmModels.Count > 0 || _fetchedTranscriptionModels.Count > 0;
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

        _apiKey = normalized;
        if (_host is not null)
        {
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
                ApplyTranscriptionCatalog(_fetchedTranscriptionModels, persist: true);
            }

            if (changed && (wasConfigured != IsConfigured || hadFetchedModels))
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SetTtsInstructions(string instructions)
    {
        _ttsInstructions = instructions.Trim();
        _host?.SetSetting(TtsInstructionsSettingName, _ttsInstructions);
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
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

        foreach (var template in FallbackTranscriptionModelEntries)
        {
            if (!MatchesModelFamily(modelId, template.Id))
                continue;

            return template with
            {
                Id = modelId,
                DisplayName = string.Equals(modelId, template.Id, StringComparison.OrdinalIgnoreCase)
                    ? template.DisplayName
                    : modelId,
                ApiModelName = modelId,
            };
        }

        return null;
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

    private HttpRequestMessage CreateTtsRequest(string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = OpenAiJson.CreateJsonContent(
            OpenAiTtsConfiguration.CreateRequestBody(text, SelectedVoiceId, _ttsInstructions));
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

        if (string.IsNullOrWhiteSpace(_oauthRefreshToken))
            throw new InvalidOperationException("ChatGPT login is not configured.");

        var refreshed = await OpenAiOAuthClient.RefreshTokenAsync(_httpClient, _oauthRefreshToken, ct);
        await StoreOAuthTokensAsync(refreshed, _oauthAccountId);
        return refreshed.AccessToken;
    }

    private async Task StoreOAuthTokensAsync(OpenAiOAuthTokenResponse tokens, string? preferredAccountId)
    {
        var metadata = OpenAiOAuthClient.ExtractMetadata(tokens, preferredAccountId);
        var accountChanged = !string.Equals(
            _oauthAccountId,
            metadata.AccountId,
            StringComparison.Ordinal);
        _oauthAccessToken = tokens.AccessToken;
        _oauthRefreshToken = tokens.RefreshToken;
        _oauthIdToken = tokens.IdToken;
        _oauthAccountId = metadata.AccountId;
        _oauthPlanType = metadata.PlanType;
        _oauthExpiresAt = metadata.ExpiresAt;
        if (accountChanged)
            _fetchedChatGptModels = [];

        if (_host is null)
            return;

        await _host.StoreSecretAsync(OAuthAccessTokenSecretName, tokens.AccessToken);
        await _host.StoreSecretAsync(OAuthRefreshTokenSecretName, tokens.RefreshToken);
        if (string.IsNullOrWhiteSpace(tokens.IdToken))
            await _host.DeleteSecretAsync(OAuthIdTokenSecretName);
        else
            await _host.StoreSecretAsync(OAuthIdTokenSecretName, tokens.IdToken);
        _host.SetSetting(OAuthAccountIdSettingName, _oauthAccountId);
        _host.SetSetting(OAuthPlanTypeSettingName, _oauthPlanType);
        _host.SetSetting(OAuthExpiresAtSettingName, _oauthExpiresAt);
        if (accountChanged)
            _host.SetSetting(FetchedChatGptModelsSettingName, _fetchedChatGptModels);
        NormalizeSelectedLlmModel(persist: true);
        _host.NotifyCapabilitiesChanged();
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
            SupportsReasoningEffort(modelId) ? _reasoningEffort : null)
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
        effort is "low" or "medium" or "high" or "xhigh" ? effort : "medium";

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

    private sealed record OpenAiModelsResponse(List<OpenAiFetchedModel> Data);
    private sealed record OpenAiChatGptModelsResponse(List<OpenAiChatGptModel> Models);
}
