using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Routes ordered hints through the selected engine's explicit WAV capability.</summary>
public static class LanguageHintTranscription
{
    /// <summary>Preserves explicit language and PCM precedence, and forwards actual SDK results unchanged.</summary>
    public static Task<PluginTranscriptionResult> DecodeAsync(ITranscriptionEnginePlugin engine,
        ReadOnlyMemory<float> samples, Func<byte[]> encodeWav, string? language,
        IReadOnlyList<string> preferredLanguages, bool translate, CancellationToken ct)
    {
        if (translate && !engine.SupportsTranslation)
            throw new NotSupportedException("This provider cannot translate audio to English.");
        if (language is not null || preferredLanguages.Count == 0 || !engine.SupportsLanguageHints)
            return engine is IPcmTranscriptionEnginePlugin pcm
                ? pcm.TranscribePcmAsync(samples, language, translate, ct)
                : engine.TranscribeAsync(encodeWav(), language, translate, null, ct);
        if (engine.SupportedLanguages.Count > 0 && preferredLanguages.Any(code => !engine.SupportedLanguages.Contains(code)))
            throw new InvalidOperationException("The selected provider does not support your preferred languages. Update Preferred languages or choose an explicit spoken language.");
        return engine.TranscribeWithLanguageHintsAsync(encodeWav(), preferredLanguages, translate, null, ct);
    }
}
