namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional interface for plugins that want to report progress or status messages
/// in their settings view. The host UI can display these to the user.
/// </summary>
public interface IPluginSettingsActivity
{
    /// <summary>
    /// Raised when the plugin wants to show a progress/status message in settings.
    /// Message is null when the activity completes.
    /// </summary>
    event Action<string?>? SettingsActivityChanged;

    /// <summary>
    /// Current progress (0.0 to 1.0) or null if indeterminate.
    /// </summary>
    double? SettingsProgress { get; }
}
