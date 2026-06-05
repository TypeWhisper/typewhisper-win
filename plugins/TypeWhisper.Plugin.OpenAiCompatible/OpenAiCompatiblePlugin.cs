using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

/// <summary>
/// Provides OpenAI-compatible transcription and LLM capabilities.
/// </summary>
public sealed class OpenAiCompatiblePlugin :
    ITranscriptionEnginePlugin,
    ILlmProviderPlugin,
    IAdditionalTranscriptionEnginesProvider,
    IAdditionalLlmProvidersProvider
{
    /// <summary>Stable profile ID used for the legacy/default configuration.</summary>
    public const string DefaultProfileId = "openai-compatible";

    private const string DefaultProfileName = "OpenAI Compatible";
    private const string ProfileIdPrefix = "openai-compatible-";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string?> _apiKeys = new(StringComparer.OrdinalIgnoreCase);
    private List<OpenAiCompatibleProfile> _profiles = [];
    private IPluginHostServices? _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatiblePlugin"/> class.
    /// </summary>
    public OpenAiCompatiblePlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
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
    public string PluginVersion => "1.0.1";

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
        host.Log(
            PluginLogLevel.Info,
            $"Activated profiles={_profiles.Count} defaultUrl={DefaultProfile.BaseUrl} configured={IsConfigured}");
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
    public UserControl? CreateSettingsView() => new OpenAiCompatibleSettingsView(this);

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
    public bool SupportsTranslation => true;

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

        _profiles.Remove(profile);
        _apiKeys.Remove(profile.Id);
        if (_host is not null)
            await _host.DeleteSecretAsync(SecretKey(profile.Id));

        PersistProfiles();
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
        _apiKeys[resolvedProfileId] = normalized;

        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                await _host.DeleteSecretAsync(SecretKey(resolvedProfileId));
            else
                await _host.StoreSecretAsync(SecretKey(resolvedProfileId), normalized);
        }

        var newConfigured = !string.IsNullOrWhiteSpace(normalized);
        if (oldConfigured != newConfigured)
            _host?.NotifyCapabilitiesChanged();
    }

    /// <summary>Selects the transcription model for a profile.</summary>
    public void SelectModelForProfile(string profileId, string modelId)
    {
        RequireProfile(profileId).SelectedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        PersistProfiles();
    }

    /// <summary>Selects the default LLM model.</summary>
    public void SelectLlmModel(string modelId) => SelectLlmModelForProfile(DefaultProfileId, modelId);

    /// <summary>Selects the LLM model for a profile.</summary>
    public void SelectLlmModelForProfile(string profileId, string modelId)
    {
        RequireProfile(profileId).SelectedLlmModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{profile.BaseUrl}/v1/models");
            var apiKey = GetApiKey(profile.Id);
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => new FetchedModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null))
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{profile.BaseUrl}/v1/models");
            var apiKey = GetApiKey(profile.Id);
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal string GetDisplayName(string profileId) =>
        FindProfile(profileId)?.DisplayName ?? DefaultProfileName;

    internal bool IsProfileConfigured(string profileId) =>
        !string.IsNullOrWhiteSpace(FindProfile(profileId)?.BaseUrl);

    internal bool IsProfileLlmAvailable(string profileId) =>
        IsProfileConfigured(profileId)
        && !string.IsNullOrWhiteSpace(FindProfile(profileId)?.SelectedLlmModelId);

    internal IReadOnlyList<PluginModelInfo> GetTranscriptionModels(string profileId)
    {
        var profile = RequireProfile(profileId);
        var models = profile.FetchedModels
            .Select(m => new PluginModelInfo(m.Id, m.Id))
            .ToList();

        if (models.Count == 0 && !string.IsNullOrWhiteSpace(profile.SelectedModelId))
            return [new PluginModelInfo(profile.SelectedModelId, profile.SelectedModelId)];

        return models;
    }

    internal IReadOnlyList<PluginModelInfo> GetLlmModels(string profileId)
    {
        var profile = RequireProfile(profileId);
        var models = profile.FetchedModels
            .Select(m => new PluginModelInfo(m.Id, m.Id))
            .ToList();

        if (models.Count == 0 && !string.IsNullOrWhiteSpace(profile.SelectedLlmModelId))
            return [new PluginModelInfo(profile.SelectedLlmModelId, profile.SelectedLlmModelId)];

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
            throw new InvalidOperationException("Server-URL nicht konfiguriert");
        if (string.IsNullOrEmpty(profile.SelectedModelId))
            throw new InvalidOperationException("Kein Transkriptions-Modell ausgewählt");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            profile.BaseUrl,
            GetApiKey(profile.Id) ?? "",
            profile.SelectedModelId,
            wavAudio,
            language,
            translate,
            "verbose_json",
            ct,
            prompt);
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
            throw new InvalidOperationException("Server-URL nicht konfiguriert");

        var modelId = !string.IsNullOrEmpty(model) ? model : profile.SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("Kein LLM-Modell ausgewählt");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            profile.BaseUrl,
            GetApiKey(profile.Id) ?? "",
            modelId,
            systemPrompt,
            userText,
            ct);
    }

    private static string SecretKey(string profileId) =>
        IsDefaultProfile(profileId) ? "api-key" : $"api-key.{profileId}";

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

        return [CreateLegacyProfile(host)];
    }

    private static OpenAiCompatibleProfile CreateLegacyProfile(IPluginHostServices host)
    {
        var fetchedModels = new List<FetchedModel>();
        var modelsJson = host.GetSetting<string>("fetchedModels");
        if (!string.IsNullOrWhiteSpace(modelsJson))
        {
            try
            {
                fetchedModels = JsonSerializer.Deserialize<List<FetchedModel>>(modelsJson, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                fetchedModels = [];
            }
        }

        return new OpenAiCompatibleProfile
        {
            Id = DefaultProfileId,
            Name = DefaultProfileName,
            BaseUrl = NormalizeBaseUrl(host.GetSetting<string>("baseUrl") ?? ""),
            SelectedModelId = NullIfWhiteSpace(host.GetSetting<string>("selectedModel")),
            SelectedLlmModelId = NullIfWhiteSpace(host.GetSetting<string>("selectedLlmModel")),
            FetchedModels = fetchedModels
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void NormalizeProfiles()
    {
        foreach (var profile in _profiles)
        {
            profile.Id = string.IsNullOrWhiteSpace(profile.Id)
                ? CreateProfileId()
                : profile.Id.Trim();
            profile.Name = NormalizeName(profile.Name, IsDefaultProfile(profile.Id) ? DefaultProfileName : "Custom Server");
            profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl ?? "");
            profile.SelectedModelId = NullIfWhiteSpace(profile.SelectedModelId);
            profile.SelectedLlmModelId = NullIfWhiteSpace(profile.SelectedLlmModelId);
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
        SyncDefaultProfileToLegacySettings();

        if (notifyCapabilitiesChanged)
            _host.NotifyCapabilitiesChanged();
    }

    private void SyncDefaultProfileToLegacySettings()
    {
        if (_host is null)
            return;

        var profile = DefaultProfile;
        _host.SetSetting("baseUrl", profile.BaseUrl);
        _host.SetSetting("selectedModel", profile.SelectedModelId ?? "");
        _host.SetSetting("selectedLlmModel", profile.SelectedLlmModelId ?? "");
        _host.SetSetting("fetchedModels", JsonSerializer.Serialize(profile.FetchedModels, JsonOptions));
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
        public bool SupportsTranslation => true;
        public string ProviderName => owner.GetDisplayName(profileId);
        public bool IsAvailable => owner.IsProfileLlmAvailable(profileId);
        public IReadOnlyList<PluginModelInfo> SupportedModels => owner.GetLlmModels(profileId);
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => owner.CreateSettingsView();
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
    /// <summary>Stable profile identifier. Additional profiles use IDs without colons.</summary>
    public string Id { get; set; } = OpenAiCompatiblePlugin.DefaultProfileId;
    /// <summary>Human-readable profile name.</summary>
    public string Name { get; set; } = "OpenAI Compatible";
    /// <summary>Base server URL without a trailing /v1 suffix.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Selected transcription model ID.</summary>
    public string? SelectedModelId { get; set; }
    /// <summary>Selected LLM model ID.</summary>
    public string? SelectedLlmModelId { get; set; }
    /// <summary>Models fetched from the provider. API keys are never stored here.</summary>
    public List<FetchedModel> FetchedModels { get; set; } = [];
    /// <summary>Display name with a fallback for unnamed profiles.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Custom Server" : Name.Trim();
}

/// <summary>
/// Model metadata fetched from an OpenAI-compatible provider.
/// </summary>
public sealed record FetchedModel(string Id, string? OwnedBy);
