namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Priority-based post-processing pipeline that applies corrections, snippets,
/// plugin processors, LLM prompts, and translation in a defined order.
/// </summary>
public interface IPostProcessingPipeline
{
    /// <summary>
    /// Processes transcribed text through the full pipeline.
    /// </summary>
    Task<PostProcessingResult> ProcessAsync(
        string rawText,
        PipelineOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Options for configuring the post-processing pipeline.
/// </summary>
public sealed record PipelineOptions
{
    /// <summary>Applies dictionary corrections to text.</summary>
    public Func<string, string>? DictionaryCorrector { get; init; }

    /// <summary>Applies snippet expansion to text.</summary>
    public Func<string, string>? SnippetExpander { get; init; }

    /// <summary>Runs LLM prompt processing on text. Returns processed text.</summary>
    public Func<string, CancellationToken, Task<string>>? LlmHandler { get; init; }

    /// <summary>Translates text. Params: text, sourceLang, targetLang. Returns translated text.</summary>
    public Func<string, string, string, CancellationToken, Task<string>>? TranslationHandler { get; init; }

    /// <summary>Translation target language, or null if no translation needed.</summary>
    public string? TranslationTarget { get; init; }

    /// <summary>Effective source language for translation (e.g. "de", "en").</summary>
    public string? EffectiveSourceLanguage { get; init; }

    /// <summary>Detected language from transcription.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Plugin post-processors to include in the pipeline. Each delegate captures its own context.</summary>
    public IReadOnlyList<PluginPostProcessor>? PluginPostProcessors { get; init; }

    /// <summary>Callback to report status updates (e.g. "AI Processing...", "Translating...").</summary>
    public Func<string, Task>? StatusCallback { get; init; }
}

/// <summary>
/// Represents a plugin post-processor with its priority.
/// The delegate captures plugin-specific context (PostProcessingContext) in its closure.
/// </summary>
public sealed record PluginPostProcessor(
    int Priority,
    Func<string, CancellationToken, Task<string>> ProcessAsync);

/// <summary>
/// Result from the post-processing pipeline.
/// </summary>
public sealed record PostProcessingResult
{
    /// <summary>The fully processed text.</summary>
    public required string Text { get; init; }
}
