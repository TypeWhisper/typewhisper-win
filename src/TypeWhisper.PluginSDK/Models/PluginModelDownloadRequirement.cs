namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Identifies a host-renderable prerequisite for downloading local model assets.
/// </summary>
public enum PluginModelDownloadRequirementKind
{
    /// <summary>A secret credential such as a Hugging Face access token.</summary>
    Credential,

    /// <summary>A model license that must be accepted before downloading.</summary>
    License
}

/// <summary>
/// Describes one optional or required prerequisite for a plugin-managed model download.
/// </summary>
/// <param name="ModelId">Stable plugin model identifier.</param>
/// <param name="ModelDisplayName">Human-readable model name.</param>
/// <param name="Id">Stable requirement identifier within the plugin.</param>
/// <param name="Kind">Requirement input kind rendered by the host.</param>
/// <param name="Title">Short localized requirement title.</param>
/// <param name="Description">Localized explanation shown to the user.</param>
/// <param name="IsRequired">Whether downloads must be blocked until satisfied.</param>
/// <param name="IsSatisfied">Whether the current persisted value satisfies the requirement.</param>
public sealed record PluginModelDownloadRequirement(
    string ModelId,
    string ModelDisplayName,
    string Id,
    PluginModelDownloadRequirementKind Kind,
    string Title,
    string Description,
    bool IsRequired,
    bool IsSatisfied)
{
    /// <summary>Optional information or license URL.</summary>
    public Uri? MoreInfoUri { get; init; }

    /// <summary>Optional license revision used to invalidate an older acceptance.</summary>
    public string? Revision { get; init; }
}

/// <summary>
/// Reports the result of updating a model download requirement.
/// </summary>
/// <param name="Succeeded">Whether the value was validated and persisted.</param>
/// <param name="Message">Optional localized status or error message.</param>
public sealed record PluginModelDownloadRequirementResult(bool Succeeded, string? Message = null);
