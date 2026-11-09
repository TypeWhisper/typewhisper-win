using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{
    internal const string RealtimeModel = MistralCatalog.RealtimeTranscribe;
    // Settings, selection and requests read the catalog often; parse the saved JSON once per change.
    private sealed record CatalogCache(string Saved, MistralModelCatalog Catalog);
    private volatile CatalogCache? _catalogCache;

    private MistralModelCatalog Catalog
    {
        get
        {
            var saved = Connection.Get("modelCatalog");
            if (_catalogCache is { } cached && cached.Saved == saved) return cached.Catalog;
            var catalog = MistralCatalog.Load(saved);
            _catalogCache = new(saved, catalog);
            return catalog;
        }
    }

    private MistralTranscriptionModel? SelectedTranscription =>
        SelectedModelId is { } id ? Catalog.Transcription.FirstOrDefault(m => m.Id == id) : null;
    private bool IsRealtime => SelectedTranscription?.Realtime == true;

    /// <summary>Model for complete recordings when a realtime model is selected: same family, uploaded in one request.</summary>
    private MistralTranscriptionModel? BatchModel =>
        Catalog.Transcription.FirstOrDefault(m => m.Id == MistralCatalog.MiniTranscribe)
        ?? Catalog.Transcription.FirstOrDefault(m => !m.Realtime);

    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public bool IsAvailable => IsConfigured && SupportedModels.Count > 0;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        Catalog.Text.Select(m => new PluginModelInfo(m.Id, MistralCatalog.DisplayName(m.Id, m.Name))).ToArray();
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        Catalog.Transcription.Select(m => new PluginModelInfo(m.Id, MistralCatalog.DisplayName(m.Id, m.Name))).ToArray();

    private string? ResolveTextModel(string? value) => Catalog.Resolve(value, Catalog.Text.Select(m => m.Id).ToArray());
    private string? ResolveTranscriptionModel(string? value) => Catalog.Resolve(value, Catalog.Transcription.Select(m => m.Id).ToArray());

    private string SelectedTextModel => ResolveTextModel(Connection.Get("llmModel"))
        ?? ResolveTextModel(MistralCatalog.SmallText) ?? Catalog.Text.FirstOrDefault()?.Id ?? "";

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("refreshModels", Connection.L("Refresh models", "Modelle aktualisieren"),
            Connection.L("Load transcription and text models using the saved API key.",
                "Transkriptions- und Textmodelle mit dem gespeicherten API-Schlüssel laden."))
        { Section = PluginSettingsSection.Connection }
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != "refreshModels") throw new ArgumentException("Unknown Mistral action.");
        var key = Connection.RequireKey();
        // The button loads the same list; a background refresh would only repeat the request.
        _modelRefresh.Cancel();
        var catalog = await FetchCatalogAsync(key, cancellationToken);
        await SaveCatalogAsync(catalog, key, cancellationToken);
        return Connection.L($"Models updated: {catalog.Transcription.Length} transcription, {catalog.Text.Length} text.",
            $"Modelle aktualisiert: {catalog.Transcription.Length} für Transkription, {catalog.Text.Length} für Text.");
    }

    /// <summary>The running or last background model list refresh. Tests await it.</summary>
    internal Task ModelCatalogRefreshTask => _modelRefresh.Current;
    internal bool RefreshesModelsAutomatically => _modelRefresh.Enabled;
    /// <summary>True when the saved list has no fetch date, is a day old or is dated in the future.</summary>
    internal bool IsModelCatalogStale => _modelRefresh.IsStale(Catalog.FetchedAtUtc);

    private async Task<MistralModelCatalog> FetchCatalogAsync(string key, CancellationToken ct)
    {
        using var request = Connection.Request(HttpMethod.Get, "https://api.mistral.ai/v1/models", key);
        using var document = await Connection.ReadAsync(request, ct).ConfigureAwait(false);
        return MistralCatalog.Parse(document.RootElement) with { FetchedAtUtc = _modelRefresh.UtcNow };
    }

    // The fetch date travels inside the saved list, so both are written at once and a key change drops both.
    private Task SaveCatalogAsync(MistralModelCatalog catalog, string key, CancellationToken ct) =>
        Connection.SaveAsync("modelCatalog", JsonSerializer.Serialize(catalog), ct, key);

    /// <summary>
    /// Mistral retires models and moves its aliases to newer ones. Reloading the list once a day drops a retired model
    /// instead of failing every request with HTTP 404.
    /// </summary>
    private void StartModelRefreshIfStale()
    {
        if (Connection.Host is not { } host || Connection.Key is not { } key || !IsModelCatalogStale)
            return;

        _modelRefresh.Start(ct => RefreshCatalogInBackgroundAsync(host, key, ct),
            ex => host.Log(PluginLogLevel.Warning, $"Mistral model list refresh failed ({Describe(ex)}). Keeping the current model list."));
    }

    /// <summary>
    /// The hourly check. It runs on a timer thread, so it takes the gate that key changes and deactivation hold. While the
    /// gate is busy the check is skipped: a key change starts its own refresh, and the next check follows an hour later.
    /// </summary>
    private void RecheckModelCatalog()
    {
        if (!_refreshGate.Wait(0)) return;
        try
        {
            // A key change can start a refresh after the helper checked for one; that refresh keeps running.
            if (_modelRefresh.Current.IsCompleted) StartModelRefreshIfStale();
        }
        finally { _refreshGate.Release(); }
    }

    private async Task RefreshCatalogInBackgroundAsync(IPluginHostServices host, string key, CancellationToken ct)
    {
        var catalog = await FetchCatalogAsync(key, ct).ConfigureAwait(false);
        var current = Catalog;
        // A list without transcription or text models would switch that capability off. The button saves such a list on
        // purpose; the background refresh keeps the current one, because a short or broken listing must not disable a
        // provider that worked a moment ago.
        if ((catalog.Transcription.Length == 0 && current.Transcription.Length > 0) || (catalog.Text.Length == 0 && current.Text.Length > 0))
        {
            host.Log(PluginLogLevel.Warning, "Mistral listed no transcription or no text models. Keeping the current model list.");
            return;
        }

        try
        {
            // The save checks the refresh token and the key again under the connection gate, so a result that arrives
            // after deactivation or a key change is dropped.
            await SaveCatalogAsync(catalog, key, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!ReferenceEquals(Connection.Host, host) || !string.Equals(Connection.Key, key, StringComparison.Ordinal))
        {
            // Deactivated or given another key meanwhile; the next activation or the new key loads the list again.
        }
    }

    // Logs never carry the key or a response body.
    private static string Describe(Exception ex) => ex is PluginRequestException request
        ? request.HttpStatusCode is { } status ? $"{request.FailureKind}, HTTP {status}" : request.FailureKind.ToString()
        : ex.GetType().Name;
}
