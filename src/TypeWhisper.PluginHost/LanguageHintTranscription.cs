using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Routes ordered hints through the selected engine's explicit WAV capability.</summary>
public static class LanguageHintTranscription
{
    /// <summary>Preserves explicit language and PCM precedence, and forwards actual SDK results unchanged.</summary>
    /// <remarks>
    /// WAV uploads go through the host's transient retry policy; the PCM path of local engines does not,
    /// because an isolated engine already restarts its worker and a local failure is not transient.
    /// </remarks>
    /// <param name="retry">Overrides the default policy for the engine's plugin; tests pass one without delays.</param>
    public static Task<PluginTranscriptionResult> DecodeAsync(ITranscriptionEnginePlugin engine,
        ReadOnlyMemory<float> samples, Func<byte[]> encodeWav, string? language,
        IReadOnlyList<string> preferredLanguages, bool translate, CancellationToken ct, IReadOnlyList<string>? dictionaryTerms = null,
        TransientRequestRetry? retry = null)
    {
        var prompt = CreateDictionaryPrompt(engine, dictionaryTerms);
        if (translate && !engine.SupportsTranslation)
            throw new NotSupportedException("This provider cannot translate audio to English.");
        if (language is not null || preferredLanguages.Count == 0 || !engine.SupportsLanguageHints)
        {
            if (engine is IPcmTranscriptionEnginePlugin pcm) return prompt is null
                ? pcm.TranscribePcmAsync(samples, language, translate, ct)
                : pcm.TranscribePcmWithPromptAsync(samples, language, translate, prompt, ct);
            return ChunkedTranscription.DecodeAsync(samples, encodeWav, engine.MaximumAudioUploadBytes,
                (wav, token) => PluginRequestRetry.RunAsync(engine.PluginId,
                    attempt => engine.TranscribeAsync(wav, language, translate, prompt, attempt), token, retry), ct);
        }
        if (engine.SupportedLanguages.Count > 0 && preferredLanguages.Any(code => !engine.SupportedLanguages.Contains(code)))
            throw new InvalidOperationException("The selected provider does not support your preferred languages. Update Preferred languages or choose an explicit spoken language.");
        return ChunkedTranscription.DecodeAsync(samples, encodeWav, engine.MaximumAudioUploadBytes,
            (wav, token) => PluginRequestRetry.RunAsync(engine.PluginId,
                attempt => engine.TranscribeWithLanguageHintsAsync(wav, preferredLanguages, translate, prompt, attempt), token, retry), ct);
    }
    /// <summary>Applies the selected provider's dictionary budget before batch or streaming routing.</summary>
    public static string? CreateDictionaryPrompt(ITranscriptionEnginePlugin engine, IReadOnlyList<string>? terms) =>
        !engine.SupportsDictionaryTerms ? null
            : engine.SupportsStructuredDictionaryTerms
                ? PluginDictionaryTerms.CreateStructuredPrompt(terms, engine.DictionaryTermsBudget)
                : PluginDictionaryTerms.CreatePrompt(terms, engine.DictionaryTermsBudget);
}
