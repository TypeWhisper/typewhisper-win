using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>Optional local-engine input that preserves captured floating-point PCM.</summary>
public interface IPcmTranscriptionEnginePlugin : ITranscriptionEnginePlugin
{
    /// <summary>Transcribes mono 16 kHz PCM, including token timing metadata when available.</summary>
    Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples,
        string? language, bool translate, CancellationToken cancellationToken);

    /// <summary>
    /// Transcribes mono 16 kHz PCM with provider-specific prompt context, such as dictionary terms.
    /// The host passes a prompt only to engines that opt into <see cref="ITranscriptionEnginePlugin.SupportsDictionaryTerms"/>.
    /// The default ignores the prompt, so engines that predate this member keep their behavior.
    /// </summary>
    Task<PluginTranscriptionResult> TranscribePcmWithPromptAsync(ReadOnlyMemory<float> samples,
        string? language, bool translate, string? prompt, CancellationToken cancellationToken) =>
        TranscribePcmAsync(samples, language, translate, cancellationToken);
}
