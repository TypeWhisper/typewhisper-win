using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Takes over dictation into the apps it lists, for example a game whose chat needs keys around the text. The host
/// tells the plugin only about dictations into those apps. For them the plugin can show the dictation in the app in
/// place of TypeWhisper's indicator, and it delivers the final text with the input the host provides.
/// </summary>
/// <remarks>
/// The host calls these members outside the queue that serializes other plugin calls, because live transcription
/// holds that queue while the user speaks. They can overlap each other and settings calls, so keep them thread-safe.
/// When the plugin is turned off or unloaded, the host cancels running calls and releases the plugin only after they
/// returned, so pass the cancellation token on to every wait.
/// </remarks>
public interface IDictationTargetPlugin : ITypeWhisperPlugin
{
    /// <summary>
    /// Process names, without ".exe", of the apps this plugin handles. The host reads them when it refreshes plugin
    /// capabilities.
    /// </summary>
    IReadOnlyList<string> TargetProcessNames { get; }

    /// <summary>
    /// Decides when a dictation into one of the listed apps begins whether this plugin handles it; null leaves it to
    /// the host. The user is already speaking: return within a few hundred milliseconds and make no network requests.
    /// </summary>
    Task<DictationTargetPlan?> PrepareDictationAsync(DictationTargetInfo target, CancellationToken cancellationToken);

    /// <summary>
    /// Reports the next step of a dictation this plugin handles. Runs on the host's UI thread and must return at once.
    /// Returns until when the app shows the dictation, or null when it does not; until then the host hides its own
    /// indicator while the app is in front.
    /// </summary>
    DateTimeOffset? OnDictationPhaseChanged(Guid dictationId, DictationTargetPhase phase, IDictationTargetInput input);

    /// <summary>
    /// Delivers the final text of a dictation this plugin handles. Text that did not fully arrive stays on the
    /// clipboard and in review, so the result must tell how much of it reached the app.
    /// </summary>
    Task<DictationDeliveryResult> DeliverDictationAsync(Guid dictationId, string text, IDictationTargetInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// Input into the window a dictation began in, provided by the host. Nothing is sent while another window is in
/// front, so keys and text never reach another app.
/// </summary>
public interface IDictationTargetInput
{
    /// <summary>Whether the window is in front and no modifier key is held, so pressed keys arrive as they are.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Presses virtual keys in order and releases them in reverse, as one input that TypeWhisper's own shortcuts
    /// ignore. Only while <see cref="IsReady"/>. Returns false when the keys were not all sent.
    /// </summary>
    bool PressKeys(IReadOnlyList<ushort> virtualKeys);

    /// <summary>
    /// Presses virtual keys while the user may still hold the dictation shortcut: a held Ctrl or Shift stays down and
    /// is left out, and nothing is sent while Alt or Windows is held, since either would turn the keys into others.
    /// Returns false when nothing was sent.
    /// </summary>
    bool SignalKeys(IReadOnlyList<ushort> virtualKeys);

    /// <summary>
    /// Pastes text into the window's focused field the way TypeWhisper pastes dictation, following the user's paste
    /// and clipboard settings. Returns false when it was not pasted.
    /// </summary>
    Task<bool> PasteAsync(string text, CancellationToken cancellationToken);

    /// <summary>Waits until the window handles input again, for example after a loading screen, for at most a second.</summary>
    Task WaitUntilResponsiveAsync(CancellationToken cancellationToken);
}
