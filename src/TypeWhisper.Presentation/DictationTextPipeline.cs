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
    /// <summary>A required workflow failure; hosts must retain the text for review and disable automatic insertion.</summary>
    public string? WorkflowError { get; init; }
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
        CancellationToken ct = default,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        string? targetProcessName = null,
        string? engineId = null,
        string? modelId = null,
        Func<string, CancellationToken, Task<string>>? workflow = null)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!preferences.IsValid) throw new ArgumentException("Unsupported text preferences.", nameof(preferences));
        var warnings = new List<string>();
        var spokenFormatting = DictationFormatting.Resolve(preferences, engineId, modelId, configuredLanguage, detectedLanguage, task);
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
        var workflowFailed = false;
        try
        {
            var result = await new PostProcessingPipeline().ProcessAsync(rawText, new PipelineOptions
            {
                RequireLlmSuccess = workflow is not null,
                LlmHandler = workflow is null ? null : async (text, token) =>
                {
                    try
                    {
                        var output = await workflow(text, token);
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("The workflow returned no text.");
                        return output;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { workflowFailed = true; throw; }
                },
                TranscriptionNumberNormalizationEnabled = preferences.TranscriptionNumberNormalizationEnabled,
                ShortUtterancePunctuationEnabled = preferences.ShortUtterancePunctuationEnabled,
                EnglishOutputVariant = preferences.EnglishOutputVariant,
                GermanOutputVariant = preferences.GermanOutputVariant,
                TranscriptionTask = task,
                ConfiguredLanguage = configuredLanguage,
                DetectedLanguage = detectedLanguage,
                PluginPostProcessors = snippets,
                TargetProcessName = targetProcessName,
                AppFormatter = preferences.AppFormattingEnabled ? (text, process) => AppFormatterService.Format(text, process) : null,
                SpokenFormatter = text => DictationFormatting.Apply(text, spokenFormatting),
                VocabularyBooster = Protect("Vocabulary boosting", boostVocabulary),
                DictionaryCorrector = Protect("Dictionary corrections", correctDictionary)
            }, ct);
            ct.ThrowIfCancellationRequested();
            return new() { Text = result.Text, Warnings = warnings.ToArray() };
        }
        catch (Exception ex) when (workflowFailed && ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return new()
            {
                Text = rawText, Warnings = warnings.ToArray(),
                WorkflowError = "Workflow processing failed. Your transcript was retained for review; nothing was pasted."
            };
        }
    }
}
