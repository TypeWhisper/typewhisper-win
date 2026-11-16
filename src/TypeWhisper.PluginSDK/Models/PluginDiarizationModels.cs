namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Audio handed to a diarization provider. The host decodes the recording to mono PCM at
/// <see cref="SampleRate"/> (16 kHz in this host) before the call; the samples stay valid until the call returns.
/// </summary>
/// <param name="Samples">Mono PCM samples in [-1, 1].</param>
/// <param name="SampleRate">Samples per second of <paramref name="Samples"/>.</param>
public sealed record PluginDiarizationRequest(ReadOnlyMemory<float> Samples, int SampleRate = 16000)
{
    /// <summary>
    /// The host-owned source file the samples were decoded from, or null when the audio has no file. Providers that
    /// upload the original file may read it until the call returns; they must not keep, move or change it.
    /// </summary>
    public string? AudioPath { get; init; }

    /// <summary>The spoken language as an ISO code, or null when unknown. Providers may ignore it.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// A fixed number of speakers, or null to detect it. The host passes a count only when it lies within
    /// <see cref="IAudioDiarizationProvider.SupportedSpeakerCounts"/>.
    /// </summary>
    public int? SpeakerCount { get; init; }

    /// <summary>The length of the audio in seconds.</summary>
    public double DurationSeconds => SampleRate > 0 ? Samples.Length / (double)SampleRate : 0;
}

/// <summary>A stretch of audio that a diarization provider attributes to one speaker.</summary>
/// <param name="SpeakerLabel">
/// Provider-local label, stable within one result, such as <c>S1</c>. Labels name voices, not people; the host may
/// renumber them.
/// </param>
/// <param name="Start">Where the turn starts, in seconds from the start of the audio.</param>
/// <param name="End">Where the turn ends, in seconds from the start of the audio; after <paramref name="Start"/>.</param>
public sealed record PluginSpeakerTurn(string SpeakerLabel, double Start, double End)
{
    /// <summary>How sure the provider is about this turn, from 0 to 1, or null when it does not say.</summary>
    public double? Confidence { get; init; }
}

/// <summary>What a diarization provider found in one recording.</summary>
/// <param name="Turns">The speaker turns in any order; turns of different speakers may overlap. Empty without speech.</param>
/// <param name="Engine">A stable identifier of the engine, such as <c>nemotron3-onnx</c>, not a display name.</param>
public sealed record PluginDiarizationResult(IReadOnlyList<PluginSpeakerTurn> Turns, string Engine)
{
    /// <summary>The model version or revision that produced the result, or null.</summary>
    public string? ModelVersion { get; init; }

    /// <summary>One voice embedding per <see cref="PluginSpeakerTurn.SpeakerLabel"/>; empty when the provider has none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<float>> SpeakerEmbeddings { get; init; } =
        new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);

    /// <summary>
    /// A stable identifier of the model that produced <see cref="SpeakerEmbeddings"/>. Embeddings of different models
    /// are never compared; change it whenever the vectors change. Null uses <see cref="Engine"/>.
    /// </summary>
    public string? SpeakerEmbeddingModel { get; init; }
}

/// <summary>The fixed speaker counts a diarization provider accepts besides automatic detection.</summary>
/// <param name="Minimum">The smallest accepted count, at least 1.</param>
/// <param name="Maximum">The largest accepted count, at least <paramref name="Minimum"/>.</param>
public sealed record PluginSpeakerCountRange(int Minimum, int Maximum)
{
    /// <summary>Whether <paramref name="count"/> lies within the range.</summary>
    public bool Contains(int count) => count >= Minimum && count <= Maximum;
}

/// <summary>Why a diarization request failed.</summary>
public enum PluginDiarizationFailureKind
{
    /// <summary>The failure could not be classified.</summary>
    Unknown = 0,
    /// <summary>The models are not installed; the host offers <see cref="IAudioDiarizationProvider.PrepareDiarizationModelsAsync"/>.</summary>
    ModelsNotInstalled,
    /// <summary>The requested speaker count is outside <see cref="IAudioDiarizationProvider.SupportedSpeakerCounts"/>.</summary>
    UnsupportedSpeakerCount,
    /// <summary>The audio's sample rate or length is not supported by the provider.</summary>
    UnsupportedAudio,
    /// <summary>The provider is not configured, for example without an API key.</summary>
    Configuration,
    /// <summary>Inference or the remote service failed.</summary>
    ProcessingFailed
}

/// <summary>A classified diarization failure. The message is safe to show and names no secrets.</summary>
public class PluginDiarizationException : InvalidOperationException
{
    /// <summary>Creates a classified diarization failure.</summary>
    public PluginDiarizationException(string message, PluginDiarizationFailureKind failureKind, Exception? innerException = null)
        : base(message, innerException) => FailureKind = failureKind;

    /// <summary>Why the request failed.</summary>
    public PluginDiarizationFailureKind FailureKind { get; }
}
