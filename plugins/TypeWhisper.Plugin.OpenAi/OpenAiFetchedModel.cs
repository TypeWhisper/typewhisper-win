using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed record OpenAiFetchedModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owned_by")] string? OwnedBy);
