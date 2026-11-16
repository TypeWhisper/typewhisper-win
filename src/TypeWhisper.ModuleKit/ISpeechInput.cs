namespace TypeWhisper.ModuleKit;

/// <summary>Takes a spoken input: records the user's dictation microphone and transcribes it.</summary>
/// <remarks>
/// The host records with the microphone the user chose for dictation and transcribes with the user's dictation
/// engine and model, under the same managed policy as dictation. It applies the dictionary and the text settings as
/// dictation does, but no snippets, workflows, text processors or memories. Nothing is inserted into an app, saved to
/// History or played as a sound, and the host shows no window or overlay for it; the module shows its own UI.
/// One spoken input runs at a time, and not while a dictation, recording or transcription runs. While it runs, the
/// host refuses to start a dictation, the recorder or a file transcription.
/// </remarks>
public interface ISpeechInput
{
    /// <summary>
    /// Records until <see cref="Stop"/> is called, the speaker has been silent for
    /// <see cref="ModuleSpeechInputOptions.SilenceTimeout"/> or <see cref="ModuleSpeechInputOptions.MaximumDuration"/>
    /// has passed, and then transcribes what was said.
    /// </summary>
    /// <param name="options">When the recording ends, or null for the defaults.</param>
    /// <param name="progress">Receives the states of the input on the UI thread, in order.</param>
    /// <param name="cancellationToken">Cancels the input; the recording is discarded.</param>
    /// <returns>The transcribed text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range.</exception>
    /// <exception cref="ModuleSpeechInputException">
    /// No text was produced; <see cref="ModuleSpeechInputException.Kind"/> says why and the message can be shown.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<ModuleSpeechInputResult> ListenAsync(ModuleSpeechInputOptions? options = null, IProgress<ModuleSpeechInputState>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the recording of the running <see cref="ListenAsync"/>, which then transcribes it. Does nothing when no
    /// recording runs.
    /// </summary>
    void Stop();
}

/// <summary>When a spoken input ends.</summary>
public sealed record ModuleSpeechInputOptions
{
    /// <summary>The shortest <see cref="SilenceTimeout"/>.</summary>
    public static TimeSpan MinimumSilenceTimeout { get; } = TimeSpan.FromSeconds(0.5);

    /// <summary>The longest <see cref="SilenceTimeout"/>.</summary>
    public static TimeSpan LongestSilenceTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest <see cref="MaximumDuration"/>.</summary>
    public static TimeSpan MinimumDuration { get; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest <see cref="MaximumDuration"/>.</summary>
    public static TimeSpan LongestDuration { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the speaker may be silent, after speech was first heard, before the recording ends; null to end only
    /// by <see cref="ISpeechInput.Stop"/> or <see cref="MaximumDuration"/>. Two seconds by default.
    /// </summary>
    public TimeSpan? SilenceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest recording, from when the microphone opens. One minute by default.</summary>
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>The stage of a spoken input.</summary>
public enum ModuleSpeechInputPhase
{
    /// <summary>The microphone is opening.</summary>
    Starting,
    /// <summary>The microphone records. Reported repeatedly with the current level.</summary>
    Listening,
    /// <summary>The recording has ended and is being transcribed.</summary>
    Transcribing,
    /// <summary>The text is ready; <see cref="ISpeechInput.ListenAsync"/> completes with it.</summary>
    Done
}

/// <summary>A state of a spoken input.</summary>
/// <param name="Phase">The stage.</param>
public sealed record ModuleSpeechInputState(ModuleSpeechInputPhase Phase)
{
    /// <summary>While listening, the loudness of the microphone for a level meter, from 0 (silent) to 1; otherwise 0.</summary>
    public double Level { get; init; }

    /// <summary>While listening, the time since the microphone opened; otherwise zero.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>While listening, whether speech has been heard yet.</summary>
    public bool HeardSpeech { get; init; }
}

/// <summary>What ended the recording of a spoken input.</summary>
public enum ModuleSpeechInputEnding
{
    /// <summary><see cref="ISpeechInput.Stop"/> was called.</summary>
    Stopped,
    /// <summary>The speaker was silent for <see cref="ModuleSpeechInputOptions.SilenceTimeout"/>.</summary>
    Silence,
    /// <summary><see cref="ModuleSpeechInputOptions.MaximumDuration"/> was reached.</summary>
    MaximumDuration
}

/// <summary>The text of a spoken input.</summary>
/// <param name="Text">The transcribed text after the dictionary and text settings, trimmed and not empty.</param>
/// <param name="Language">The spoken language as an ISO code, or null when unknown.</param>
/// <param name="DurationSeconds">The length of the recording.</param>
public sealed record ModuleSpeechInputResult(string Text, string? Language, double DurationSeconds)
{
    /// <summary>What ended the recording.</summary>
    public ModuleSpeechInputEnding Ending { get; init; }
}

/// <summary>Why a spoken input produced no text.</summary>
public enum ModuleSpeechInputErrorKind
{
    /// <summary>The microphone could not be opened or was lost while recording.</summary>
    MicrophoneUnavailable,
    /// <summary>No speech was heard or recognized.</summary>
    NoSpeech,
    /// <summary>The user has no transcription engine or model ready.</summary>
    NotSetUp,
    /// <summary>The managed policy does not allow the transcription. The message says why.</summary>
    Refused,
    /// <summary>A dictation, recording, transcription or another spoken input runs. Trying again later may work.</summary>
    Busy,
    /// <summary>The recording or the transcription failed.</summary>
    Failed
}

/// <summary>A spoken input produced no text. The message is safe to show.</summary>
public sealed class ModuleSpeechInputException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">Why no text was produced.</param>
    /// <param name="message">A message that can be shown, or null for a general one.</param>
    /// <param name="innerException">The host's error, if any.</param>
    public ModuleSpeechInputException(ModuleSpeechInputErrorKind kind, string? message = null, Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage(kind) : message, innerException) => Kind = kind;

    /// <summary>Why no text was produced.</summary>
    public ModuleSpeechInputErrorKind Kind { get; }

    private static string DefaultMessage(ModuleSpeechInputErrorKind kind) => kind switch
    {
        ModuleSpeechInputErrorKind.MicrophoneUnavailable => "The microphone is unavailable.",
        ModuleSpeechInputErrorKind.NoSpeech => "No speech was recognized.",
        ModuleSpeechInputErrorKind.NotSetUp => "No transcription model is ready.",
        ModuleSpeechInputErrorKind.Refused => "Your organization does not allow this transcription.",
        ModuleSpeechInputErrorKind.Busy => "Another recording or transcription is running.",
        _ => "The transcription failed."
    };
}
