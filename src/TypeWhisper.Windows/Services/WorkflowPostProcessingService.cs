using System.Runtime.InteropServices;
using System.Windows;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.SpokenFormatting;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Captures the persisted context needed to run current workflow post-processing.
/// </summary>
public sealed record WorkflowPostProcessingRequest(
    string RawText,
    Workflow? Workflow,
    string? DetectedLanguage,
    string? ConfiguredLanguage,
    IReadOnlyList<string> ConfiguredLanguageCandidates,
    TranscriptionTask TranscriptionTask,
    string? TranscriptionEngineId,
    string? TranscriptionModelId,
    string? AppName,
    string? AppProcessName,
    double AudioDurationSeconds);

/// <summary>
/// Contains a completed shared post-processing result.
/// </summary>
public sealed record WorkflowPostProcessingResult(string Text, bool WorkflowPromptApplied);

/// <summary>
/// Runs the shared workflow post-processing pipeline.
/// </summary>
public interface IWorkflowPostProcessingService
{
    /// <summary>
    /// Processes raw text with the current workflow definition and current processing settings.
    /// </summary>
    Task<WorkflowPostProcessingResult> ProcessAsync(
        WorkflowPostProcessingRequest request,
        Func<string, Task>? statusCallback,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds the same current-settings post-processing pipeline for live dictation and history retry.
/// </summary>
public sealed class WorkflowPostProcessingService : IWorkflowPostProcessingService
{
    private readonly ISettingsService _settings;
    private readonly ModelManagerService _modelManager;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly ISnippetService _snippets;
    private readonly ITranslationService _translation;
    private readonly IWorkflowTextProcessor _workflowTextProcessor;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly SpokenFormattingService _spokenFormatting;
    private readonly SpokenFormattingStrategyResolver _spokenFormattingStrategyResolver;

    /// <summary>
    /// Creates a shared workflow post-processing service.
    /// </summary>
    public WorkflowPostProcessingService(
        ISettingsService settings,
        ModelManagerService modelManager,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        ISnippetService snippets,
        ITranslationService translation,
        IWorkflowTextProcessor workflowTextProcessor,
        IPostProcessingPipeline pipeline,
        SpokenFormattingService? spokenFormatting = null,
        SpokenFormattingStrategyResolver? spokenFormattingStrategyResolver = null)
    {
        _settings = settings;
        _modelManager = modelManager;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _snippets = snippets;
        _translation = translation;
        _workflowTextProcessor = workflowTextProcessor;
        _pipeline = pipeline;
        var spokenFormattingRules = new SpokenFormattingRulesLoader();
        _spokenFormatting = spokenFormatting ?? new SpokenFormattingService(spokenFormattingRules);
        _spokenFormattingStrategyResolver = spokenFormattingStrategyResolver
            ?? new SpokenFormattingStrategyResolver(
                new SpokenFormattingProfileStore(settings),
                spokenFormattingRules);
    }

    /// <summary>
    /// Gets whether the supplied workflow currently has a post-processing prompt.
    /// </summary>
    public static bool HasWorkflowPrompt(Workflow? workflow, string? detectedLanguage, string? configuredLanguage) =>
        workflow?.SystemPrompt(
            fallbackTranslationTarget: workflow.Behavior.TranslationTarget,
            detectedLanguage: detectedLanguage,
            configuredLanguage: configuredLanguage == "auto" ? null : configuredLanguage) is not null;

    /// <summary>
    /// Processes raw text with the current workflow definition and current processing settings.
    /// </summary>
    public async Task<WorkflowPostProcessingResult> ProcessAsync(
        WorkflowPostProcessingRequest request,
        Func<string, Task>? statusCallback,
        CancellationToken cancellationToken)
    {
        var context = new PostProcessingContext
        {
            SourceLanguage = request.DetectedLanguage ?? request.ConfiguredLanguage,
            ActiveAppName = request.AppName,
            ActiveAppProcessName = request.AppProcessName,
            ProfileName = request.Workflow?.Name,
            AudioDurationSeconds = request.AudioDurationSeconds
        };

        Func<string, CancellationToken, Task<string>>? llmHandler = null;
        var systemPrompt = request.Workflow?.SystemPrompt(
            fallbackTranslationTarget: request.Workflow.Behavior.TranslationTarget,
            detectedLanguage: request.DetectedLanguage,
            configuredLanguage: request.ConfiguredLanguage == "auto" ? null : request.ConfiguredLanguage);
        var workflowRequiresLlm = systemPrompt is not null;
        if (systemPrompt is not null)
        {
            if (!_workflowTextProcessor.IsAnyProviderAvailable)
                throw new InvalidOperationException(Localization.Loc.Instance["Error.NoLlmProvider"]);

            var behavior = request.Workflow!.Behavior;
            llmHandler = (text, token) => _workflowTextProcessor.ProcessAsync(
                systemPrompt,
                text,
                behavior.ProviderOverride,
                behavior.ModelOverride,
                token);
        }

        var pluginProcessors = _modelManager.PluginManager.PostProcessors
            .Select(plugin => new PluginPostProcessor(
                plugin.Priority,
                (text, token) => plugin.ProcessAsync(text, context, token)))
            .ToList();
        var translationTarget = request.Workflow is null
            ? _settings.Current.TranslationTargetLanguage
            : null;
        var spokenFormattingContext = _spokenFormattingStrategyResolver.Resolve(
            request.TranscriptionEngineId,
            request.TranscriptionModelId,
            request.ConfiguredLanguageCandidates,
            request.DetectedLanguage);
        var options = new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = _settings.Current.TranscriptionNumberNormalizationEnabled,
            ShortUtterancePunctuationEnabled = _settings.Current.ShortUtterancePunctuationEnabled,
            NormalizeNumbersOverride = request.Workflow?.Output.NumberNormalizationMode.OverrideValue(),
            GermanOutputVariant = _settings.Current.GermanOutputVariant,
            TranscriptionTask = request.TranscriptionTask,
            DetectedLanguage = request.DetectedLanguage,
            ConfiguredLanguage = request.ConfiguredLanguage == "auto" ? null : request.ConfiguredLanguage,
            ConfiguredLanguageCandidates = request.ConfiguredLanguageCandidates,
            AppFormatter = AppFormatterService.Format,
            SpokenFormatter = CreateSpokenFormatter(_spokenFormatting, spokenFormattingContext),
            TargetProcessName = request.AppProcessName,
            VocabularyBooster = _settings.Current.VocabularyBoostingEnabled
                ? _vocabularyBoosting.Apply
                : null,
            DictionaryCorrector = _dictionary.ApplyCorrections,
            SnippetExpander = text => _snippets.ApplySnippets(text, ReadClipboardText),
            LlmHandler = llmHandler,
            RequireLlmSuccess = workflowRequiresLlm,
            TranslationHandler = !string.IsNullOrEmpty(translationTarget)
                ? (text, source, target, token) => _translation.TranslateAsync(text, source, target, token)
                : null,
            TranslationTarget = translationTarget,
            RequireTranslationSuccess = !string.IsNullOrEmpty(translationTarget),
            EffectiveSourceLanguage = request.ConfiguredLanguage == "auto" ? null : request.ConfiguredLanguage,
            PluginPostProcessors = pluginProcessors,
            StatusCallback = statusCallback
        };

        var result = await _pipeline.ProcessAsync(request.RawText, options, cancellationToken).ConfigureAwait(false);
        return new WorkflowPostProcessingResult(result.Text, workflowRequiresLlm);
    }

    internal static Func<string, string>? CreateSpokenFormatter(
        SpokenFormattingService service,
        ResolvedSpokenFormattingStrategy? context) => context?.Strategy switch
        {
            SpokenFormattingStrategy.Automatic => text => service.Normalize(
                text,
                context.LanguageCode,
                SpokenFormattingApplicationMode.SelectiveFallback),
            SpokenFormattingStrategy.FallbackOnly => text => service.Normalize(
                text,
                context.LanguageCode,
                SpokenFormattingApplicationMode.FullFallback),
            _ => null
        };

    private static string ReadClipboardText()
    {
        var text = string.Empty;
        var application = Application.Current;
        if (application is null)
            return text;

        try
        {
            application.Dispatcher.Invoke(() => text = Clipboard.GetText());
        }
        catch (ExternalException)
        {
            // Clipboard context is optional and must not fail dictation processing.
        }
        return text;
    }
}
