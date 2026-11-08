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
        using var request = Connection.Request(HttpMethod.Get, "https://api.mistral.ai/v1/models", key);
        using var document = await Connection.ReadAsync(request, cancellationToken);
        var catalog = MistralCatalog.Parse(document.RootElement);
        await Connection.SaveAsync("modelCatalog", JsonSerializer.Serialize(catalog), cancellationToken, key);
        return Connection.L($"Models updated: {catalog.Transcription.Length} transcription, {catalog.Text.Length} text.",
            $"Modelle aktualisiert: {catalog.Transcription.Length} für Transkription, {catalog.Text.Length} für Text.");
    }
}
