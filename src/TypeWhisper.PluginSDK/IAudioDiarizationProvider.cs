using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional plugin capability that tells who spoke when in a recording (speaker diarization). It separates voices;
/// it does not identify people. Plugin API 1.3.0.
/// </summary>
/// <remarks>
/// The host owns the audio, its decoding, the alignment with transcription timing and the policy checks; the provider
/// owns its models and the inference. The host serializes calls into one plugin. A provider whose manifest declares a
/// <c>locality</c> other than <c>local</c> receives audio only while the organization allows cloud transcription.
/// </remarks>
public interface IAudioDiarizationProvider : ITypeWhisperPlugin
{
    /// <summary>A stable identifier, unique among diarization providers, such as the plugin ID.</summary>
    string DiarizationProviderId { get; }

    /// <summary>The provider's name for the UI.</summary>
    string DiarizationProviderDisplayName { get; }

    /// <summary>Whether the provider can run apart from its models, for example with its API key set.</summary>
    bool IsDiarizationConfigured => true;

    /// <summary>True when <see cref="DiarizeAsync"/> can run without downloading anything.</summary>
    bool AreDiarizationModelsInstalled { get; }

    /// <summary>The model <see cref="PrepareDiarizationModelsAsync"/> installs, for its name and size in the UI, or null.</summary>
    PluginModelInfo? DiarizationModel => null;

    /// <summary>Fixed speaker counts the provider accepts besides automatic detection, or null for automatic detection only.</summary>
    PluginSpeakerCountRange? SupportedSpeakerCounts => null;

    /// <summary>Downloads and verifies the models. Progress runs from 0 to 1. Does nothing when they are installed.</summary>
    /// <param name="progress">Receives the completed fraction between 0 and 1.</param>
    /// <param name="cancellationToken">Cancels the download; completed files may be kept for the next attempt.</param>
    Task PrepareDiarizationModelsAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Unloads the models and removes them from disk.</summary>
    /// <param name="cancellationToken">Cancels the removal.</param>
    Task DeleteDiarizationModelsAsync(CancellationToken cancellationToken);

    /// <summary>Frees loaded models; they load again on the next <see cref="DiarizeAsync"/>.</summary>
    Task UnloadDiarizationModelsAsync();

    /// <summary>
    /// Finds the speaker turns of one recording. Throws <see cref="OperationCanceledException"/> when cancelled and
    /// <see cref="PluginDiarizationException"/> for a classified failure.
    /// </summary>
    /// <param name="request">The audio and its optional hints.</param>
    /// <param name="progress">Receives the completed fraction between 0 and 1.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<PluginDiarizationResult> DiarizeAsync(PluginDiarizationRequest request, IProgress<double>? progress,
        CancellationToken cancellationToken);
}
