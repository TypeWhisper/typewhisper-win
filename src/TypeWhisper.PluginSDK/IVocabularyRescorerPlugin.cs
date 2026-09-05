namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional acoustic vocabulary refinement after the main recognizer completes.
/// The host retains the original transcript when the plugin is disabled, unavailable or fails.
/// Never invoke this capability for a text-only preview or without user-enabled vocabulary terms.
/// </summary>
public interface IVocabularyRescorerPlugin : ITypeWhisperPlugin
{
    /// <summary>Whether the model and tokenizer are loaded and ready for scoring.</summary>
    bool IsReady { get; }

    /// <summary>Scores vocabulary candidates against audio. Cancellation must not publish a late result.</summary>
    Task<VocabularyRescoreResult> RescoreAsync(VocabularyRescoreRequest request, CancellationToken cancellationToken);
}

/// <summary>A user-enabled dictionary term, optionally overriding the acoustic similarity threshold.</summary>
/// <param name="Text">Preferred spelling, not a forced replacement.</param>
/// <param name="MinimumSimilarity">Optional value from zero to one, matching the Mac vocabulary hint.</param>
public sealed record VocabularyTermHint(string Text, float? MinimumSimilarity = null);

/// <summary>A main-recognizer token with times in seconds relative to the start of the supplied audio.</summary>
/// <param name="Text">The decoded token text.</param>
/// <param name="StartSeconds">Inclusive token start.</param>
/// <param name="EndSeconds">Exclusive token end.</param>
public sealed record VocabularyTokenTiming(string Text, double StartSeconds, double EndSeconds);

/// <summary>
/// Snapshot owned by the host for one final transcription. Audio is mono normalized float PCM.
/// The host must supply a dedicated audio copy and immutable term/timing snapshots to in-process plugins.
/// Plugins must not retain audio after the request finishes. Empty timings mean rescoring is unsupported.
/// </summary>
/// <param name="RecordingId">Identity used to reject results belonging to an obsolete recording.</param>
/// <param name="Text">Unmodified main-recognizer transcript.</param>
/// <param name="Audio">Audio used by the main recognizer.</param>
/// <param name="SampleRate">Samples per second; CTC implementations may require 16000.</param>
/// <param name="TokenTimings">Token timing snapshot aligned to Audio.</param>
/// <param name="Terms">Enabled vocabulary terms at recording start.</param>
public sealed record VocabularyRescoreRequest(Guid RecordingId, string Text, ReadOnlyMemory<float> Audio,
    int SampleRate, IReadOnlyList<VocabularyTokenTiming> TokenTimings, IReadOnlyList<VocabularyTermHint> Terms);

/// <summary>An auditable acoustic replacement in the original transcript.</summary>
/// <param name="Start">UTF-16 index in the original text.</param>
/// <param name="Length">UTF-16 length replaced in the original text.</param>
/// <param name="Term">Preferred vocabulary spelling supported by the acoustic score.</param>
/// <param name="Score">Finite model-specific evidence score, not necessarily a probability.</param>
public sealed record VocabularyReplacement(int Start, int Length, string Term, double Score);

/// <summary>The host validates identity, spans and enabled terms before applying replacements itself.</summary>
/// <param name="RecordingId">Recording identity copied from the request.</param>
/// <param name="Replacements">Non-overlapping replacements in original-text coordinates.</param>
public sealed record VocabularyRescoreResult(Guid RecordingId, IReadOnlyList<VocabularyReplacement> Replacements);
