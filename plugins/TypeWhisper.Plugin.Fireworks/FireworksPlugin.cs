using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Fireworks;

/// <summary>Independent portable Fireworks AI provider.</summary>
public sealed partial class FireworksPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, ILlmRequestHedgingSupport, IApiKeyPlugin, IPluginTextSettings
{
    private readonly ProviderConnection Connection;
    private readonly ModelCatalogAutoRefresh _catalogRefresh;
    // Held while the staleness check starts a refresh and while deactivation or a key change cancels it.
    private readonly object _refreshLock = new();
    /// <summary>Creates a provider with isolated HTTP transport.</summary>
    public FireworksPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) },
        refreshCatalogAutomatically: ModelCatalogAutoRefresh.AllowedInThisProcess) { }
    // Tests leave the background refresh off, so they never reach Fireworks.
    internal FireworksPlugin(HttpClient http, bool refreshCatalogAutomatically = false, TimeProvider? clock = null)
    {
        Connection = new(http);
        _catalogRefresh = new(refreshCatalogAutomatically, clock);
    }
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.fireworks";
    /// <inheritdoc />
    public string PluginName => "Fireworks AI";
    /// <inheritdoc />
    public string PluginVersion => "1.2.0";
    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        await Connection.ActivateAsync(host);
        StartCatalogRefreshIfStale();
        // Checks again every hour: the app often runs for days without activating plugins again.
        _catalogRefresh.StartRechecks(StartCatalogRefreshIfStale);
    }
    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        lock (_refreshLock)
        {
            _catalogRefresh.StopRechecks();
            _catalogRefresh.Cancel();
            Connection.Deactivate();
        }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public bool IsConfigured => Connection.Configured;
    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        var previous = Connection.Key;
        await Connection.SetKeyAsync(apiKey);
        if (string.Equals(previous, Connection.Key, StringComparison.Ordinal)) return;
        // The key change dropped the previous account's catalog; a refresh still running with that key stops.
        lock (_refreshLock) _catalogRefresh.Cancel();
        StartCatalogRefreshIfStale();
    }
    /// <inheritdoc />
    public void Dispose() { _catalogRefresh.Dispose(); Connection.Dispose(); }

    private PluginTextSetting Field(string id, string en, string de, string fallback, PluginSettingsSection section,
        params PluginSettingChoice[] choices) => new(id, Connection.L(en, de), "", Connection.Get(id, fallback)) { Section = section, Choices = choices };
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = TextSettings.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("Unknown setting.");
        value = value.Trim();
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
        if (id is "llmModel" or "apiModel" && (value.Length == 0 || value.Length > 256 || value.Any(char.IsWhiteSpace))) throw new ArgumentException("Enter a model ID.");
    }
    private static string Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTPS URL, or HTTP on localhost, without credentials, query or fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    /// <inheritdoc />
    public string ProviderId => "fireworks";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("whisper-v3-turbo", "Whisper V3 Turbo"), new("whisper-v3", "Whisper V3")];
    /// <inheritdoc />
    public string? SelectedModelId => TranscriptionModels.Any(m => m.Id == Connection.Get("model")) ? Connection.Get("model") : TranscriptionModels[0].Id;
    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        // Hosts that predate SelectModelAsync call this member and still block on the save.
        if (!TranscriptionModels.Any(m => m.Id == modelId)) throw new ArgumentException("Unknown transcription model.");
        Connection.SaveAsync("model", modelId, default).GetAwaiter().GetResult();
    }
    // Implemented implicitly: an explicit interface implementation would reference a member older SDKs lack and fail to load there.
    /// <inheritdoc />
    public Task SelectModelAsync(string modelId, CancellationToken ct)
    {
        if (!TranscriptionModels.Any(m => m.Id == modelId)) throw new ArgumentException("Unknown transcription model.");
        return Connection.SaveAsync("model", modelId, ct);
    }
}
