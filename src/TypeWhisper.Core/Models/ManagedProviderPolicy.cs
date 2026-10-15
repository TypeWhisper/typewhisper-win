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

/// <summary>A provider/model pair from policy: a stable selection ID and a model ID within that provider.</summary>
/// <param name="ProviderId">The stable transcription or LLM selection ID.</param>
/// <param name="ModelId">The model ID as the provider lists it.</param>
public sealed record ManagedSelection(string ProviderId, string ModelId)
{
    /// <summary>Whether this pair names the given provider, compared like the plugin registry does.</summary>
    public bool IsProvider(string providerId) => string.Equals(ProviderId, providerId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this pair names the given model, compared like the plugin registry does.</summary>
    public bool IsModel(string modelId) => string.Equals(ModelId, modelId, StringComparison.OrdinalIgnoreCase);
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
        ManagedPolicyKeys.TranscriptionProviderId, ManagedPolicyKeys.TranscriptionModelId,
        ManagedPolicyKeys.LlmProviderId, ManagedPolicyKeys.LlmModelId,
        ManagedPolicyKeys.AllowCloudTranscription, ManagedPolicyKeys.AllowCloudTextProcessing,
        ManagedPolicyKeys.AllowedTranscriptionProviders, ManagedPolicyKeys.AllowedLlmProviders, ManagedPolicyKeys.AllowedPlugins,
        ManagedPolicyKeys.AllowPluginDownloads, ManagedPolicyKeys.AllowModelDownloads
    ];

    /// <summary>The transcription provider and model every entry point must use, or null.</summary>
    public ManagedSelection? EnforcedTranscription { get; init; }

    /// <summary>The transcription provider and model a profile without an explicit choice starts with, or null.</summary>
    public ManagedSelection? RecommendedTranscription { get; init; }

    /// <summary>The LLM provider and model every workflow, translation and text step must use, or null.</summary>
    public ManagedSelection? EnforcedLlm { get; init; }

    /// <summary>The LLM provider and model a profile without an explicit default starts with, or null.</summary>
    public ManagedSelection? RecommendedLlm { get; init; }

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
            blocked)
        {
            EnforcedTranscription = Pair(snapshot, ManagedPolicyScope.Enforced, ManagedPolicyKeys.TranscriptionProviderId, ManagedPolicyKeys.TranscriptionModelId),
            RecommendedTranscription = Pair(snapshot, ManagedPolicyScope.Recommended, ManagedPolicyKeys.TranscriptionProviderId, ManagedPolicyKeys.TranscriptionModelId),
            EnforcedLlm = Pair(snapshot, ManagedPolicyScope.Enforced, ManagedPolicyKeys.LlmProviderId, ManagedPolicyKeys.LlmModelId),
            RecommendedLlm = Pair(snapshot, ManagedPolicyScope.Recommended, ManagedPolicyKeys.LlmProviderId, ManagedPolicyKeys.LlmModelId)
        };
    }

    /// <summary>Whether any provider, cloud or download policy applies.</summary>
    public bool IsManaged => CloudTranscriptionAllowed is not null || CloudTextProcessingAllowed is not null
        || AllowedTranscriptionProviders is not null || AllowedLlmProviders is not null || AllowedPlugins is not null
        || PluginDownloadsAllowed is not null || ModelDownloadsAllowed is not null || BlockedKeys.Count > 0
        || EnforcedTranscription is not null || RecommendedTranscription is not null || EnforcedLlm is not null || RecommendedLlm is not null;

    /// <summary>Whether transcription waits because the enforced pair is incomplete, invalid or unreadable.</summary>
    public bool TranscriptionSelectionBlocked => BlockedKeys.Contains(ManagedPolicyKeys.TranscriptionProviderId) || BlockedKeys.Contains(ManagedPolicyKeys.TranscriptionModelId);

    /// <summary>Whether LLM processing waits because the enforced pair is incomplete, invalid or unreadable.</summary>
    public bool LlmSelectionBlocked => BlockedKeys.Contains(ManagedPolicyKeys.LlmProviderId) || BlockedKeys.Contains(ManagedPolicyKeys.LlmModelId);

    /// <summary>Why a package may not be installed, loaded, enabled or executed, or null.</summary>
    public string? PluginRefusal(string pluginId) =>
        Blocked(ManagedPolicyKeys.AllowedPlugins)
        ?? (AllowedPlugins is not null && !AllowedPlugins.Contains(pluginId)
            ? Loc.T("The plugin {0} is not allowed by your organization.", pluginId) : null);

    /// <summary>
    /// Why a package may not be installed, loaded or enabled at all: it is outside the allowlist, or it processes content
    /// remotely and the cloud keys refuse every capability its categories declare. A package without recognized
    /// categories is judged by the allowlist only; its roles are still checked when they are used.
    /// </summary>
    public string? PackageRefusal(string pluginId, ContentLocality locality, IEnumerable<string>? categories)
    {
        if (PluginRefusal(pluginId) is { } refused) return refused;
        if (locality == ContentLocality.Local) return null;
        var ids = new HashSet<string>((categories ?? []).Select(id => id.Trim()).Where(id => id.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return null;
        var audio = ids.Contains("transcription");
        var text = ids.Any(id => !id.Equals("transcription", StringComparison.OrdinalIgnoreCase));
        var audioRefusal = !audio ? null : Blocked(ManagedPolicyKeys.AllowCloudTranscription) ?? (CloudTranscriptionAllowed == false ? CloudPackageRefusal : null);
        var textRefusal = !text ? null : Blocked(ManagedPolicyKeys.AllowCloudTextProcessing) ?? (CloudTextProcessingAllowed == false ? CloudPackageRefusal : null);
        if (audio && audioRefusal is null || text && textRefusal is null) return null;
        return audioRefusal ?? textRefusal;
    }

    private static string CloudPackageRefusal => Loc.T("This plugin processes content in the cloud, which your organization has turned off.");

    /// <summary>
    /// Why audio may not be sent to a transcription role, or null. With a model ID the enforced pair is checked
    /// completely; without one only its provider is, for callers that do not know the model yet.
    /// </summary>
    public string? TranscriptionRefusal(string selectionId, string pluginId, ContentLocality locality, string? modelId = null) =>
        Blocked(ManagedPolicyKeys.TranscriptionProviderId)
        ?? Blocked(ManagedPolicyKeys.TranscriptionModelId)
        ?? PluginRefusal(pluginId)
        ?? Blocked(ManagedPolicyKeys.AllowedTranscriptionProviders)
        ?? Blocked(ManagedPolicyKeys.AllowCloudTranscription)
        ?? (EnforcedTranscription is { } pair && !pair.IsProvider(selectionId)
            ? Loc.T("Your organization requires the transcription provider {0}.", pair.ProviderId) : null)
        ?? (AllowedTranscriptionProviders is not null && !AllowedTranscriptionProviders.Contains(selectionId)
            ? Loc.T("The transcription provider {0} is not allowed by your organization.", selectionId) : null)
        ?? (CloudTranscriptionAllowed == false && locality != ContentLocality.Local
            ? Loc.T("Cloud transcription is turned off by your organization. Choose a local transcription model.") : null)
        ?? (modelId is not null && EnforcedTranscription is { } required && !required.IsModel(modelId)
            ? Loc.T("Your organization requires the transcription model {0}.", required.ModelId) : null);

    /// <summary>
    /// Why text may not be sent to an LLM role, or null. With a model ID the enforced pair is checked completely;
    /// without one only its provider is.
    /// </summary>
    public string? LlmRefusal(string selectionId, string pluginId, ContentLocality locality, string? modelId = null) =>
        Blocked(ManagedPolicyKeys.LlmProviderId)
        ?? Blocked(ManagedPolicyKeys.LlmModelId)
        ?? PluginRefusal(pluginId)
        ?? Blocked(ManagedPolicyKeys.AllowedLlmProviders)
        ?? Blocked(ManagedPolicyKeys.AllowCloudTextProcessing)
        ?? (EnforcedLlm is { } pair && !pair.IsProvider(selectionId)
            ? Loc.T("Your organization requires the text model provider {0}.", pair.ProviderId) : null)
        ?? (AllowedLlmProviders is not null && !AllowedLlmProviders.Contains(selectionId)
            ? Loc.T("The text model provider {0} is not allowed by your organization.", selectionId) : null)
        ?? (CloudTextProcessingAllowed == false && locality != ContentLocality.Local
            ? Loc.T("Cloud text processing is turned off by your organization. Choose a local text model.") : null)
        ?? (modelId is not null && EnforcedLlm is { } required && !required.IsModel(modelId)
            ? Loc.T("Your organization requires the text model {0}.", required.ModelId) : null);

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

    // The validator only publishes a pair when both members are valid at the same scope.
    private static ManagedSelection? Pair(ManagedPolicySnapshot snapshot, ManagedPolicyScope scope, string providerKey, string modelKey)
    {
        var provider = scope == ManagedPolicyScope.Enforced ? snapshot.Enforced(providerKey) : snapshot.Recommended(providerKey);
        var model = scope == ManagedPolicyScope.Enforced ? snapshot.Enforced(modelKey) : snapshot.Recommended(modelKey);
        return provider?.Text is { } providerId && model?.Text is { } modelId ? new(providerId, modelId) : null;
    }

    private static IReadOnlySet<string>? List(ManagedPolicySnapshot snapshot, string key) =>
        snapshot.Enforced(key) is { Items: { } items } ? new HashSet<string>(items, StringComparer.OrdinalIgnoreCase) : null;
}
