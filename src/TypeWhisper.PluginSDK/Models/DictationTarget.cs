namespace TypeWhisper.PluginSDK.Models;

/// <summary>The app a dictation began in, as the host tells a dictation target plugin.</summary>
/// <param name="DictationId">Identifies the dictation in later calls.</param>
/// <param name="ProcessName">The app's process name without ".exe".</param>
/// <param name="ExecutablePath">Full path of the app's executable, or null when Windows does not tell it.</param>
public sealed record DictationTargetInfo(Guid DictationId, string ProcessName, string? ExecutablePath);

/// <summary>How a dictation target plugin handles a dictation it accepted.</summary>
public sealed record DictationTargetPlan
{
    /// <summary>The app's name as users know it, for example "World of Warcraft". The host names a translation after it.</summary>
    public string? AppName { get; init; }

    /// <summary>
    /// The language to translate the dictation into, in English, or null to keep the spoken one. The host translates
    /// with the default LLM of its workflows when that can run and skips the translation otherwise. A workflow of the
    /// user's own for the app applies instead.
    /// </summary>
    public string? TranslateTo { get; init; }
}

/// <summary>The steps of a dictation a target plugin handles, in order. <see cref="Delivered"/> and <see cref="Stopped"/> end it.</summary>
public enum DictationTargetPhase
{
    /// <summary>The microphone is open.</summary>
    Recording,
    /// <summary>The recording ended; the speech is transcribed and processed.</summary>
    Transcribing,
    /// <summary>The plugin's delivery put text into the app.</summary>
    Delivered,
    /// <summary>
    /// The dictation ended without text from the plugin's delivery: canceled, failed, nothing was said, copied instead
    /// of pasted, or handled by a workflow action.
    /// </summary>
    Stopped
}

/// <summary>How much of a dictation's text reached the app.</summary>
/// <param name="Parts">How many parts the text went out in, for example chat messages; zero when nothing was left to send.</param>
/// <param name="Delivered">How many parts reached the app.</param>
/// <param name="StoppedAt">A short token naming the step that stopped the rest, such as "open" or "paste", for diagnostics.</param>
public sealed record DictationDeliveryResult(int Parts, int Delivered, string? StoppedAt = null)
{
    /// <summary>Whether every part reached the app.</summary>
    public bool Completed => Parts > 0 && Delivered == Parts;
}

/// <summary>An LLM by display name, and whether it can run now.</summary>
/// <param name="Name">The provider and model as the host shows them, such as "Groq · Llama 3.3 70B".</param>
/// <param name="Ready">Whether the provider is set up and offers the model.</param>
public sealed record PluginLlmStatus(string Name, bool Ready);
