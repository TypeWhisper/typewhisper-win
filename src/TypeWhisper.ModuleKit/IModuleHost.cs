namespace TypeWhisper.ModuleKit;

/// <summary>What the host offers a module.</summary>
/// <remarks>
/// Members are used on the UI thread, and the host calls back on the UI thread. A capability is null when the host
/// does not provide it, for example in a test host, so modules check before use.
/// </remarks>
public interface IModuleHost
{
    /// <summary>The host's informational version, such as <c>"1.3.0"</c>.</summary>
    string HostVersion { get; }

    /// <summary>
    /// The code of the interface language, such as <c>"en"</c>, <c>"de"</c>, <c>"ja"</c> or <c>"zh-Hans"</c>. It does not
    /// change while the app runs. See <see cref="ModuleTranslations"/>.
    /// </summary>
    string InterfaceLanguage { get; }

    /// <summary>Reads History records and their changes.</summary>
    IHistoryReading? HistoryReading { get; }

    /// <summary>Reads the managed policy keys that modules declare in <see cref="ModuleContributions.ManagedPolicyKeys"/>.</summary>
    IManagedPolicyReading? ManagedPolicyReading { get; }

    /// <summary>Reports the user's Premium access and its changes.</summary>
    IPremiumAccessReading? PremiumAccessReading { get; }

    /// <summary>Keeps module data with the user's data, so it is exported and erased with it.</summary>
    IUserDataLifecycle? UserDataLifecycle { get; }

    /// <summary>Tells who spoke when in an audio file, with the diarization providers of enabled plugins.</summary>
    IAudioDiarization? AudioDiarization { get; }

    /// <summary>Searches the host's full-text index of History.</summary>
    IHistorySearching? HistorySearching => null;

    /// <summary>Sends prompts to the host's text model providers under the managed policy.</summary>
    ILanguageModelAnswering? LanguageModelAnswering => null;

    /// <summary>Writes events to the app's diagnostic log under this module's identifier.</summary>
    IModuleDiagnostics? Diagnostics => null;

    /// <summary>Records and transcribes a spoken input with the user's dictation microphone and engine.</summary>
    ISpeechInput? SpeechInput => null;

    /// <summary>
    /// Completes once the app has started: its windows can be shown and the managed policy keys that modules declare
    /// are readable. A module that awaits it on the UI thread continues there. It never fails or is cancelled, and it
    /// does not wait for models or plugins to load. A host without a startup returns a completed task.
    /// </summary>
    Task WhenStarted => Task.CompletedTask;

    /// <summary>
    /// Opens the settings page that holds the <see cref="ModuleSettingsSection"/> with this identifier and scrolls the
    /// section into view. Before startup has finished, and for an unknown identifier, nothing happens.
    /// </summary>
    /// <param name="sectionId">The section's <see cref="ModuleSettingsSection.Id"/>.</param>
    void ShowSettingsSection(string sectionId) { }

    /// <summary>
    /// Shows the <see cref="ModulePanel"/> with this identifier, creating its content on the first show. A panel that
    /// is already visible comes to the front. Before startup has finished, and for an unknown identifier, nothing
    /// happens.
    /// </summary>
    /// <param name="id">The panel's <see cref="ModulePanel.Id"/>.</param>
    void ShowPanel(string id);

    /// <summary>Hides the panel with this identifier if it is visible.</summary>
    /// <param name="id">The panel's <see cref="ModulePanel.Id"/>.</param>
    void HidePanel(string id);

    /// <summary>Hides the panel with this identifier if it is visible, and shows it otherwise.</summary>
    /// <param name="id">The panel's <see cref="ModulePanel.Id"/>.</param>
    void TogglePanel(string id);

    /// <summary>Whether the panel with this identifier is visible.</summary>
    /// <param name="id">The panel's <see cref="ModulePanel.Id"/>.</param>
    bool IsPanelVisible(string id);
}
