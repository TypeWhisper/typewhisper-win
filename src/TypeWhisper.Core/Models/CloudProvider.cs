namespace TypeWhisper.Core.Models;

/// <summary>
/// Helper methods for cloud model ID handling.
/// Cloud model IDs use the format "provider:model" (e.g., "groq:whisper-large-v3").
/// </summary>
public static class CloudProvider
{
    public static bool IsCloudModel(string modelId) => modelId.Contains(':');

    public static (string ProviderId, string ModelId) ParseCloudModelId(string modelId)
    {
        var idx = modelId.IndexOf(':');
        if (idx < 0)
            throw new ArgumentException($"Not a cloud model ID: {modelId}");
        return (modelId[..idx], modelId[(idx + 1)..]);
    }

    public static string GetFullModelId(string providerId, string modelId) =>
        $"{providerId}:{modelId}";
}

public sealed record CloudModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ApiModelName { get; init; }
    public bool SupportsTranslation { get; init; } = true;
    public string ResponseFormat { get; init; } = "verbose_json";
}
