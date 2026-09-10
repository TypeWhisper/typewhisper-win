using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Routes ordered hints through the selected engine's explicit WAV capability.</summary>
public static class LanguageHintTranscription
{
    /// <summary>Preserves explicit language and PCM precedence, and forwards actual SDK results unchanged.</summary>
    public static Task<PluginTranscriptionResult> DecodeAsync(ITranscriptionEnginePlugin engine,
        ReadOnlyMemory<float> samples, Func<byte[]> encodeWav, string? language,
        IReadOnlyList<string> preferredLanguages, bool translate, CancellationToken ct, IReadOnlyList<string>? dictionaryTerms = null)
    {
        var prompt = CreateDictionaryPrompt(engine, dictionaryTerms);
        if (translate && !engine.SupportsTranslation)
            throw new NotSupportedException("This provider cannot translate audio to English.");
        if (language is not null || preferredLanguages.Count == 0 || !engine.SupportsLanguageHints)
            return engine is IPcmTranscriptionEnginePlugin pcm
                ? pcm.TranscribePcmAsync(samples, language, translate, ct)
                : engine.TranscribeAsync(encodeWav(), language, translate, prompt, ct);
        if (engine.SupportedLanguages.Count > 0 && preferredLanguages.Any(code => !engine.SupportedLanguages.Contains(code)))
            throw new InvalidOperationException("The selected provider does not support your preferred languages. Update Preferred languages or choose an explicit spoken language.");
        return engine.TranscribeWithLanguageHintsAsync(encodeWav(), preferredLanguages, translate, prompt, ct);
    }
    /// <summary>Applies the selected provider's dictionary budget before batch or streaming routing.</summary>
    public static string? CreateDictionaryPrompt(ITranscriptionEnginePlugin engine, IReadOnlyList<string>? terms) =>
        engine.SupportsDictionaryTerms ? PluginDictionaryTerms.CreatePrompt(terms, engine.DictionaryTermsBudget) : null;
}
