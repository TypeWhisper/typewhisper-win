namespace TypeWhisper.PluginSDK;

/// <summary>An explicit user action offered by a plugin's settings page.</summary>
public sealed record PluginSettingsAction(string Id, string Title, string Description)
{
    /// <summary>Logical section containing this explicit action.</summary>
    public PluginSettingsSection Section { get; init; } = PluginSettingsSection.General;
}

/// <summary>Host-rendered configuration actions executed under the package lease.</summary>
public interface IPluginSettingsActions
{
    /// <summary>Listing actions must not perform network requests or import credentials.</summary>
    IReadOnlyList<PluginSettingsAction> SettingsActions { get; }
    /// <summary>Executes only the action explicitly selected by the user. Returns a non-secret status.</summary>
    Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken);
}
