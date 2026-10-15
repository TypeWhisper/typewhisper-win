namespace TypeWhisper.Core.Models;

/// <summary>Where a plugin processes the content it receives, as declared by its manifest.</summary>
public enum ContentLocality
{
    /// <summary>Not declared or not recognized; treated as not local under restrictions.</summary>
    Unknown,
    /// <summary>Processing stays on this device.</summary>
    Local,
    /// <summary>A fixed remote service of the plugin's vendor.</summary>
    Cloud,
    /// <summary>A user-defined endpoint, script or command whose destination the app cannot verify.</summary>
    Configurable
}

/// <summary>Thrown when machine policy refuses an operation. The message is safe to show and names no secrets.</summary>
public sealed class ManagedPolicyDeniedException(string policyKey, string message) : InvalidOperationException(message)
{
    /// <summary>The policy key that caused the refusal.</summary>
    public string PolicyKey { get; } = policyKey;
}

/// <summary>The provider, cloud and download part of a policy snapshot, resolved to typed refusals.</summary>
/// <remarks>
/// Every refusal method returns null when the operation may proceed and a user-facing message otherwise.
/// A key whose enforced value is invalid or whose source is unreadable refuses the operations it governs.
/// Identifiers compare case-insensitively, as the plugin registry does.
/// </remarks>
/// <param name="CloudTranscriptionAllowed">False blocks remote audio processing; null is unmanaged.</param>
/// <param name="CloudTextProcessingAllowed">False blocks remote processing of transcript, prompt and contextual text; null is unmanaged.</param>
/// <param name="AllowedTranscriptionProviders">Stable transcription selection IDs that may be used, or null for all.</param>
/// <param name="AllowedLlmProviders">Stable LLM selection IDs that may be used, or null for all.</param>
/// <param name="AllowedPlugins">Package IDs that may be installed, loaded, enabled and executed, or null for all.</param>
/// <param name="PluginDownloadsAllowed">False blocks plugin acquisition and updates; null is unmanaged.</param>
/// <param name="ModelDownloadsAllowed">False blocks model and runtime asset downloads; null is unmanaged.</param>
/// <param name="BlockedKeys">Keys whose operations stay refused because their value is invalid or unreadable.</param>
public sealed record ManagedProviderPolicy(
    bool? CloudTranscriptionAllowed,
    bool? CloudTextProcessingAllowed,
    IReadOnlySet<string>? AllowedTranscriptionProviders,
    IReadOnlySet<string>? AllowedLlmProviders,
    IReadOnlySet<string>? AllowedPlugins,
    bool? PluginDownloadsAllowed,
    bool? ModelDownloadsAllowed,
    IReadOnlySet<string> BlockedKeys)
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>No policy: every operation proceeds.</summary>
    public static ManagedProviderPolicy None { get; } = new(null, null, null, null, null, null, null, NoKeys);

    /// <summary>The keys this policy reads.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        ManagedPolicyKeys.AllowCloudTranscription, ManagedPolicyKeys.AllowCloudTextProcessing,
        ManagedPolicyKeys.AllowedTranscriptionProviders, ManagedPolicyKeys.AllowedLlmProviders, ManagedPolicyKeys.AllowedPlugins,
        ManagedPolicyKeys.AllowPluginDownloads, ManagedPolicyKeys.AllowModelDownloads
    ];

    /// <summary>Reads the provider, cloud and download keys from a snapshot.</summary>
    public static ManagedProviderPolicy From(ManagedPolicySnapshot snapshot)
    {
        var blocked = new HashSet<string>(Keys.Where(snapshot.IsBlocked), StringComparer.Ordinal);
        return new(
            Boolean(snapshot, ManagedPolicyKeys.AllowCloudTranscription),
            Boolean(snapshot, ManagedPolicyKeys.AllowCloudTextProcessing),
            List(snapshot, ManagedPolicyKeys.AllowedTranscriptionProviders),
            List(snapshot, ManagedPolicyKeys.AllowedLlmProviders),
            List(snapshot, ManagedPolicyKeys.AllowedPlugins),
            Boolean(snapshot, ManagedPolicyKeys.AllowPluginDownloads),
            Boolean(snapshot, ManagedPolicyKeys.AllowModelDownloads),
            blocked);
    }

    /// <summary>Whether any provider, cloud or download policy applies.</summary>
    public bool IsManaged => CloudTranscriptionAllowed is not null || CloudTextProcessingAllowed is not null
        || AllowedTranscriptionProviders is not null || AllowedLlmProviders is not null || AllowedPlugins is not null
        || PluginDownloadsAllowed is not null || ModelDownloadsAllowed is not null || BlockedKeys.Count > 0;

    /// <summary>Why a package may not be installed, loaded, enabled or executed, or null.</summary>
    public string? PluginRefusal(string pluginId) =>
        Blocked(ManagedPolicyKeys.AllowedPlugins)
        ?? (AllowedPlugins is not null && !AllowedPlugins.Contains(pluginId)
            ? Loc.T("The plugin {0} is not allowed by your organization.", pluginId) : null);

    /// <summary>Why audio may not be sent to a transcription role, or null.</summary>
    public string? TranscriptionRefusal(string selectionId, string pluginId, ContentLocality locality) =>
        PluginRefusal(pluginId)
        ?? Blocked(ManagedPolicyKeys.AllowedTranscriptionProviders)
        ?? Blocked(ManagedPolicyKeys.AllowCloudTranscription)
        ?? (AllowedTranscriptionProviders is not null && !AllowedTranscriptionProviders.Contains(selectionId)
            ? Loc.T("The transcription provider {0} is not allowed by your organization.", selectionId) : null)
        ?? (CloudTranscriptionAllowed == false && locality != ContentLocality.Local
            ? Loc.T("Cloud transcription is turned off by your organization. Choose a local transcription model.") : null);

    /// <summary>Why text may not be sent to an LLM role, or null.</summary>
    public string? LlmRefusal(string selectionId, string pluginId, ContentLocality locality) =>
        PluginRefusal(pluginId)
        ?? Blocked(ManagedPolicyKeys.AllowedLlmProviders)
        ?? Blocked(ManagedPolicyKeys.AllowCloudTextProcessing)
        ?? (AllowedLlmProviders is not null && !AllowedLlmProviders.Contains(selectionId)
            ? Loc.T("The text model provider {0} is not allowed by your organization.", selectionId) : null)
        ?? (CloudTextProcessingAllowed == false && locality != ContentLocality.Local
            ? Loc.T("Cloud text processing is turned off by your organization. Choose a local text model.") : null);

    /// <summary>Why text may not be handed to a text processor, action, speech or memory plugin, or null.</summary>
    public string? TextTransferRefusal(string pluginId, ContentLocality locality) =>
        PluginRefusal(pluginId)
        ?? Blocked(ManagedPolicyKeys.AllowCloudTextProcessing)
        ?? (CloudTextProcessingAllowed == false && locality != ContentLocality.Local
            ? Loc.T("Sending text to {0} is turned off by your organization.", pluginId) : null);

    /// <summary>Why the app may not download or update plugins, or null.</summary>
    public string? PluginDownloadRefusal() =>
        Blocked(ManagedPolicyKeys.AllowPluginDownloads)
        ?? (PluginDownloadsAllowed == false ? Loc.T("Plugin downloads are turned off by your organization.") : null);

    /// <summary>Why the app may not download models or runtime assets, or null.</summary>
    public string? ModelDownloadRefusal() =>
        Blocked(ManagedPolicyKeys.AllowModelDownloads)
        ?? (ModelDownloadsAllowed == false ? Loc.T("Model downloads are turned off by your organization.") : null);

    private string? Blocked(string key) => BlockedKeys.Contains(key)
        ? Loc.T("Your organization's policy could not be read. This operation waits until it is readable.") : null;

    private static bool? Boolean(ManagedPolicySnapshot snapshot, string key) => snapshot.Enforced(key)?.IsTrue;

    private static IReadOnlySet<string>? List(ManagedPolicySnapshot snapshot, string key) =>
        snapshot.Enforced(key) is { Items: { } items } ? new HashSet<string>(items, StringComparer.OrdinalIgnoreCase) : null;
}
