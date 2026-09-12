// Protocol snapshot from the legacy Windows provider at 3282e9ca.
// Independent portable implementation; legacy packages and profile directories remain untouched.
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

/// <summary>
/// Provides OpenAI-compatible transcription and LLM capabilities.
/// </summary>
public sealed partial class OpenAiCompatiblePlugin :
    ITranscriptionEnginePlugin,
    ILlmProviderPlugin,
    ILlmRequestHedgingSupport,
    IAdditionalTranscriptionEnginesProvider,
    IAdditionalLlmProvidersProvider,
    IPluginProfileSettings, IPluginSettingsActions, IApiKeyPlugin, IPluginConnectionSettings
{
    /// <summary>Stable profile ID used for the legacy/default configuration.</summary>
    public const string DefaultProfileId = "openai-compatible";

    private const string DefaultProfileName = "OpenAI Compatible";
    private const string ProfileIdPrefix = "openai-compatible-";
    internal const int DefaultLlmRequestTimeoutSeconds = 300;
    internal const int MinLlmRequestTimeoutSeconds = 5;
    internal const int MaxLlmRequestTimeoutSeconds = 3600;
    private static readonly TimeSpan DefaultHttpRequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <inheritdoc />
    public bool SupportsRequestHedging => SupportsRequestHedgingForProfile(DefaultProfileId);

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string?> _apiKeys = new(StringComparer.OrdinalIgnoreCase);
    private List<OpenAiCompatibleProfile> _profiles = [];
    private IPluginHostServices? _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatiblePlugin"/> class.
    /// </summary>
    public OpenAiCompatiblePlugin()
        : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatiblePlugin"/> class.
    /// </summary>
    public OpenAiCompatiblePlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.openai-compatible";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "OpenAI Compatible";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.1.0";

    /// <summary>Current profiles, including the default profile.</summary>
    public IReadOnlyList<OpenAiCompatibleProfile> Profiles => _profiles;

    /// <inheritdoc />
    public IReadOnlyList<ITranscriptionEnginePlugin> AdditionalTranscriptionEngines =>
        _profiles
            .Where(profile => !IsDefaultProfile(profile.Id))
            .Select(profile => new OpenAiCompatibleProfileRole(this, profile.Id))
            .Cast<ITranscriptionEnginePlugin>()
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders =>
        _profiles
            .Where(profile => !IsDefaultProfile(profile.Id))
            .Select(profile => new OpenAiCompatibleProfileRole(this, profile.Id))
            .Cast<ILlmProviderPlugin>()
            .ToList();

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _profiles = LoadProfiles(host);
        NormalizeProfiles();

        _apiKeys.Clear();
        foreach (var profile in _profiles)
            _apiKeys[profile.Id] = await host.LoadSecretAsync(SecretKey(profile.Id));

        PersistProfiles(notifyCapabilitiesChanged: false);
        await RetrySecretCleanupAsync();
        host.Log(
            PluginLogLevel.Info,
            $"Activated profiles={_profiles.Count} configured={IsConfigured}");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        _apiKeys.Clear();
        _profiles.Clear();
        _draftCatalogs.Clear();
        _settingsProfileId = DefaultProfileId;
        return Task.CompletedTask;
    }


    /// <summary>
    /// Gets the stable provider identifier used for engine overrides.
    /// </summary>
    public string ProviderId => DefaultProfileId;
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => GetDisplayName(DefaultProfileId);

    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => IsProfileConfigured(DefaultProfileId);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => GetTranscriptionModels(DefaultProfileId);

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => DefaultProfile.SelectedModelId;

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId) => SelectModelForProfile(DefaultProfileId, modelId);

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => !SupportsStreaming;

    /// <summary>
    /// Transcribes WAV audio using the selected provider configuration.
    /// </summary>
    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeAsync(DefaultProfileId, wavAudio, language, translate, prompt, ct);

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => GetDisplayName(DefaultProfileId);

    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => IsProfileLlmAvailable(DefaultProfileId);

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels => GetLlmModels(DefaultProfileId);

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct) =>
        ProcessAsync(DefaultProfileId, systemPrompt, userText, model, ct);

    // Methods used by the settings view and tests.
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? BaseUrl => DefaultProfile.BaseUrl;
    internal string? ApiKey => GetApiKey();
    internal string? SelectedTranscriptionModelId => DefaultProfile.SelectedModelId;
    internal string? SelectedLlmModelId => DefaultProfile.SelectedLlmModelId;
    internal IReadOnlyList<FetchedModel> FetchedModels => DefaultProfile.FetchedModels;

    /// <summary>Returns the API key for the requested profile, if configured.</summary>
    public string? GetApiKey(string? profileId = null) =>
        _apiKeys.TryGetValue(ResolveProfileId(profileId), out var key) ? key : null;

    /// <summary>Adds a new OpenAI-compatible profile.</summary>
    public OpenAiCompatibleProfile AddProfile(string name)
    {
        var profile = new OpenAiCompatibleProfile
        {
            Id = CreateProfileId(),
            Name = NormalizeName(name, "Custom Server")
        };
        _profiles.Add(profile);
        PersistProfiles();
        return profile;
    }

    /// <summary>Renames an existing profile.</summary>
    public bool RenameProfile(string profileId, string name)
    {
        if (FindProfile(profileId) is not { } profile)
            return false;

        profile.Name = NormalizeName(name, profile.Name);
        PersistProfiles();
        return true;
    }

    /// <summary>Deletes an additional profile and its scoped secret.</summary>
    public async Task<bool> DeleteProfileAsync(string profileId)
    {
        if (IsDefaultProfile(profileId))
            return false;

        var profile = FindProfile(profileId);
        if (profile is null)
            return false;

        var removedSecret = SecretKey(profile.Id);
        QueueSecretCleanup(removedSecret);
        var profiles = CloneProfiles();
        profiles.RemoveAll(p => p.Id == profile.Id);
        CommitProfiles(profiles, notify: false);
        _apiKeys.Remove(profile.Id);
        _draftCatalogs.Remove(profile.Id);
        if (_settingsProfileId == profile.Id) _settingsProfileId = DefaultProfileId;
        _host?.NotifyCapabilitiesChanged();
        await RetrySecretCleanupAsync();
        return true;
    }

    /// <summary>Updates the base URL for a profile.</summary>
    public void SetBaseUrl(string url, string? profileId = null)
    {
        var profile = RequireProfile(profileId);
        profile.BaseUrl = NormalizeBaseUrl(url);
        PersistProfiles();
    }

    /// <summary>Stores or removes the API key for a profile.</summary>
    public async Task SetApiKeyAsync(string key, string? profileId = null)
    {
        var resolvedProfileId = ResolveProfileId(profileId);
        RequireProfile(resolvedProfileId);

        var normalized = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        var oldConfigured = !string.IsNullOrWhiteSpace(GetApiKey(resolvedProfileId));

        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                await _host.DeleteSecretAsync(SecretKey(resolvedProfileId));
            else
                await _host.StoreSecretAsync(SecretKey(resolvedProfileId), normalized);
        }

        _apiKeys[resolvedProfileId] = normalized;
        var newConfigured = !string.IsNullOrWhiteSpace(normalized);
        if (oldConfigured != newConfigured)
            _host?.NotifyCapabilitiesChanged();
    }

    /// <summary>Selects the transcription model for a profile.</summary>
    public void SelectModelForProfile(string profileId, string modelId)
    {
        var canonicalId = RequireProfile(profileId).Id;
        var profiles = CloneProfiles();
        profiles.Single(p => p.Id == canonicalId).SelectedModelId = NullIfWhiteSpace(modelId);
        CommitProfiles(profiles);
    }

    /// <summary>Selects the default LLM model.</summary>
    public void SelectLlmModel(string modelId) => SelectLlmModelForProfile(DefaultProfileId, modelId);

    /// <summary>Selects the LLM model for a profile.</summary>
    public void SelectLlmModelForProfile(string profileId, string modelId)
    {
        RequireProfile(profileId).SelectedLlmModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        PersistProfiles();
    }

    /// <summary>Enables or disables thinking mode for the default profile.</summary>
    public void SetThinkingEnabled(bool enabled) =>
        SetThinkingEnabledForProfile(DefaultProfileId, enabled);

    /// <summary>Enables or disables thinking mode for a profile.</summary>
    public void SetThinkingEnabledForProfile(string profileId, bool enabled)
    {
        RequireProfile(profileId).ThinkingEnabled = enabled;
        PersistProfiles();
    }

    /// <summary>Sets the LLM request timeout for a profile.</summary>
    public void SetLlmRequestTimeoutForProfile(string profileId, int seconds)
    {
        if (seconds is < MinLlmRequestTimeoutSeconds or > MaxLlmRequestTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                seconds,
                $"LLM request timeout must be between {MinLlmRequestTimeoutSeconds} and {MaxLlmRequestTimeoutSeconds} seconds.");
        }

        RequireProfile(profileId).LlmRequestTimeoutSeconds = seconds;
        PersistProfiles();
    }

    /// <summary>Stores fetched models for the default profile.</summary>
    public void SetFetchedModels(List<FetchedModel> models) =>
        SetFetchedModelsForProfile(DefaultProfileId, models);

    /// <summary>Stores fetched models for a profile.</summary>
    public void SetFetchedModelsForProfile(string profileId, List<FetchedModel> models)
    {
        RequireProfile(profileId).FetchedModels = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        PersistProfiles();
    }

    /// <summary>Fetches model IDs from the default profile server.</summary>
    public Task<List<FetchedModel>> FetchModelsAsync(CancellationToken ct = default) =>
        FetchModelsAsync(DefaultProfileId, ct);

    /// <summary>Fetches model IDs from a profile server.</summary>
    public async Task<List<FetchedModel>> FetchModelsAsync(string profileId, CancellationToken ct = default)
    {
        var profile = RequireProfile(profileId);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            return [];

        try
        {
            using var timeout = CreateRequestTimeoutSource(ct, DefaultHttpRequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(profile, "/v1/models"));
            Authenticate(request, GetApiKey(profile.Id));

            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            using var doc = JsonDocument.Parse(json);

            return ParseModelCatalog(doc.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Validates the default profile connection.</summary>
    public Task<bool> ValidateConnectionAsync(CancellationToken ct = default) =>
        ValidateConnectionAsync(DefaultProfileId, ct);

    /// <summary>Validates a profile connection.</summary>
    public async Task<bool> ValidateConnectionAsync(string profileId, CancellationToken ct = default)
    {
        var profile = RequireProfile(profileId);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            return false;

        try
        {
            using var timeout = CreateRequestTimeoutSource(ct, DefaultHttpRequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(profile, "/v1/models"));
            Authenticate(request, GetApiKey(profile.Id));

            using var response = await _httpClient.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return false;
        }
    }

    internal string GetDisplayName(string profileId) =>
        FindProfile(profileId)?.DisplayName ?? DefaultProfileName;

    internal bool IsProfileConfigured(string profileId) =>
        !string.IsNullOrWhiteSpace(FindProfile(profileId)?.BaseUrl);

    internal bool IsProfileLlmAvailable(string profileId) =>
        IsProfileConfigured(profileId);

    internal IReadOnlyList<PluginModelInfo> GetTranscriptionModels(string profileId)
    {
        var profile = RequireProfile(profileId);
        var models = profile.FetchedModels
            .Select(m => new PluginModelInfo(m.Id, m.Id))
            .ToList();

        if (!string.IsNullOrWhiteSpace(profile.SelectedModelId) && !models.Any(m => m.Id == profile.SelectedModelId))
            models.Add(new PluginModelInfo(profile.SelectedModelId, profile.SelectedModelId));

        return models;
    }

    internal IReadOnlyList<PluginModelInfo> GetLlmModels(string profileId)
    {
        var profile = RequireProfile(profileId);
        var models = profile.FetchedModels
            .Select(m => new PluginModelInfo(m.Id, m.Id))
            .ToList();

        if (!string.IsNullOrWhiteSpace(profile.SelectedLlmModelId) && !models.Any(m => m.Id == profile.SelectedLlmModelId))
            models.Add(new PluginModelInfo(profile.SelectedLlmModelId, profile.SelectedLlmModelId));

        return models;
    }

    internal async Task<PluginTranscriptionResult> TranscribeAsync(
        string profileId,
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        var profile = RequireProfile(profileId);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            throw new PluginRequestException(
                "Server URL not configured",
                PluginRequestFailureKind.Configuration);
        if (string.IsNullOrEmpty(profile.SelectedModelId))
            throw new PluginRequestException("Select a transcription model.", PluginRequestFailureKind.Configuration);

        using var timeout = CreateRequestTimeoutSource(ct, DefaultHttpRequestTimeout);
        try
        {
            if (UsesRealtime(profile))
            {
                if (translate) throw new PluginRequestException("Realtime transcription does not support translation. Use batch mode.", PluginRequestFailureKind.Configuration);
                return await CompatibleRealtimeStreamingSession.TranscribeWavAsync(RealtimeUri(profile), GetApiKey(profile.Id) ?? "",
                    profile.SelectedModelId, wavAudio, LanguageHints(language), prompt, timeout.Token, protocol: profile.RealtimeProtocol);
            }
            var endpoint = BatchUri(profile);
            if (translate) endpoint = new Uri(endpoint.AbsoluteUri.Replace("/audio/transcriptions", "/audio/translations", StringComparison.Ordinal));
            return await CompatibleTranscriptionHelper.TranscribeAsync(
                _httpClient,
                profile.BaseUrl,
                GetApiKey(profile.Id) ?? "",
                profile.SelectedModelId,
                new OpenAiTranscriptionUpload(wavAudio, "audio.wav", "audio/wav"),
                language,
                translate,
                profile.ApiVersion.Length > 0 || profile.BatchEndpoint == "deployment-scoped" ? "json" : "verbose_json",
                timeout.Token,
                prompt, endpoint);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new PluginRequestException(
                $"OpenAI-compatible transcription request timed out after {DefaultHttpRequestTimeout.TotalSeconds:0} seconds.",
                PluginRequestFailureKind.Timeout,
                innerException: ex);
        }
    }

    internal async Task<string> ProcessAsync(
        string profileId,
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct)
    {
        var profile = RequireProfile(profileId);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            throw new PluginRequestException(
                "Server URL is not configured.",
                PluginRequestFailureKind.Configuration);

        var modelId = !string.IsNullOrEmpty(model) ? model : profile.SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new PluginRequestException(
                "No LLM model selected",
                PluginRequestFailureKind.Configuration);

        return await SendChatCompletionAsync(
            profile,
            modelId,
            systemPrompt,
            userText,
            ct);
    }

    private async Task<string> SendChatCompletionAsync(
        OpenAiCompatibleProfile profile,
        string modelId,
        string systemPrompt,
        string userText,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText }
            },
            ["max_tokens"] = LlmOutputTokenBudget.Calculate(systemPrompt, userText)
        };
        var responses = profile.LlmApi == "responses";
        if (responses)
        {
            body.Remove("messages"); body.Remove("max_tokens");
            body["max_output_tokens"] = profile.ReasoningEffort.Length > 0
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText);
            body["instructions"] = string.IsNullOrWhiteSpace(systemPrompt) ? "You are a helpful assistant." : systemPrompt;
            body["input"] = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = userText } } } };
            body["store"] = false;
            if (profile.ReasoningEffort.Length > 0) body["reasoning"] = new { effort = profile.ReasoningEffort };
        }
        else AddThinkingConfiguration(body, profile.BaseUrl, profile.ThinkingEnabled);
        if (profile.TemperatureMode == "custom" && !(responses && profile.ReasoningEffort.Length > 0))
            body["temperature"] = profile.Temperature;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            RequestUri(profile, responses ? "/v1/responses" : "/v1/chat/completions"));
        Authenticate(request, GetApiKey(profile.Id));
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, RequestJsonOptions),
            Encoding.UTF8,
            "application/json");

        var timeoutSeconds = NormalizeLlmRequestTimeoutSeconds(profile.LlmRequestTimeoutSeconds);
        using var timeout = CreateRequestTimeoutSource(ct, TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            HttpResponseMessage response;
            try { response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, timeout.Token); }
            catch (PluginRequestException ex) when (!responses && ex.HttpStatusCode == 400 &&
                ex.Message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
            {
                body["max_completion_tokens"] = body["max_tokens"]; body.Remove("max_tokens");
                using var retry = new HttpRequestMessage(HttpMethod.Post, request.RequestUri);
                Authenticate(retry, GetApiKey(profile.Id));
                retry.Content = new StringContent(JsonSerializer.Serialize(body, RequestJsonOptions), Encoding.UTF8, "application/json");
                response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, retry, timeout.Token);
            }
            using var ownedResponse = response;
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            return responses ? ParseResponsesText(json) : ParseChatCompletionResponse(json);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new PluginRequestException(
                $"OpenAI-compatible LLM request timed out after {timeoutSeconds} seconds.",
                PluginRequestFailureKind.Timeout,
                innerException: ex);
        }
    }

    private static string ParseChatCompletionResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PluginRequestException(
                "OpenAI-compatible chat completion returned an empty response.",
                PluginRequestFailureKind.EmptyResponse);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(
            root,
            "The OpenAI-compatible provider");

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()?.Trim()
                : content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray()
                    .Where(p => p.TryGetProperty("type", out var type) && type.GetString() == "text" && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetProperty("text").GetString())).Trim() : null;
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        throw new PluginRequestException(
            "OpenAI-compatible chat completion response did not contain a string "
            + "choices[0].message.content value.",
            PluginRequestFailureKind.EmptyResponse);
    }

    private static void AddThinkingConfiguration(
        Dictionary<string, object?> body,
        string baseUrl,
        bool thinkingEnabled)
    {
        if (IsDeepInfraEndpoint(baseUrl))
        {
            body["reasoning_effort"] = thinkingEnabled ? "high" : "none";
            return;
        }

        body["thinking"] = new
        {
            type = thinkingEnabled ? "enabled" : "disabled"
        };
    }

    private static bool IsDeepInfraEndpoint(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "api.deepinfra.com", StringComparison.OrdinalIgnoreCase);

    private string SecretKey(string profileId) => SecretKey(profileId, FindProfile(profileId)?.ApiKeyRevision);

    private static string SecretKey(string profileId, string? revision)
    {
        var prefix = IsDefaultProfile(profileId) ? "api-key" : $"api-key.{profileId}";
        if (revision is null) return prefix;
        if (!Guid.TryParseExact(revision, "N", out _)) throw new InvalidDataException("Invalid saved key revision.");
        return prefix + "." + revision;
    }

    private static bool IsDefaultProfile(string profileId) =>
        string.Equals(profileId, DefaultProfileId, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value, string fallback)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private static string NormalizeBaseUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];
        return normalized;
    }

    private string CreateProfileId()
    {
        string id;
        do
        {
            id = $"{ProfileIdPrefix}{Guid.NewGuid():N}";
        }
        while (_profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)));

        return id;
    }

    private string ResolveProfileId(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? DefaultProfileId : profileId.Trim();

    private OpenAiCompatibleProfile DefaultProfile => RequireProfile(DefaultProfileId);

    private OpenAiCompatibleProfile? FindProfile(string? profileId)
    {
        var resolved = ResolveProfileId(profileId);
        return _profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, resolved, StringComparison.OrdinalIgnoreCase));
    }

    private bool SupportsRequestHedgingForProfile(string? profileId)
    {
        var baseUrl = FindProfile(profileId)?.BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var address))
            return true;
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }

        return !address.IsIPv6LinkLocal
            && !address.IsIPv6SiteLocal
            && (bytes[0] & 0xfe) != 0xfc;
    }

    private OpenAiCompatibleProfile RequireProfile(string? profileId)
    {
        var resolved = ResolveProfileId(profileId);
        return FindProfile(resolved)
            ?? throw new ArgumentException($"Unknown OpenAI-compatible profile: {resolved}", nameof(profileId));
    }

    private List<OpenAiCompatibleProfile> LoadProfiles(IPluginHostServices host)
    {
        var profiles = host.GetSetting<List<OpenAiCompatibleProfile>>("profiles");
        if (profiles is { Count: > 0 })
            return profiles;

        return [new OpenAiCompatibleProfile()];
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int NormalizeLlmRequestTimeoutSeconds(int seconds) =>
        Math.Clamp(seconds, MinLlmRequestTimeoutSeconds, MaxLlmRequestTimeoutSeconds);

    private static CancellationTokenSource CreateRequestTimeoutSource(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private void NormalizeProfiles()
    {
        foreach (var profile in _profiles)
        {
            profile.Id = string.IsNullOrWhiteSpace(profile.Id)
                ? CreateProfileId()
                : profile.Id.Trim();
            profile.Name = NormalizeName(profile.Name, IsDefaultProfile(profile.Id) ? DefaultProfileName : "Custom Server");
            profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl ?? "");
            profile.ApiVersion = (profile.ApiVersion ?? "").Trim();
            profile.TranscriptionTransport = profile.TranscriptionTransport is "batch" or "realtime" ? profile.TranscriptionTransport : "auto";
            profile.RealtimeProtocol = profile.RealtimeProtocol is "live" or "whisper" ? profile.RealtimeProtocol : "auto";
            profile.BatchEndpoint = profile.BatchEndpoint == "deployment-scoped" ? "deployment-scoped" : "standard";
            profile.LlmApi = profile.LlmApi == "responses" ? "responses" : "chat-completions";
            profile.ReasoningEffort = profile.ReasoningEffort is "low" or "medium" or "high" or "xhigh" or "max" ? profile.ReasoningEffort : "";
            profile.TemperatureMode = profile.TemperatureMode == "custom" ? "custom" : "provider-default";
            profile.Temperature = double.IsFinite(profile.Temperature) ? Math.Clamp(profile.Temperature, 0, 2) : 0.3;
            profile.SelectedModelId = NullIfWhiteSpace(profile.SelectedModelId);
            profile.SelectedLlmModelId = NullIfWhiteSpace(profile.SelectedLlmModelId);
            profile.LlmRequestTimeoutSeconds = NormalizeLlmRequestTimeoutSeconds(profile.LlmRequestTimeoutSeconds);
            profile.FetchedModels = profile.FetchedModels
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (_profiles.All(profile => !IsDefaultProfile(profile.Id)))
        {
            _profiles.Insert(0, new OpenAiCompatibleProfile
            {
                Id = DefaultProfileId,
                Name = DefaultProfileName
            });
        }

        _profiles = _profiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => IsDefaultProfile(profile.Id) ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void PersistProfiles(bool notifyCapabilitiesChanged = true)
    {
        if (_host is null)
            return;

        _host.SetSetting("profiles", _profiles);

        if (notifyCapabilitiesChanged)
            _host.NotifyCapabilitiesChanged();
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    private sealed class OpenAiCompatibleProfileRole(
        OpenAiCompatiblePlugin owner,
        string profileId) :
        ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        ILlmRequestHedgingSupport,
        ITranscriptionEngineSelectionIdentity,
        ILlmProviderSelectionIdentity
    {
        public string PluginId => owner.PluginId;
        public string PluginName => owner.PluginName;
        public string PluginVersion => owner.PluginVersion;
        public string TranscriptionSelectionId => profileId;
        public string LlmSelectionId => profileId;
        public string ProviderId => profileId;
        public string ProviderDisplayName => owner.GetDisplayName(profileId);
        public bool IsConfigured => owner.IsProfileConfigured(profileId);
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => owner.GetTranscriptionModels(profileId);
        public string? SelectedModelId => owner.FindProfile(profileId)?.SelectedModelId;
        public bool SupportsTranslation => !SupportsStreaming;
        public bool SupportsStreaming => UsesRealtime(owner.RequireProfile(profileId));
        public bool SupportsStreamingCompletion => true;
        public bool SupportsLanguageHints => SupportsStreaming && CompatibleRealtimeStreamingSession.IsLiveModel(SelectedModelId ?? "", owner.RequireProfile(profileId).RealtimeProtocol);
        public bool SupportsDictionaryTerms => !SupportsStreaming || CompatibleRealtimeStreamingSession.IsLiveModel(SelectedModelId ?? "", owner.RequireProfile(profileId).RealtimeProtocol);
        public bool SupportsStreamingForPrompt(string? prompt) => SupportsStreaming &&
            (string.IsNullOrWhiteSpace(prompt) || CompatibleRealtimeStreamingSession.IsLiveModel(SelectedModelId ?? "", owner.RequireProfile(profileId).RealtimeProtocol));
        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) => owner.StartProfileStreamingAsync(profileId, LanguageHints(language), null, ct);
        public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> hints, CancellationToken ct) => owner.StartProfileStreamingAsync(profileId, hints, null, ct);
        public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> hints, string? prompt, CancellationToken ct) => owner.StartProfileStreamingAsync(profileId, hints, prompt, ct);
        public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(byte[] audio, IReadOnlyList<string> hints, bool translate, string? prompt, CancellationToken ct) =>
            owner.TranscribeProfileWithHintsAsync(profileId, audio, hints, translate, prompt, ct);
        public string ProviderName => owner.GetDisplayName(profileId);
        public bool IsAvailable => owner.IsProfileLlmAvailable(profileId);
        public IReadOnlyList<PluginModelInfo> SupportedModels => owner.GetLlmModels(profileId);
        public bool SupportsRequestHedging => owner.SupportsRequestHedgingForProfile(profileId);
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectModel(string modelId) => owner.SelectModelForProfile(profileId, modelId);
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            owner.TranscribeAsync(profileId, wavAudio, language, translate, prompt, ct);
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            owner.ProcessAsync(profileId, systemPrompt, userText, model, ct);
        public void Dispose() { }
    }
}

/// <summary>
/// Persisted OpenAI-compatible provider profile.
/// </summary>
public sealed class OpenAiCompatibleProfile
{
    /// <summary>Opaque encrypted-key revision used to commit profile fields and a replacement key together.</summary>
    public string? ApiKeyRevision { get; set; }
    /// <summary>Stable profile identifier. Additional profiles use IDs without colons.</summary>
    public string Id { get; set; } = OpenAiCompatiblePlugin.DefaultProfileId;
    /// <summary>Human-readable profile name.</summary>
    public string Name { get; set; } = "OpenAI Compatible";
    /// <summary>Base server URL without a trailing /v1 suffix.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Optional API version appended to provider requests.</summary>
    public string ApiVersion { get; set; } = "";
    /// <summary>auto, batch, or realtime transcription transport.</summary>
    public string TranscriptionTransport { get; set; } = "auto";
    /// <summary>Realtime payload family: auto recognizes canonical IDs, live and whisper support arbitrary deployment aliases.</summary>
    public string RealtimeProtocol { get; set; } = "auto";
    /// <summary>standard or deployment-scoped batch route.</summary>
    public string BatchEndpoint { get; set; } = "standard";
    /// <summary>chat-completions or responses text API.</summary>
    public string LlmApi { get; set; } = "chat-completions";
    /// <summary>Optional Responses reasoning effort; empty uses the provider default.</summary>
    public string ReasoningEffort { get; set; } = "";
    /// <summary>provider-default omits temperature; custom uses Temperature.</summary>
    public string TemperatureMode { get; set; } = "provider-default";
    /// <summary>Custom text generation temperature, between zero and two.</summary>
    public double Temperature { get; set; } = 0.3;
    /// <summary>Selected transcription model ID.</summary>
    public string? SelectedModelId { get; set; }
    /// <summary>Selected LLM model ID.</summary>
    public string? SelectedLlmModelId { get; set; }
    /// <summary>Whether compatible chat requests should enable model thinking mode.</summary>
    public bool ThinkingEnabled { get; set; }
    /// <summary>Maximum duration of one LLM request, in seconds.</summary>
    public int LlmRequestTimeoutSeconds { get; set; } = OpenAiCompatiblePlugin.DefaultLlmRequestTimeoutSeconds;
    /// <summary>Models fetched from the provider. API keys are never stored here.</summary>
    public List<FetchedModel> FetchedModels { get; set; } = [];
    /// <summary>Display name with a fallback for unnamed profiles.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Custom Server" : Name.Trim();
}

/// <summary>
/// Model metadata fetched from an OpenAI-compatible provider.
/// </summary>
public sealed record FetchedModel(string Id, string? OwnedBy);
