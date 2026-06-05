namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional stable selection identity for transcription engine roles.
/// </summary>
public interface ITranscriptionEngineSelectionIdentity
{
    /// <summary>Stable identifier used in plugin model selection IDs.</summary>
    string TranscriptionSelectionId { get; }
}

/// <summary>
/// Optional stable selection identity for LLM provider roles.
/// </summary>
public interface ILlmProviderSelectionIdentity
{
    /// <summary>Stable identifier used in plugin LLM selection IDs.</summary>
    string LlmSelectionId { get; }
}

/// <summary>
/// Helpers for resolving backward-compatible provider selection IDs.
/// </summary>
public static class PluginSelectionExtensions
{
    /// <summary>
    /// Returns the selection ID for a transcription engine role.
    /// Existing providers default to their plugin ID.
    /// </summary>
    public static string GetTranscriptionSelectionId(this ITranscriptionEnginePlugin plugin) =>
        plugin is ITranscriptionEngineSelectionIdentity identity
            && !string.IsNullOrWhiteSpace(identity.TranscriptionSelectionId)
                ? identity.TranscriptionSelectionId
                : plugin.PluginId;

    /// <summary>
    /// Returns the selection ID for an LLM provider role.
    /// Existing providers default to their plugin ID.
    /// </summary>
    public static string GetLlmSelectionId(this ILlmProviderPlugin plugin) =>
        plugin is ILlmProviderSelectionIdentity identity
            && !string.IsNullOrWhiteSpace(identity.LlmSelectionId)
                ? identity.LlmSelectionId
                : plugin.PluginId;
}
