using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>Independent portable Voxtral (Mistral API) provider, compared with Windows 4db8f6ac and macOS ac00e39e.</summary>
public sealed partial class VoxtralPlugin : ITranscriptionEnginePlugin, IApiKeyPlugin, IPluginTextSettings, ILlmProviderPlugin, ILlmRequestHedgingSupport, IPluginSettingsActions
{
    private readonly ProviderConnection Connection;
    private readonly ModelCatalogAutoRefresh _modelRefresh;
    // Key changes, deactivation and the hourly model list check take turns. The check runs on a timer thread; without
    // this gate it could refresh with a key that is being replaced, or after deactivation. The connection's own gate
    // cannot serve here: deactivation must not wait for a background save.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    /// <summary>Creates a provider with isolated HTTP transport.</summary>
    public VoxtralPlugin() : this(new HttpClient(new MistralErrorHandler(new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(5) },
        refreshModelsAutomatically: ModelCatalogAutoRefresh.AllowedInThisProcess) { }
    // Tests keep the background model list refresh off unless they opt in, so they never reach Mistral.
    internal VoxtralPlugin(HttpClient http, bool refreshModelsAutomatically = false, TimeProvider? clock = null)
    {
        Connection = new(http);
        _modelRefresh = new(refreshModelsAutomatically, clock);
    }
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.voxtral";
    /// <inheritdoc />
    public string PluginName => "Mistral";
    /// <inheritdoc />
    public string PluginVersion => "1.5.0";
    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        await Connection.ActivateAsync(host).ConfigureAwait(false);
        StartModelRefreshIfStale();
        // The app often runs for days without activating plugins again, so an active plugin checks every hour.
        _modelRefresh.StartRechecks(RecheckModelCatalog);
    }
    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try { _modelRefresh.StopRechecks(); _modelRefresh.Cancel(); Connection.Deactivate(); }
        finally { _refreshGate.Release(); }
    }
    /// <inheritdoc />
    public bool IsConfigured => Connection.Configured;
    bool ITranscriptionEnginePlugin.IsConfigured => Connection.Configured && SelectedModelId is not null;
    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A refresh still loading the previous account's list must not save it. A new key drops the saved list
            // (OnKeyReplacing), so the refresh that follows loads the new account's models.
            _modelRefresh.Cancel();
            try { await Connection.SetKeyAsync(apiKey).ConfigureAwait(false); }
            finally { StartModelRefreshIfStale(); }
        }
        finally { _refreshGate.Release(); }
    }
    /// <inheritdoc />
    public void Dispose() { _modelRefresh.Dispose(); Connection.Dispose(); _refreshGate.Dispose(); }

    private PluginTextSetting Field(string id, string en, string de, string fallback, PluginSettingsSection section,
        params PluginSettingChoice[] choices) => new(id, Connection.L(en, de), "", Connection.Get(id, fallback)) { Section = section, Choices = choices };
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = TextSettings.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("Unknown setting.");
        value = value.Trim();
        if (id is "model" or "llmModel")
        {
            // Aliases and retired IDs, e.g. from API clients or older settings, are saved as the listed model.
            value = (id == "model" ? ResolveTranscriptionModel(value) : ResolveTextModel(value))
                ?? throw new ArgumentException("Unknown model. Refresh models and select an available model.");
        }
        if (value.Length > 2048 || value.Any(char.IsControl) || (field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value)))
            throw new ArgumentException("Invalid setting value.");
        ValidateValue(id, value);
        return Connection.SaveAsync(id, value, cancellationToken);
    }
    private static void ValidateValue(string id, string value)
    {
        if (id == "temperature" && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || number > 2))
            throw new ArgumentException("Temperature must be a number from 0 to 2.");
        if (id == "accountId" && value.Length != 0 && (value.Length != 32 || !value.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Account ID must contain 32 hexadecimal characters.");
        if (id is "teamId" or "projectId" && value.Length != 0 && !Guid.TryParse(value, out _))
            throw new ArgumentException("Enter a valid UUID.");
        if (id == "baseUrl") _ = Endpoint(value);
        if (id is "llmModel" or "apiModel" && (value.Length == 0 || value.Any(char.IsWhiteSpace))) throw new ArgumentException("Enter a model ID.");
    }
    private static string Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTPS URL, or HTTP on localhost, without credentials, query or fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    /// <inheritdoc />
    public string ProviderId => "voxtral";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public string? SelectedModelId => ResolveTranscriptionModel(Connection.Get("model"))
        ?? ResolveTranscriptionModel(MistralCatalog.MiniTranscribe) ?? Catalog.Transcription.FirstOrDefault()?.Id;
    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        // Hosts that predate SelectModelAsync call this member and still block on the save.
        var id = ResolveTranscriptionModel(modelId) ?? throw new ArgumentException("Unknown transcription model.");
        Connection.SaveAsync("model", id, default).GetAwaiter().GetResult();
    }
    // Implemented implicitly: an explicit interface implementation would reference a member older SDKs lack and fail to load there.
    /// <inheritdoc />
    public Task SelectModelAsync(string modelId, CancellationToken ct)
    {
        var id = ResolveTranscriptionModel(modelId) ?? throw new ArgumentException("Unknown transcription model.");
        return Connection.SaveAsync("model", id, ct);
    }
}
