using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides workflow palette service behavior.
/// </summary>
public sealed class WorkflowPaletteService
{
    private readonly IWorkflowService _workflows;
    private readonly IActiveWindowService _activeWindow;
    private readonly TextInsertionService _textInsertion;
    private readonly ISettingsService _settings;
    private readonly IWorkflowTextProcessor _textProcessor;
    private readonly PluginManager _pluginManager;
    private readonly IWorkflowPalettePresenter _presenter;

    /// <summary>
    /// Raised when feedback requested.
    /// </summary>
    public event Action<string, bool>? FeedbackRequested;

    /// <summary>
    /// Initializes a new instance of the WorkflowPaletteService class.
    /// </summary>
    public WorkflowPaletteService(
        IWorkflowService workflows,
        IActiveWindowService activeWindow,
        TextInsertionService textInsertion,
        ISettingsService settings,
        IWorkflowTextProcessor textProcessor,
        PluginManager pluginManager)
        : this(
            workflows,
            activeWindow,
            textInsertion,
            settings,
            textProcessor,
            pluginManager,
            new WorkflowPaletteWindowPresenter())
    {
    }

    internal WorkflowPaletteService(
        IWorkflowService workflows,
        IActiveWindowService activeWindow,
        TextInsertionService textInsertion,
        ISettingsService settings,
        IWorkflowTextProcessor textProcessor,
        PluginManager pluginManager,
        IWorkflowPalettePresenter presenter)
    {
        _workflows = workflows;
        _activeWindow = activeWindow;
        _textInsertion = textInsertion;
        _settings = settings;
        _textProcessor = textProcessor;
        _pluginManager = pluginManager;
        _presenter = presenter;
    }

    /// <summary>
    /// Toggles palette asynchronously.
    /// </summary>
    public async Task TogglePaletteAsync()
    {
        if (_presenter.IsVisible)
        {
            _presenter.Close();
            return;
        }

        var manualWorkflows = _workflows.Workflows
            .Where(static workflow =>
                workflow.IsEnabled
                && workflow.Trigger.Kind == WorkflowTriggerKind.Manual
                && workflow.IsManuallyRunnable)
            .OrderBy(workflow => workflow.SortOrder)
            .ThenBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (manualWorkflows.Count == 0)
        {
            FeedbackRequested?.Invoke(Loc.Instance["WorkflowPalette.NoManualWorkflows"], false);
            return;
        }

        var executionContext = await BuildExecutionContextAsync();
        if (executionContext is null)
        {
            FeedbackRequested?.Invoke(Loc.Instance["WorkflowPalette.NoText"], true);
            return;
        }

        var viewModel = new WorkflowPaletteViewModel(
            manualWorkflows,
            executionContext.SourceText,
            item => _ = ExecuteWorkflowWithContextAsync(item.Workflow, executionContext));

        _presenter.Show(viewModel, static () => { });
    }

    /// <summary>
    /// Executes workflow asynchronously..
    /// </summary>
    public async Task ExecuteWorkflowAsync(Workflow workflow)
    {
        if (!workflow.IsEnabled || !workflow.IsManuallyRunnable)
            return;

        var executionContext = await BuildExecutionContextAsync();
        if (executionContext is null)
        {
            FeedbackRequested?.Invoke(Loc.Instance["WorkflowPalette.NoText"], true);
            return;
        }

        await ExecuteWorkflowWithContextAsync(workflow, executionContext);
    }

    private async Task<WorkflowPaletteExecutionContext?> BuildExecutionContextAsync()
    {
        var targetWindowHandle = _activeWindow.GetActiveWindowHandle();
        var targetProcessName = _activeWindow.GetActiveWindowProcessName();
        var targetWindowTitle = _activeWindow.GetActiveWindowTitle();
        var targetUrl = _activeWindow.GetBrowserUrl();

        var sourceText = await _textInsertion.TryCaptureSelectedTextAsync(targetWindowHandle);
        sourceText = string.IsNullOrWhiteSpace(sourceText)
            ? await _textInsertion.TryGetClipboardTextAsync()
            : sourceText;

        return string.IsNullOrWhiteSpace(sourceText)
            ? null
            : new WorkflowPaletteExecutionContext(
                sourceText.Trim(),
                targetWindowHandle,
                targetProcessName,
                targetWindowTitle,
                targetUrl);
    }

    private async Task ExecuteWorkflowWithContextAsync(Workflow workflow, WorkflowPaletteExecutionContext context)
    {
        try
        {
            var processedText = context.SourceText;
            var workflowLanguage = workflow.Behavior
                .GetLanguageHints(_settings.Current.GetLanguageHints())
                .FirstOrDefault();
            if (workflow.SystemPrompt(
                    fallbackTranslationTarget: workflow.Behavior.TranslationTarget,
                    configuredLanguage: workflowLanguage) is { } systemPrompt)
            {
                if (!_textProcessor.IsAnyProviderAvailable)
                {
                    FeedbackRequested?.Invoke(Loc.Instance["Error.NoLlmProvider"], true);
                    return;
                }

                processedText = await _textProcessor.ProcessAsync(
                    systemPrompt,
                    context.SourceText,
                    workflow.Behavior.ProviderOverride,
                    workflow.Behavior.ModelOverride,
                    CancellationToken.None);
            }

            var targetActionPluginId = workflow.Output.TargetActionPluginId;
            if (!string.IsNullOrWhiteSpace(targetActionPluginId))
            {
                var actionPlugin = _pluginManager.ActionPlugins
                    .FirstOrDefault(plugin =>
                        string.Equals(plugin.PluginId, targetActionPluginId, StringComparison.Ordinal)
                        || string.Equals(plugin.ActionId, targetActionPluginId, StringComparison.Ordinal));

                if (actionPlugin is not null)
                {
                    var actionResult = await actionPlugin.ExecuteAsync(
                        processedText,
                        new ActionContext(
                            context.TargetWindowTitle,
                            context.TargetProcessName,
                            context.TargetUrl,
                            workflowLanguage,
                            context.SourceText),
                        CancellationToken.None);

                    FeedbackRequested?.Invoke(
                        actionResult.Message ?? (actionResult.Success ? Loc.Instance["Status.Done"] : "Failed"),
                        !actionResult.Success);
                    return;
                }
            }

            var insertionResult = await _textInsertion.InsertTextAsync(
                processedText,
                _settings.Current.AutoPaste,
                workflow.Output.AutoEnter,
                context.TargetWindowHandle);
            FeedbackRequested?.Invoke(StatusTextFor(insertionResult), false);
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ReportError(ex);
        }
    }

    private static string StatusTextFor(InsertionResult result) =>
        result switch
        {
            InsertionResult.Pasted => Loc.Instance["Status.Pasted"],
            InsertionResult.CopiedToClipboard => Loc.Instance["Status.Clipboard"],
            InsertionResult.NoText => Loc.Instance["WorkflowPalette.NoText"],
            _ => Loc.Instance["Status.Done"]
        };

    private void ReportError(Exception ex) =>
        FeedbackRequested?.Invoke(Loc.Instance.GetString("Status.ErrorFormat", ex.Message), true);
}

internal interface IWorkflowPalettePresenter
{
    bool IsVisible { get; }
    void Show(WorkflowPaletteViewModel viewModel, Action onClosed);
    void Close();
}

internal sealed class WorkflowPaletteWindowPresenter : IWorkflowPalettePresenter
{
    private WorkflowPaletteWindow? _window;

    /// <summary>
    /// Gets whether is visible.
    /// </summary>
    public bool IsVisible => _window is not null;

    /// <summary>
    /// Performs show.
    /// </summary>
    public void Show(WorkflowPaletteViewModel viewModel, Action onClosed)
    {
        var window = new WorkflowPaletteWindow(viewModel);
        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;

            onClosed();
        };

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Performs close.
    /// </summary>
    public void Close() => _window?.RequestClose();
}

internal sealed record WorkflowPaletteExecutionContext(
    string SourceText,
    IntPtr TargetWindowHandle,
    string? TargetProcessName,
    string? TargetWindowTitle,
    string? TargetUrl);
