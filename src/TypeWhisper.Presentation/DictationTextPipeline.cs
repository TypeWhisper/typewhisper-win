using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Final text and recoverable failures from configured dictation text steps.</summary>
public sealed record DictationTextPipelineResult
{
    /// <summary>The processed text, retaining the preceding text when an optional step fails.</summary>
    public required string Text { get; init; }
    /// <summary>User-facing failures in execution order.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Runs the shared Core pipeline with asynchronous snippet expansion and explicit dictionary steps.</summary>
public static class DictationTextPipeline
{
    /// <summary>
    /// Applies short punctuation, numbers, snippets, vocabulary boosting, dictionary corrections,
    /// and finally regional spelling. Snippets are awaited on the caller's context so their
    /// adapter can read clipboard text on the UI thread. Cancellation aborts the whole operation.
    /// </summary>
    public static async Task<DictationTextPipelineResult> ProcessAsync(
        string rawText,
        DictationTextPreferences preferences,
        string? configuredLanguage,
        string? detectedLanguage = null,
        Func<string, CancellationToken, Task<string>>? expandSnippets = null,
        Func<string, string>? boostVocabulary = null,
        Func<string, string>? correctDictionary = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!preferences.IsValid) throw new ArgumentException("Unsupported text preferences.", nameof(preferences));
        var warnings = new List<string>();
        Func<string, string>? Protect(string name, Func<string, string>? step) => step is null ? null : text =>
        {
            try { return step(text); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                warnings.Add($"{name} failed. Text from the preceding step was retained.");
                return text;
            }
        };
        IReadOnlyList<PluginPostProcessor>? snippets = expandSnippets is null ? null :
        [new(500, async (text, token) =>
        {
            try { return await expandSnippets(text, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                warnings.Add("Snippet expansion failed. Text from the preceding step was retained.");
                return text;
            }
        })];
        var result = await new PostProcessingPipeline().ProcessAsync(rawText, new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = preferences.TranscriptionNumberNormalizationEnabled,
            ShortUtterancePunctuationEnabled = preferences.ShortUtterancePunctuationEnabled,
            EnglishOutputVariant = preferences.EnglishOutputVariant,
            GermanOutputVariant = preferences.GermanOutputVariant,
            ConfiguredLanguage = configuredLanguage,
            DetectedLanguage = detectedLanguage,
            PluginPostProcessors = snippets,
            VocabularyBooster = Protect("Vocabulary boosting", boostVocabulary),
            DictionaryCorrector = Protect("Dictionary corrections", correctDictionary)
        }, ct);
        ct.ThrowIfCancellationRequested();
        return new() { Text = result.Text, Warnings = warnings.ToArray() };
    }
}
