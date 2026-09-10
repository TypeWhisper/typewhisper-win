using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>Optional local-engine input that preserves captured floating-point PCM.</summary>
public interface IPcmTranscriptionEnginePlugin : ITranscriptionEnginePlugin
{
    /// <summary>Transcribes mono 16 kHz PCM, including token timing metadata when available.</summary>
    Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples,
        string? language, bool translate, CancellationToken cancellationToken);
}
