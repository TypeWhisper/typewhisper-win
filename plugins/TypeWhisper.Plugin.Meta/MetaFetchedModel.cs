using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.Meta;

internal sealed record MetaFetchedModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owned_by")] string? OwnedBy);

internal sealed record MetaModelCatalog(
    IReadOnlyList<MetaFetchedModel> LlmModels,
    IReadOnlyList<MetaFetchedModel> TranscriptionModels);
