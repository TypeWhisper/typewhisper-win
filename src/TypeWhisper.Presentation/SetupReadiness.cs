namespace TypeWhisper.Presentation;

/// <summary>Separates live setup readiness from persistence errors that must remain visible.</summary>
public sealed class SetupReadiness
{
    /// <summary>The last unsuccessful write or load; metadata refresh does not clear it.</summary>
    public string? PersistenceError { get; private set; }
    /// <summary>Records the outcome of an explicit persistence attempt.</summary>
    public void ReportPersistence(string? error) => PersistenceError = error;
    /// <summary>Returns a blocking reason using actual capture fallback availability, not only the preferred device.</summary>
    public static string? Validate(bool busy, bool hasShortcut, bool modelReady, bool microphoneAvailable) =>
        busy ? "Wait for the current operation to finish. You can skip setup."
        : !hasShortcut ? "Save a dictation shortcut before finishing."
        : !modelReady ? "Select a ready model in plugin settings before finishing."
        : !microphoneAvailable ? "No microphone is available. Connect one or skip setup."
        : null;
    /// <summary>Checks the prerequisite belonging to the current setup step.</summary>
    public static string? ValidateStep(int step, bool busy, bool hasShortcut, bool modelReady, bool microphoneAvailable) => step switch
    {
        0 => null,
        1 => microphoneAvailable ? null : "Connect a microphone to continue, or skip setup.",
        2 => hasShortcut ? null : "Save a dictation shortcut to continue.",
        3 => busy ? "Wait for the model to finish loading." : modelReady ? null : "Choose a ready model or configure its plugin to continue.",
        _ => Validate(busy, hasShortcut, modelReady, microphoneAvailable)
    };

    /// <summary>Preserves persistence errors across live readiness updates.</summary>
    public string Message(string? readiness) => PersistenceError ?? readiness ?? "Configuration is ready. Audio has not been tested by setup.";
}
