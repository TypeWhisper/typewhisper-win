using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed record OpenAiFetchedModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owned_by")] string? OwnedBy);

internal sealed record OpenAiChatGptModel(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("priority")] int? Priority,
    [property: JsonPropertyName("available_in_plans")] List<string>? AvailableInPlans);
