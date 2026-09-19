using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{
    private sealed record ModelCatalog(PluginModelInfo[] Transcription, PluginModelInfo[] Text, string[]? Realtime = null);
    private static readonly ModelCatalog DefaultCatalog = new(
        [new("voxtral-mini-latest", "Voxtral Mini Latest"), new(RealtimeModel, "Voxtral Realtime")],
        [new("mistral-small-latest", "Mistral Small Latest")], [RealtimeModel]);

    internal const string RealtimeModel = "voxtral-mini-transcribe-realtime-2602";
    private bool IsRealtime => Catalog.Realtime?.Contains(SelectedModelId, StringComparer.Ordinal) == true;

    private ModelCatalog Catalog
    {
        get
        {
            var saved = Connection.Get("modelCatalog");
            if (saved.Length == 0) return DefaultCatalog;
            try
            {
                var catalog = JsonSerializer.Deserialize<ModelCatalog>(saved);
                return catalog is { Transcription: not null, Text: not null } ? catalog : DefaultCatalog;
            }
            catch (JsonException) { return DefaultCatalog; }
        }
    }

    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public bool IsAvailable => IsConfigured && SupportedModels.Count > 0;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => Catalog.Text;
    private string SelectedTextModel => SupportedModels.Any(m => m.Id == Connection.Get("llmModel"))
        ? Connection.Get("llmModel") : SupportedModels.FirstOrDefault(m => m.Id == "mistral-small-latest")?.Id
            ?? SupportedModels.FirstOrDefault()?.Id ?? "";

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
        var models = ProviderConnection.Required(document.RootElement, "data", JsonValueKind.Array);
        var transcription = new Dictionary<string, PluginModelInfo>(StringComparer.Ordinal);
        var text = new Dictionary<string, PluginModelInfo>(StringComparer.Ordinal);
        var realtime = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            var modelId = ProviderConnection.RequiredText(model, "id");
            if (modelId.Length > 256 || modelId.Any(char.IsWhiteSpace) || modelId.Any(char.IsControl))
                throw ProviderConnection.InvalidResponse();
            var capabilities = ProviderConnection.Required(model, "capabilities", JsonValueKind.Object);
            if (Flag(model, "archived")) continue;
            var info = new PluginModelInfo(modelId, modelId);
            // Realtime models use WebSocket audio, while batch models use multipart uploads.
            if (Flag(capabilities, "audio_transcription")) transcription[modelId] = info;
            if (Flag(capabilities, "audio_transcription_realtime"))
            {
                transcription[modelId] = info;
                realtime.Add(modelId);
            }
            if (Flag(capabilities, "completion_chat") && !Flag(capabilities, "audio_speech")) text[modelId] = info;
        }
        var catalog = new ModelCatalog(
            transcription.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray(),
            text.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray(),
            realtime.Order(StringComparer.Ordinal).ToArray());
        await Connection.SaveAsync("modelCatalog", JsonSerializer.Serialize(catalog), cancellationToken, key);
        return Connection.L($"Models updated: {catalog.Transcription.Length} transcription, {catalog.Text.Length} text.",
            $"Modelle aktualisiert: {catalog.Transcription.Length} für Transkription, {catalog.Text.Length} für Text.");
    }

    private static bool Flag(JsonElement item, string name) => item.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
}
