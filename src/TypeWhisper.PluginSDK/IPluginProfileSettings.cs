namespace TypeWhisper.PluginSDK;

/// <summary>Optional profile editor with one save for profile fields and an optional replacement API key.</summary>
public interface IPluginProfileSettings : IPluginTextSettings
{
    /// <summary>The choice setting that selects the profile being edited, independently of the active provider.</summary>
    string ProfileSelectorId { get; }
    /// <summary>The settings action that creates and selects a profile, if supported.</summary>
    string? AddProfileActionId { get; }
    /// <summary>The settings action that removes the selected profile, if allowed.</summary>
    string? RemoveProfileActionId { get; }
    /// <summary>Validates and persists the selected profile together. Rejects stale identities and retains saved values on failure. A null API key keeps the stored key; supplied keys must use the host secret store.</summary>
    Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken);
    /// <summary>Tests the current editor values without persisting them. The plugin may retain fetched model suggestions until the explicit profile save.</summary>
    Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken);
}

/// <summary>Feedback from a draft action and whether it staged data for the next explicit save.</summary>
public sealed record PluginProfileActionResult(string Message, bool HasPendingChanges = false);
