using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional capability for plugins that expose host-renderable credentials or
/// license acceptance requirements for model downloads.
/// </summary>
public interface IModelDownloadRequirementsProvider
{
    /// <summary>Raised when persisted requirement state changes.</summary>
    event EventHandler? ModelDownloadRequirementsChanged;

    /// <summary>Gets the current requirements for all plugin-managed models.</summary>
    IReadOnlyList<PluginModelDownloadRequirement> ModelDownloadRequirements { get; }

    /// <summary>Validates and stores a credential requirement.</summary>
    Task<PluginModelDownloadRequirementResult> SaveModelDownloadCredentialAsync(
        string modelId,
        string requirementId,
        string credential,
        CancellationToken ct) =>
        Task.FromResult(new PluginModelDownloadRequirementResult(
            false,
            "This plugin does not support credential configuration."));

    /// <summary>Clears a persisted credential requirement.</summary>
    Task ClearModelDownloadCredentialAsync(
        string modelId,
        string requirementId,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>Accepts or revokes a model license requirement.</summary>
    Task SetModelDownloadLicenseAcceptanceAsync(
        string modelId,
        string requirementId,
        bool accepted,
        CancellationToken ct) =>
        Task.FromException(new NotSupportedException(
            "This plugin does not support model license acceptance."));
}
