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
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string SelectedVoiceSettingName = "selectedVoice";
    private const string TtsInstructionsSettingName = "ttsInstructions";
    private const string ReasoningEffortSettingName = "reasoningEffort";
    private const string FetchedLlmModelsSettingName = "fetchedLLMModels";
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
    private string _selectedResponseFormat = "verbose_json";
    private string? _selectedVoiceId;
    private string _ttsInstructions = "";
    private string _reasoningEffort = "medium";
    private string _temperatureMode = TemperatureModeProviderDefault;
    private double _temperatureValue = 0.3;
    private List<OpenAiFetchedModel> _fetchedLlmModels = [];
    private OpenAiAuthMode _authMode = OpenAiAuthMode.ApiKey;
    private string? _selectedLlmModelId;
    private string? _oauthAccessToken;
    private string? _oauthRefreshToken;
    private string? _oauthIdToken;
    private string? _oauthAccountId;
    private string? _oauthPlanType;
    private DateTimeOffset? _oauthExpiresAt;

    private static readonly IReadOnlyList<TranscriptionModelEntry> TranscriptionModelEntries =
    [
        new("whisper-1", "Whisper 1", "whisper-1", "verbose_json", SupportsTranslation: true),
        new("gpt-4o-transcribe", "GPT-4o Transcribe", "gpt-4o-transcribe", "json", SupportsTranslation: false),
        new("gpt-4o-mini-transcribe", "GPT-4o Mini Transcribe", "gpt-4o-mini-transcribe", "json", SupportsTranslation: false),
        new(OpenAiRealtimeStreamingSession.ModelId, "GPT Realtime Whisper", OpenAiRealtimeStreamingSession.ModelId, "json", SupportsTranslation: false, SupportsStreaming: true),
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

    private static readonly IReadOnlyList<PluginModelInfo> ChatGptModels =
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
    public string PluginVersion => "1.1.1";

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
        _oauthAccountId = host.GetSetting<string>(OAuthAccountIdSettingName);
        _oauthPlanType = host.GetSetting<string>(OAuthPlanTypeSettingName);
        _oauthExpiresAt = LoadExpiresAt(host);

        SelectModelCore(host.GetSetting<string>(SelectedModelSettingName) ?? TranscriptionModelEntries[0].Id, persist: false);
        NormalizeSelectedLlmModel(persist: false);
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
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        TranscriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToList();

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
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId) => SelectModelCore(modelId, persist: true);

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedApiModelName is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        if (_selectedModelId == OpenAiRealtimeStreamingSession.ModelId)
        {
            if (translate)
                throw new InvalidOperationException("GPT Realtime Whisper does not support translation.");

            return await OpenAiRealtimeStreamingSession.TranscribeWavAsync(
                _apiKey!,
                wavAudio,
                NormalizeLanguage(language),
                prompt,
                ct);
        }

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, BaseUrl, _apiKey!, _selectedApiModelName,
            wavAudio, NormalizeLanguage(language), translate, _selectedResponseFormat, ct, prompt);
    }

    /// <summary>
    /// Opens a streaming transcription session for live audio.
    /// </summary>
    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");
        if (_selectedModelId != OpenAiRealtimeStreamingSession.ModelId)
            throw new NotSupportedException("Select GPT Realtime Whisper to use OpenAI realtime streaming.");

        return await OpenAiRealtimeStreamingSession.ConnectAsync(_apiKey!, NormalizeLanguage(language), prompt: null, ct);
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
            ? ChatGptModels
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
            NormalizeSelectedLlmModel(persist: true);
            _host?.NotifyCapabilitiesChanged();
            return SupportedModels;
        }

        var models = await FetchLlmModelsAsync(ct);
        if (models.Count == 0)
            return [];

        _fetchedLlmModels = models.ToList();
        _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
        return SupportedModels;
    }

    internal async Task<IReadOnlyList<OpenAiFetchedModel>> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenAiModelsResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return decoded?.Data
                .Where(model => IsChatModel(model.Id))
                .OrderBy(model => model.Id, StringComparer.Ordinal)
                .ToList()
                ?? [];
        }
        catch
        {
            return [];
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

    internal void SetAuthMode(OpenAiAuthMode mode)
    {
        if (_authMode == mode)
            return;

        _authMode = mode;
        _host?.SetSetting(AuthModeSettingName, mode.ToStorageValue());
        NormalizeSelectedLlmModel(persist: true);
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
    }

    internal async Task ClearChatGptLoginAsync()
    {
        _oauthAccessToken = null;
        _oauthRefreshToken = null;
        _oauthIdToken = null;
        _oauthAccountId = null;
        _oauthPlanType = null;
        _oauthExpiresAt = null;

        if (_host is not null)
        {
            await _host.DeleteSecretAsync(OAuthAccessTokenSecretName);
            await _host.DeleteSecretAsync(OAuthRefreshTokenSecretName);
            await _host.DeleteSecretAsync(OAuthIdTokenSecretName);
            _host.SetSetting<string?>(OAuthAccountIdSettingName, null);
            _host.SetSetting<string?>(OAuthPlanTypeSettingName, null);
            _host.SetSetting<DateTimeOffset?>(OAuthExpiresAtSettingName, null);
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

    private TranscriptionModelEntry? SelectedModelEntry =>
        TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId);

    private void SelectModelCore(string modelId, bool persist)
    {
        var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? TranscriptionModelEntries[0];
        _selectedModelId = entry.Id;
        _selectedApiModelName = entry.ApiModelName;
        _selectedResponseFormat = entry.ResponseFormat;

        if (persist)
            _host?.SetSetting(SelectedModelSettingName, entry.Id);
    }

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
        _oauthAccessToken = tokens.AccessToken;
        _oauthRefreshToken = tokens.RefreshToken;
        _oauthIdToken = tokens.IdToken;
        _oauthAccountId = metadata.AccountId;
        _oauthPlanType = metadata.PlanType;
        _oauthExpiresAt = metadata.ExpiresAt;

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
        NormalizeSelectedLlmModel(persist: true);
        _host.NotifyCapabilitiesChanged();
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

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language;

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
        string ResponseFormat,
        bool SupportsTranslation,
        bool SupportsStreaming = false);

    private sealed record OpenAiModelsResponse(List<OpenAiFetchedModel> Data);
}
