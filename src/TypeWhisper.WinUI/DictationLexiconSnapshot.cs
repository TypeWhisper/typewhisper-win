using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Shared by microphone and file requests. Expansion never writes an older catalog back.
internal sealed class DictationLexiconSnapshot(DictationDictionarySnapshot? dictionary, DictationSnippetSnapshot? snippets)
{
    internal DictationDictionarySnapshot? Dictionary => dictionary;
    internal sealed record Result(string Text, IReadOnlyList<string> Warnings, IReadOnlyList<string> AppliedSnippetIds, string? WorkflowError = null, IReadOnlyList<TypeWhisper.Core.Models.TextProcessorProvenance>? TextProcessors = null);

    internal static DictationLexiconSnapshot Load(string dictionaryPath, string snippetPath) =>
        new(DictationDictionarySnapshot.Load(dictionaryPath), DictationSnippetSnapshot.Load(snippetPath));

    internal static bool CanRefineWithCtc(TranscriptionTask task, bool registryProvider, string? modelId,
        bool ready, int timingCount) => task == TranscriptionTask.Transcribe && !registryProvider &&
        modelId == "parakeet-tdt-0.6b" && ready && timingCount > 0;

    internal async Task<Result> ProcessAsync(string text, DictationTextPreferences preferences,
        string? configuredLanguage, string? detectedLanguage, bool boostVocabulary,
        Func<CancellationToken, Task<string>> readClipboard, CancellationToken ct,
        TranscriptionTask task, string? targetProcessName, string? engineId, string? modelId,
        Func<string, CancellationToken, Task<string>>? workflow = null, IReadOnlyList<DictationTextProcessor>? textProcessors = null)
    {
        var warnings = new List<string>();
        if (dictionary?.Error is { } dictionaryError) warnings.Add(dictionaryError);
        string[] appliedIds = [];
        var processed = await DictationTextPipeline.ProcessAsync(text, preferences, configuredLanguage, detectedLanguage,
            expandSnippets: snippets is null ? null : async (input, token) =>
            {
                string? clipboardText = null;
                if (await Task.Run(() => snippets.NeedsClipboard(input), token))
                {
                    try { clipboardText = await readClipboard(token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { System.Diagnostics.Debug.WriteLine("Snippet clipboard text unavailable: " + ex.Message); }
                }
                var expansion = await Task.Run(() => snippets.ApplyWithUsage(input,
                    clipboardText is null ? null : () => clipboardText), token);
                if (expansion.Error is { } error) warnings.Add(error);
                appliedIds = expansion.AppliedIds;
                return expansion.Text;
            },
            boostVocabulary: boostVocabulary && dictionary is not null ? dictionary.ApplyBoosting : null,
            correctDictionary: dictionary is not null ? dictionary.ApplyCorrections : null,
            ct: ct, task: task, targetProcessName: targetProcessName, engineId: engineId, modelId: modelId, workflow: workflow, textProcessors: textProcessors);
        warnings.AddRange(processed.Warnings);
        return new(processed.Text, warnings, appliedIds, processed.WorkflowError, processed.TextProcessors);
    }

    internal static string? RecordUsage(string path, IReadOnlyCollection<string> appliedIds)
    {
        try { SnippetUsageRecorder.Record(path, appliedIds); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        { return "Snippet usage could not be saved. Your transcript is unchanged."; }
    }
}
