using System.Collections.ObjectModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

public sealed partial class WorkflowsView : UserControl
{
    private enum Page { List, Editor, Result, Configuration }
    private Page _page;
    private readonly HandCursorButton _templateHelp = SettingsHelp.Button(Loc.T("Template"), Loc.T("Choose a workflow template."));
    private readonly HandCursorButton _shortcutHelp = SettingsHelp.Button(Loc.T("Shortcut"), Loc.T("Choose a shortcut to activate this workflow."));
    private WorkflowDraft? _opened;
    private (string Id, string Message)? _workflowRunFailure;
    private string _query = string.Empty;
    private readonly Dictionary<string, string> _drafts = [];
    private readonly List<WorkflowDraft> _workflows = [];
    private Page _configurationReturnPage;
    private bool _loadingConfiguration;
    private bool _creating;
    private WorkflowDraft? _selectionBeforeCreate;
    private Action? _afterConfigurationExit;
    private LocalDictationSession? _session;
    // All lifecycle and UI callbacks are owned by the dispatcher thread.
    private bool _closing;
    private TaskCompletionSource? _runCompletion;
    private TaskCompletionSource? _deleteCompletion;
    private ContentDialog? _deleteDialog;
    internal Task ShutdownAsync()
    {
        _closing = true;
        IsEnabled = false;
        _run?.Cancel();
        try { _deleteDialog?.Hide(); _defaultsDialog?.Hide(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Workflow dialog close failed: " + ex); }
        return Task.WhenAll(_defaultsCompletion?.Task ?? Task.CompletedTask, _runCompletion?.Task ?? Task.CompletedTask, _deleteCompletion?.Task ?? Task.CompletedTask);
    }

    private ManualWorkflowStore? _store;
    internal WorkflowShortcutCatalog? Shortcuts { get; set; }
    private readonly Dictionary<string, string> _shortcutDraft = [];
    private string DraftHotkeys => _shortcutDraft.GetValueOrDefault("WorkflowSelectedTextHotkeys", "");
    private string? ShortcutDraftError => Shortcuts is null ? Loc.T("Wait for workflow shortcuts to initialize.")
        : Shortcuts.ValidateDraft(_opened?.Id ?? "", DraftHotkeys, ConfigEnabled.IsOn);
    private void Shortcut_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var recorder = ConfigShortcutHost.Children.OfType<ShortcutRecorder>().FirstOrDefault(r => r.IsEditing);
        if (recorder is not null && (recorder.IsCapturing || e.Key == global::Windows.System.VirtualKey.Escape)) recorder.CaptureKeyDown(e);
    }
    private void Shortcut_PreviewKeyUp(object sender, KeyRoutedEventArgs e) =>
        ConfigShortcutHost.Children.OfType<ShortcutRecorder>().FirstOrDefault(r => r.IsCapturing)?.CaptureKeyUp(e);
    internal bool IsBusy => _defaultsDialog is not null || _run is not null || _page == Page.Configuration || _deleteCompletion is { Task.IsCompleted: false };
    private string? _loadError;
    private CancellationTokenSource? _run;
    private IReadOnlyList<Choice> Providers => [new(WorkflowLlmDefaults.Inherit, Loc.T("Use default"), Loc.T("Use the shared workflow LLM")), new("none", Loc.T("Not configured"), Loc.T("Choose an installed LLM provider")),
        .. (_session?.LlmProviders.Select(p => new Choice(p.SelectionId, p.Name, p.Ready ? Loc.T("Ready") : Loc.T("Requires configuration")) { PluginId = p.PluginId }) ?? [])];

    internal void Connect(LocalDictationSession session)
    {
        if (_session is not null) _session.Changed -= RuntimeChanged;
        _session = session;
        _session.Changed += RuntimeChanged;
        _store = new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json"));
        try
        {
            _workflows.Clear();
            _workflows.AddRange(_store.Read()
                .Select(WorkflowDraft.FromStored));
            _loadError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _loadError = Loc.T("Workflows could not be loaded. Existing data has not been changed."); }
        Filter("");
    }

    private static StackPanel HelpHeading(string title, HandCursorButton help)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(help);
        return row;
    }

    private void RuntimeChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_page == Page.Configuration)
        {
            ConfigureActionTargets(ConfigActionTarget.SelectedId);
            ConfigureMemorySources(ConfigMemory.SelectedId);
            ConfigProvider.SetOptions(Providers, ConfigProvider.SelectedId, Loc.T("{0} (unavailable)", ConfigProvider.SelectedId));
            ConfigureModels(ConfigModel.SelectedId);
            UpdateConfigurationState();
        }
        else UpdateSourceState();
    });

    private void ConfigureMemorySources(string selected) => ConfigMemory.SetOptions([new("", Loc.T("Off"), Loc.T("Do not use saved memories")), .. (_session?.PluginRuntime.MemoryProviders.OrderBy(p => p.Name).Select(p => new Choice(p.PluginId, p.Name, Loc.T("Use matching saved facts")) { PluginId = p.PluginId }) ?? [])], selected, Loc.T("Unavailable memory source: {0}", selected));

    private void ConfigureActionTargets(string selected) => ConfigActionTarget.SetOptions([new("", Loc.T("Insert Text"), Loc.T("Use dictation output preferences")), .. (_session?.PluginRuntime.Actions.OrderBy(a => a.Name).Select(a => new Choice(a.PluginId, a.Name, Loc.T("Send the workflow result to this action")) { PluginId = a.PluginId }) ?? [])], selected, Loc.T("Unavailable action: {0}", selected));

    private bool Available(string provider, string model) => _session?.LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model)) == true;
    internal ObservableCollection<WorkflowDraft> FilteredWorkflows { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? ConfigurationModeChanged;
    internal event Action<bool>? DetailModeChanged;
    internal event Action<string>? ConfigurationSaved;
    internal bool EditWorkflow(string id)
    {
        if (_closing || IsBusy) return false;
        var workflow = _workflows.FirstOrDefault(item => item.Id == id && item.IsEditable);
        if (workflow is null) return false;
        _opened = workflow;
        _creating = false;
        _configurationReturnPage = Page.List;
        LoadConfiguration();
        return true;
    }
    internal bool IsDetail => _page != Page.List;
    internal bool IsConfiguring => _page == Page.Configuration;

    public WorkflowsView()
    {
        InitializeComponent();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WorkflowList, Loc.T("Saved workflows"));
        WorkflowExecutionSummary.Text = Loc.T("Choose a provider and model in Edit workflow.");
        SourceTextLabel.Text = Loc.T("SOURCE TEXT");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WorkflowSource, Loc.T("Workflow source text"));
        SourceWatermark.Text = Loc.T("Paste or type your text…");
        ResultLabel.Text = Loc.T("RESULT");
        WorkflowResultStatus.Text = Loc.T("Review the workflow result.");
        TemplateHint.Text = Loc.T("Choose the result this workflow should produce.");
        ActionTargetHeading.Text = Loc.T("Action Target");
        MemoryHeading.Text = Loc.T("Memory context");
        MemoryHint.Text = Loc.T("Optional: search saved facts related to your text. Up to five matches are sent to this workflow’s LLM provider. Nothing is remembered automatically. Dictation-only workflows do not use memory context.");
        TriggerHeading.Text = Loc.T("Trigger");
        ConfigAppProcesses.PlaceholderText = Loc.T("notepad, chrome (without .exe)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigAppProcesses, Loc.T("Workflow process names"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigWebsiteDomains, Loc.T("Workflow website domains"));
        BehaviorHeading.Text = Loc.T("Behavior");
        TaskLabel.Text = Loc.T("Transcription task");
        ConfigEnabled.Header = Loc.T("Enable workflow");
        ConfigEnabled.OnContent = Loc.T("Enabled");
        ConfigEnabled.OffContent = Loc.T("Disabled");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigEnabled, Loc.T("Enable workflow"));
        NameLabel.Text = Loc.T("NAME");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigName, Loc.T("Workflow name"));
        ConfigTranslationTarget.PlaceholderText = Loc.T("English (default)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigTranslationTarget, Loc.T("Workflow translation target language"));
        ConfigAdvanced.Header = Loc.T("Advanced");
        PriorityLabel.Text = Loc.T("PRIORITY (LOWER WINS)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigPriority, Loc.T("Workflow priority"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigInstruction, Loc.T("Workflow instructions"));
        ModelLabel.Text = Loc.T("MODEL");
        DeleteWorkflowButton.Content = Loc.T("Delete workflow");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DeleteWorkflowButton, Loc.T("Delete workflow"));
        KeepWorkflowEditing.Content = Loc.T("Keep editing");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(KeepWorkflowEditing, Loc.T("Keep workflow changes"));
        DiscardWorkflowChanges.Content = Loc.T("Discard changes");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiscardWorkflowChanges, Loc.T("Discard workflow changes"));
        DefaultLlmButton.Content = Loc.T("Default LLM");
        ConfigureWorkflowButton.Content = Loc.T("Edit workflow");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConfigureWorkflowButton, Loc.T("Edit selected workflow"));
        NewWorkflowButton.Content = Loc.T("+  New workflow");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NewWorkflowButton, Loc.T("Create new workflow"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WorkflowPrimaryButton, Loc.T("Workflow primary action"));
        EntryActionMenu.Attach(this, WorkflowContextActions);
        TemplateHelp.Child = HelpHeading(Loc.T("Template"), _templateHelp);
        InitializeIconPicker();
        ShortcutHelp.Child = HelpHeading(Loc.T("Shortcut"), _shortcutHelp);
        ActivationHelp.Child = SettingsHelp.Label(Loc.T("Activation"), Loc.T("Matching app and website rules take precedence, followed by website, app, then global fallback. Lower priority numbers win within a group; equal priorities use the workflow name. Dictation shortcuts apply their workflow and transcription task for one recording, overriding these automatic rules. The selected action target receives the finished workflow result."), 12);
        AppProcessesHelp.Child = SettingsHelp.Label(Loc.T("Windows process names"), Loc.T("Required for App activation; optional for Website activation. Separate process names with commas."), 12);
        WebsiteDomainsHelp.Child = SettingsHelp.Label(Loc.T("Website domains"), Loc.T("Required for Website activation; optional for App activation. Domains include subdomains (example.com also matches mail.example.com). Use commas, without paths or query strings. The browser address is read once before recording; only the hostname can enter saved History. Chrome, Edge, Brave, Chromium and Firefox require a recognized address bar. Missing context leaves app/global fallback rules available."), 12);
        ContextModeHelp.Child = SettingsHelp.Label(Loc.T("App and website conditions"), Loc.T("Match all requires an app from your list AND a domain from your list. Match any allows either component, so the app rule can still run when a browser address is unavailable."), 12);
        TranslationHelp.Child = SettingsHelp.Label(Loc.T("Translation language"), Loc.T("Leave empty to translate into English. This is a text workflow using the selected LLM provider."), 12);
        ProviderHelp.Child = SettingsHelp.Label(Loc.T("Provider"), Loc.T("Manual workflows run when you choose Run. Selected-text shortcuts send your selection and instructions to the configured provider and open the result for review. App, Website and Global workflows run on matching dictations and use dictation output settings; failures open review without pasting."), 12);
        WorkflowList.SelectionChanged += (_, _) =>
        {
            if (_page == Page.List) ConfigureWorkflowButton.IsEnabled = WorkflowList.SelectedItem is WorkflowDraft { IsEditable: true };
        };
        ConfigMemory.Configure(Loc.T("Memory context"), "file", Loc.T("Workflow memory source"));
        ConfigMemory.SelectionChanged += _ => UpdateConfigurationState();
        ConfigActionTarget.Configure(Loc.T("Action Target"), "plugin", Loc.T("Workflow action target"));
        ConfigActionTarget.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTrigger.Configure(Loc.T("Activation"), "workflow", Loc.T("Workflow activation"));
        ConfigTrigger.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTask.Configure(Loc.T("Transcription task"), "microphone", Loc.T("Workflow transcription task"));
        ConfigTask.SelectionChanged += _ => UpdateConfigurationState();
        ConfigContextMode.Configure(Loc.T("App and website conditions"), "workflow", Loc.T("Workflow context match mode"));
        ConfigContextMode.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTemplate.SelectionChanged += _ =>
        {
            if (!_loadingConfiguration && Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var template))
            {
                var suggested = WorkflowTemplateCatalog.DefinitionFor(template).Name;
                ConfigName.Text = WorkflowTemplateNames.ForSelection(ConfigName.Text, _suggestedName, suggested);
                _suggestedName = suggested;
                ConfigAdvanced.IsExpanded = template == WorkflowTemplate.Custom;
            }
            UpdateConfigurationState();
        };
        ConfigProvider.Configure(Loc.T("Provider"), "plugin", Loc.T("Workflow provider"));
        ConfigModel.Configure(Loc.T("Model"), "chip", Loc.T("Workflow model"));
        ConfigProvider.SelectionChanged += _ =>
        {
            ConfigureModels(string.Empty);
            UpdateConfigurationState();
        };
        ConfigModel.SelectionChanged += _ => UpdateConfigurationState();
        Unloaded += (_, _) => _run?.Cancel();
        Filter(string.Empty);
    }

    internal void Filter(string query)
    {
        if (_run is not null) { if (query != _query) _run.Cancel(); return; }
        if (_page == Page.Configuration) return;
        if (query == _query && IsDetail) return;
        _query = query;
        var selected = WorkflowList.SelectedItem as WorkflowDraft;
        FilteredWorkflows.Clear();
        foreach (var item in _workflows.Where(item => query.Length == 0
            || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase))) FilteredWorkflows.Add(item);
        WorkflowList.SelectedItem = FilteredWorkflows.FirstOrDefault(item => item.Id == selected?.Id) ?? FilteredWorkflows.FirstOrDefault();
        ShowPage(Page.List);
        WorkflowEmptyState.Visibility = FilteredWorkflows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Without any workflow, a search hint would be misleading; offer the first workflow instead.
        var none = _workflows.Count == 0;
        WorkflowEmptyTitle.Text = none ? Loc.T("No workflows yet") : Loc.T("No workflows found");
        WorkflowEmptyAction.Content = none ? Loc.T("Create first workflow") : Loc.T("Clear search");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WorkflowEmptyAction, none ? Loc.T("Create first workflow") : Loc.T("Clear workflow search"));
    }

    internal void MoveSelection(int offset)
    {
        if (IsDetail || FilteredWorkflows.Count == 0) return;
        WorkflowList.SelectedIndex = Math.Clamp(WorkflowList.SelectedIndex + offset, 0, FilteredWorkflows.Count - 1);
        WorkflowList.ScrollIntoView(WorkflowList.SelectedItem);
    }

    internal void OpenWorkflow(string id)
    {
        if (_closing || IsBusy) return;
        Filter(string.Empty);
        WorkflowList.SelectedItem = FilteredWorkflows.FirstOrDefault(workflow => workflow.Id == id);
        OpenSelected();
    }

    internal void OpenSelected()
    {
        if (IsDetail || WorkflowList.SelectedItem is not WorkflowDraft workflow) return;
        _opened = workflow;
        WorkflowInstruction.Text = workflow.InstructionDescription;
        WorkflowSource.Text = (_drafts.GetValueOrDefault(workflow.Id) ?? "").ReplaceLineEndings("\r");
        ShowPage(Page.Editor);
        FocusEntry();
    }

    internal void FocusEntry()
    {
        if (_page == Page.Editor) { if (_opened?.IsEditable == false) WorkflowEnableButton.Focus(FocusState.Programmatic); else WorkflowSource.Focus(FocusState.Programmatic); }
        else if (_page == Page.Configuration)
        {
            if (ConfigurationDiscardPrompt.Visibility == Visibility.Visible) KeepWorkflowEditing.Focus(FocusState.Programmatic);
            else if (!ConfigTrigger.IsPopupOpen && !ConfigContextMode.IsPopupOpen && !ConfigProvider.IsPopupOpen && !ConfigModel.IsPopupOpen && !ConfigActionTarget.IsPopupOpen && !ConfigMemory.IsPopupOpen && !ConfigTask.IsPopupOpen) ConfigName.Focus(FocusState.Programmatic);
        }
        else if (_page == Page.Result) WorkflowPrimaryButton.Focus(FocusState.Programmatic);
    }

    private void ShowPage(Page page)
    {
        _page = page;
        WorkflowListPage.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        WorkflowEditorPage.Visibility = page == Page.Editor ? Visibility.Visible : Visibility.Collapsed;
        WorkflowResultPage.Visibility = page == Page.Result ? Visibility.Visible : Visibility.Collapsed;
        WorkflowConfigurationPage.Visibility = page == Page.Configuration ? Visibility.Visible : Visibility.Collapsed;
        WorkflowPageTitle.Text = page == Page.Configuration ? (_creating ? Loc.T("New workflow") : Loc.T("Edit workflow")) : page == Page.List ? Loc.T("Workflows") : _opened?.Title ?? Loc.T("Workflow");
        WorkflowSummary.Text = page == Page.List
            ? (FilteredWorkflows.Count == 1 ? Loc.T("1 workflow") : Loc.T("{0} workflows", FilteredWorkflows.Count)) : Loc.T("Workflow");
        UpdateBreadcrumbs();
        // Settings has no Backspace navigation, and Esc on the list closes the window.
        WorkflowNavigationHint.Text = page switch { Page.Configuration => Loc.T("Esc Cancel   Ctrl S Save"), Page.Editor => Loc.T("Esc Back   Ctrl Enter Run"), Page.Result => Loc.T("Esc Back"), _ => Loc.T("\u2191\u2193 Navigate   Enter Open") };
        WorkflowPrimaryButton.Visibility = page == Page.List ? Visibility.Collapsed : Visibility.Visible;
        WorkflowPrimaryButton.Content = page == Page.Configuration ? (_creating ? Loc.T("Create workflow") : Loc.T("Save changes")) : page == Page.Result ? Loc.T("Copy result") : RunButtonLabel;
        UpdateExecutionSummary();
        if (_loadError is not null) WorkflowSummary.Text = _loadError;
        else if (Shortcuts?.Error is { } shortcutError) WorkflowSummary.Text = shortcutError;
        ConfigureWorkflowButton.Visibility = page is Page.List or Page.Editor ? Visibility.Visible : Visibility.Collapsed;
        NewWorkflowButton.IsEnabled = _loadError is null && _store is not null;
        DefaultLlmButton.Visibility = page is Page.List or Page.Configuration ? Visibility.Visible : Visibility.Collapsed;
        NewWorkflowButton.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        ConfigureWorkflowButton.IsEnabled = page == Page.List
            ? WorkflowList.SelectedItem is WorkflowDraft { IsEditable: true } : _opened?.IsEditable == true;
        ConfigurationModeChanged?.Invoke(page == Page.Configuration);
        DetailModeChanged?.Invoke(page != Page.List);
        UpdateSourceState();
    }

    internal void GoBack()
    {
        if (_closing) return;
        if (_run is not null) { _run.Cancel(); return; }
        if (_page == Page.Configuration)
        {
            foreach (var picker in new[] { ConfigTrigger, ConfigContextMode, ConfigProvider, ConfigModel, ConfigActionTarget, ConfigMemory, ConfigTask })
                if (picker.IsPopupOpen) { picker.ClosePopup(); return; }
            if (ConfigurationDiscardPrompt.Visibility == Visibility.Visible) { _afterConfigurationExit = null; DismissDiscard(); return; }
            if (!ConfigurationDirty) { LeaveConfiguration(); return; }
            ConfigurationDiscardPrompt.Visibility = Visibility.Visible;
            ConfigurationScroll.IsEnabled = WorkflowPrimaryButton.IsEnabled = false;
            WorkflowConfigurationPage.Opacity = 0.2;
            KeepWorkflowEditing.Focus(FocusState.Programmatic);
            return;
        }
        if (_page == Page.Result) { ShowPage(Page.Editor); FocusEntry(); }
        else if (_page == Page.Editor) { ShowPage(Page.List); WorkflowList.Focus(FocusState.Programmatic); }
        else ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void RunWorkflow()
    {
        if (_closing) return;
        if (_run is not null) { _run.Cancel(); return; }
        if (_page != Page.Editor || _opened is null || !_opened.IsEditable || _session is null) return;
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        _workflowRunFailure = null;
        var completion = _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WorkflowSource.IsReadOnly = true;
            ConfigureWorkflowButton.IsEnabled = false;
            WorkflowPrimaryButton.Content = Loc.T("Cancel run");
            WorkflowInputHint.Text = string.IsNullOrWhiteSpace(_opened.TargetActionPluginId) ? Loc.T("Processing with the saved provider and model…") : Loc.T("Processing and sending to the selected action…");
            var execution = await _session.RunWorkflowWithActionAsync(_session.WorkflowDefaults.Resolve(_opened.ToStored()), WorkflowSource.Text, cancellation.Token);
            var result = execution.Text;
            if (_closing) return;
            WorkflowResultText.Text = result;
            ShowPage(Page.Result);
            WorkflowResultStatus.Text = execution.Message ?? Loc.T("Completed. Review and copy the result.");
            if (execution.ActionSucceeded is { } actionSucceeded)
                WorkflowSummary.Text = actionSucceeded ? Loc.T("Action finished") : Loc.T("Action not confirmed");
            WorkflowResultScroll.ChangeView(null, 0, null, true);
            FocusEntry();
        }
        catch (OperationCanceledException) { RememberRunFailure(Loc.T("Run cancelled. Your source text is unchanged.")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { RememberRunFailure(Loc.T("Run failed. {0}", ex.Message)); }
        finally
        {
            try
            {
            _run = null;
            WorkflowSource.IsReadOnly = false;
            ConfigureWorkflowButton.IsEnabled = true;
            WorkflowPrimaryButton.Content = _page == Page.Result ? Loc.T("Copy result") : RunButtonLabel;
            WorkflowPrimaryButton.IsEnabled = _page == Page.Result || _opened is { IsEnabled: true } && (_opened.Template == WorkflowTemplate.Dictation || EffectiveAvailable(_opened.ProviderId, _opened.ModelId)) && !string.IsNullOrWhiteSpace(WorkflowSource.Text);
            }
            finally { completion.TrySetResult(); }
        }
    }

    private void WorkflowEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _run is not null || _opened is null || _store is null || _loadError is not null) return;
        try
        {
            RequireUnchangedApiWorkflow(_opened);
            var updated = WorkflowDraft.FromStored(Shortcuts is { } shortcuts
                ? shortcuts.SetEnabled(_opened.Id, !_opened.IsEnabled) : _store.SetEnabled(_opened.Id, !_opened.IsEnabled));
            var index = _workflows.FindIndex(item => item.Id == updated.Id);
            if (index >= 0) _workflows[index] = updated;
            _opened = updated;
            WorkflowInstruction.Text = updated.InstructionDescription;
            ShowPage(Page.Editor);
            WorkflowSummary.Text = updated.IsEnabled ? Loc.T("Workflow enabled") : Loc.T("Workflow disabled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { WorkflowInputHint.Text = Loc.T("Enablement was not changed. {0}", ex.Message); }
    }
    private void RememberRunFailure(string message)
    {
        _workflowRunFailure = (_opened!.Id, message);
        WorkflowInputHint.Text = message;
    }

    private void Source_Changed(object sender, TextChangedEventArgs e)
    {
        _workflowRunFailure = null;
        if (_opened is not null) _drafts[_opened.Id] = WorkflowSource.Text;
        if (WorkflowPrimaryButton is not null) UpdateSourceState();
    }

    private string RunButtonLabel => _session?.PluginRuntime.Actions.FirstOrDefault(a => a.PluginId == _opened?.TargetActionPluginId)?.Name ?? Loc.T("Run workflow");

    private void UpdateSourceState()
    {
        if (_page == Page.Configuration) { UpdateConfigurationState(); return; }
        WorkflowEnableButton.Content = _opened?.IsEnabled == true ? Loc.T("Disable workflow") : Loc.T("Enable workflow");
        WorkflowEnableButton.IsEnabled = !_closing && _run is null;
        if (_opened is { IsEditable: false })
        {
            WorkflowSource.IsReadOnly = true;
            WorkflowPrimaryButton.IsEnabled = false;
            ConfigureWorkflowButton.IsEnabled = false;
            WorkflowInputHint.Text = Loc.T("Unsupported workflow: editing and execution are unavailable. Enablement can change without changing other settings. An enabled matching App/Global rule with unsupported overrides sends dictation to review without pasting.");
            WorkflowExecutionSummary.Text = Loc.T("Stored settings are preserved. This detail is read-only.");
            SourceWatermark.Visibility = Visibility.Collapsed;
            return;
        }
        WorkflowSource.IsReadOnly = _run is not null;
        var empty = string.IsNullOrWhiteSpace(WorkflowSource.Text);
        if (_run is not null) return;
        UpdateExecutionSummary();
        if (_page == Page.Editor) WorkflowPrimaryButton.Content = RunButtonLabel;
        WorkflowPrimaryButton.IsEnabled = _page == Page.Result || !empty && _opened is { IsEnabled: true } && (_opened.Template == WorkflowTemplate.Dictation || EffectiveAvailable(_opened.ProviderId, _opened.ModelId));
        SourceWatermark.Visibility = WorkflowSource.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkflowInputHint.Text = _opened is { IsEnabled: false } ? Loc.T("This workflow is disabled. Enable it in Edit workflow to run it.")
            : _opened is null || !(_opened.Template == WorkflowTemplate.Dictation || EffectiveAvailable(_opened.ProviderId, _opened.ModelId))
            ? Loc.T("The saved provider or model is unavailable. Edit the workflow or configure the plugin.")
            : empty ? Loc.T("Paste or type the text to process.") : !string.IsNullOrEmpty(_opened.TargetActionPluginId) ? Loc.T("Run processes this text and sends the result to the saved plugin action.") : Loc.T("Run sends this text to the selected provider. Review the result before copying.");
        if (_workflowRunFailure is { } failure && failure.Id == _opened?.Id) WorkflowInputHint.Text = failure.Message;
    }

    private void Source_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Never use Enter or Backspace in the multiline editor for navigation.
        if (e.Key == global::Windows.System.VirtualKey.Enter
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            RunWorkflow();
            e.Handled = true;
        }
    }

    private void Workflow_Click(object sender, ItemClickEventArgs e) { WorkflowList.SelectedItem = e.ClickedItem; OpenSelected(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0) { NewWorkflow_Click(sender, e); return; }
        Filter(string.Empty); ClearSearchRequested?.Invoke(this, EventArgs.Empty);
    }
    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_page == Page.Configuration) { SaveConfiguration(); return; }
        if (_page == Page.Editor) { RunWorkflow(); return; }
        if (_page != Page.Result) return;
        try
        {
            var data = new DataPackage();
            data.SetText(WorkflowResultText.Text);
            Clipboard.SetContent(data);
            WorkflowSummary.Text = Loc.T("Result copied");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WorkflowSummary.Text = Loc.T("Clipboard unavailable");
        }
    }

    private IReadOnlyList<Choice> Models => _session?.LlmProviders.FirstOrDefault(p => p.SelectionId == ConfigProvider.SelectedId)?.Models
        .Select(m => new Choice(m.Id, m.DisplayName, m.Id)).ToArray() ?? [];
    private bool ConfigUsesRecordingTask => ConfigTrigger.SelectedId is "DictationHotkey" or "App" or "Website" or "Global";
    private string? ConfigSelectedTask => ConfigUsesRecordingTask
        ? (string.IsNullOrEmpty(ConfigTask.SelectedId) ? null : ConfigTask.SelectedId)
        : _opened is { } opened && ConfigTrigger.SelectedId == opened.ActivationId ? opened.SelectedTask : null;
    private bool ConfigurationDirty => _opened is not null && (ConfigName.Text != _opened.Title || ConfigInstruction.Text.ReplaceLineEndings("\n") != _opened.Instruction.ReplaceLineEndings("\n")
        || ConfigActionTarget.SelectedId != (_opened.TargetActionPluginId ?? "")
        || ConfigMemory.SelectedId != (_opened.MemoryPluginId ?? "")
        || _draftIcon != _opened.IconKind
        || ConfigTrigger.SelectedId != _opened.ActivationId || ConfigAppProcesses.Text != _opened.AppProcesses
        || DraftHotkeys != _opened.Hotkeys
        || ConfigSelectedTask != (string.IsNullOrWhiteSpace(_opened.SelectedTask) ? null : _opened.SelectedTask)
        || ConfigWebsiteDomains.Text != _opened.WebsiteDomains || ConfigContextMode.SelectedId != _opened.ContextMatchMode.ToString()
        || ConfigPriority.Text != _opened.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
        || ConfigTemplate.SelectedId != _opened.Template.ToString() || ConfigTranslationTarget.Text != (_opened.TranslationTarget ?? "")
        || ConfigProvider.SelectedId != _opened.ProviderId || ConfigModel.SelectedId != _opened.ModelId || ConfigEnabled.IsOn != _opened.IsEnabled);
    private string? ConfigurationError => _apiConfigurationConflict is { } apiConflict ? apiConflict
        : string.IsNullOrWhiteSpace(ConfigName.Text) ? Loc.T("Enter a workflow name.")
        : ConfigTrigger.SelectedId is "Hotkey" or "DictationHotkey" && ShortcutDraftError is { } shortcutError ? shortcutError
        : ConfigTrigger.SelectedId == "App" && string.IsNullOrWhiteSpace(ConfigAppProcesses.Text) ? Loc.T("Enter at least one Windows process name.")
        : ConfigTrigger.SelectedId is "App" or "Website" && !string.IsNullOrWhiteSpace(ConfigAppProcesses.Text)
            && ConfigAppProcesses.Text.Split(',').Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || value.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ? Loc.T("Enter process names such as notepad, chrome (without paths or .exe).")
        : ConfigTrigger.SelectedId == "Website" && string.IsNullOrWhiteSpace(ConfigWebsiteDomains.Text) ? Loc.T("Enter at least one website domain.")
        : ConfigTrigger.SelectedId is "App" or "Website" && !string.IsNullOrWhiteSpace(ConfigWebsiteDomains.Text)
            && ConfigWebsiteDomains.Text.Split(',').Any(value => BrowserWorkflowContext.NormalizePattern(value) is null) ? Loc.T("Enter comma-separated domains without paths, query strings or credentials.")
        : !int.TryParse(ConfigPriority.Text, out _) ? Loc.T("Enter a whole-number priority. Lower numbers win.")
        : !Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var template) || !Enum.IsDefined(template) ? Loc.T("Choose a workflow template.")
        : template == WorkflowTemplate.Custom && string.IsNullOrWhiteSpace(ConfigInstruction.Text) ? Loc.T("Add instructions for this custom workflow.")
        : template != WorkflowTemplate.Dictation && ConfigProvider.SelectedId is not "none" and not WorkflowLlmDefaults.Inherit && !Models.Any(model => model.Id == ConfigModel.SelectedId)
            && (_creating || ConfigProvider.SelectedId != _opened?.ProviderId || ConfigModel.SelectedId != _opened?.ModelId)
                ? Loc.T("Choose a model for this provider.") : null;

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        _creating = false;
        if (_page == Page.List) _opened = WorkflowList.SelectedItem as WorkflowDraft;
        if (_closing || _opened is null || !_opened.IsEditable) return;
        _configurationReturnPage = _page;
        LoadConfiguration();
    }

    private IEnumerable<EntryActionMenu.Action> WorkflowContextActions()
    {
        foreach (var action in EntryActionMenu.FromButtons(ContextActionsFooter)) yield return action;
        var workflow = _page == Page.List ? WorkflowList.SelectedItem as WorkflowDraft : _opened;
        if (_page is not (Page.List or Page.Editor) || workflow?.IsEditable != true) yield break;
        yield return new(Loc.T("Set shortcut for selected text…"), () => ConfigureShortcut("Hotkey"), !IsBusy);
        yield return new(Loc.T("Set shortcut for dictation…"), () => ConfigureShortcut("DictationHotkey"), !IsBusy);
    }

    private void ConfigureShortcut(string activation)
    {
        if (_closing || IsBusy) return;
        if (_page == Page.List) _opened = WorkflowList.SelectedItem as WorkflowDraft;
        if (_opened?.IsEditable != true) return;
        _configurationReturnPage = _page;
        LoadConfiguration(activation);
        UpdateConfigurationState();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_page != Page.Configuration) return;
            ConfigShortcutSection.StartBringIntoView();
            ConfigShortcutHost.Children.OfType<ShortcutRecorder>().FirstOrDefault()?.StartEditing();
        });
    }

    private string? _suggestedName;

    private void NewWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_page != Page.List) return;
        _selectionBeforeCreate = WorkflowList.SelectedItem as WorkflowDraft;
        _creating = true;
        _opened = new WorkflowDraft(Guid.NewGuid().ToString("N"), WorkflowTemplateCatalog.DefinitionFor(WorkflowTemplate.CleanedText).Name, Loc.T("Manual workflow"), "workflow", "") { Template = WorkflowTemplate.CleanedText };
        _configurationReturnPage = Page.List;
        LoadConfiguration();
    }

    private void LoadConfiguration(string? activation = null)
    {
        _apiConfigurationConflict = null;
        if (_opened is null) return;
        ConfigurationDiscardTitle.Text = _creating ? Loc.T("Discard this new workflow?") : Loc.T("Discard your changes?");
        ConfigurationDiscardDescription.Text = _creating ? Loc.T("This draft has not been created. Discarding it leaves your workflow list unchanged.")
            : Loc.T("The saved workflow will stay unchanged. Your source text will also be kept.");
        _loadingConfiguration = true;
        ConfigEnabled.IsOn = _opened.IsEnabled;
        ConfigureActionTargets(_opened.TargetActionPluginId ?? "");
        ConfigureMemorySources(_opened.MemoryPluginId ?? "");
        DeleteWorkflowButton.Visibility = _creating ? Visibility.Collapsed : Visibility.Visible;
        ConfigAdvanced.IsExpanded = false;
        _suggestedName = _creating ? WorkflowTemplateCatalog.DefinitionFor(_opened.Template).Name : null;
        ConfigName.Text = _opened.Title;
        SetDraftIcon(_opened.IconKind);
        ConfigTrigger.SetOptions([
            new("Manual", Loc.T("Manual"), Loc.T("Run explicitly with source text")),
            new("Hotkey", Loc.T("Shortcut \u00b7 selected text"), Loc.T("Send the selected text to this workflow and review the result")),
            new("DictationHotkey", Loc.T("Shortcut \u00b7 dictation"), Loc.T("Press to start dictation with this workflow; press again to stop")),
            new("App", Loc.T("App"), Loc.T("Apply to dictation in matching Windows processes")),
            new("Website", Loc.T("Website"), Loc.T("Apply to dictation on matching browser domains")),
            new("Global", Loc.T("Global fallback"), Loc.T("Apply when no app or website rule matches"))], activation ?? _opened.ActivationId);
        ConfigTask.SetOptions([
            new("", Loc.T("Use global setting"), Loc.T("Use the transcription task selected in Dictation")),
            new("transcribe", Loc.T("Transcribe"), Loc.T("Keep speech in its original language")),
            new("translate", Loc.T("Translate to English"), Loc.T("Use the transcription model's native English translation"))], string.IsNullOrWhiteSpace(_opened.SelectedTask) ? "" : _opened.SelectedTask);
        ConfigAppProcesses.Text = _opened.AppProcesses;
        _shortcutDraft["WorkflowSelectedTextHotkeys"] = _opened.Hotkeys;
        ConfigShortcutHost.Children.Clear();
        ConfigShortcutHost.Children.Add(new ShortcutRecorder("WorkflowSelectedTextHotkeys", Loc.T("Workflow"), "", _shortcutDraft,
            () => [], value =>
            {
                _shortcutDraft["WorkflowSelectedTextHotkeys"] = WorkflowShortcutCatalog.Canonical(value);
                DispatcherQueue.TryEnqueue(UpdateConfigurationState);
                return null;
            }));
        ConfigWebsiteDomains.Text = _opened.WebsiteDomains;
        ConfigContextMode.SetOptions([new("All", Loc.T("Match all"), Loc.T("App AND website")), new("Any", Loc.T("Match any"), Loc.T("App OR website"))], _opened.ContextMatchMode.ToString());
        ConfigPriority.Text = _opened.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ConfigTemplate.SetOptions(WorkflowTemplateCatalog.All.Select(definition => new Choice(
            definition.Template.ToString(), Loc.T(definition.Name), Loc.T(definition.Description))).ToArray(), _opened.Template.ToString());
        ConfigTranslationTarget.Text = _opened.TranslationTarget ?? "";
        ConfigInstruction.Text = _opened.Instruction.ReplaceLineEndings("\r");
        ConfigProvider.SetOptions(Providers, _opened.ProviderId, Loc.T("{0} (unavailable)", _opened.ProviderId));
        ConfigureModels(_opened.ModelId);
        _loadingConfiguration = false;
        ShowPage(Page.Configuration);
        ConfigurationScroll.ChangeView(null, 0, null, true);
        FocusEntry();
    }

    private void ConfigureModels(string modelId)
    {
        if (ConfigProvider.SelectedId == WorkflowLlmDefaults.Inherit)
        {
            var defaults = ReadDefaults();
            ConfigModel.SetOptions([new("", defaults?.Model ?? Loc.T("No default configured"), Loc.T("Inherited from Default LLM"))], "");
            ConfigModel.IsEnabled = false;
            return;
        }
        ConfigModel.IsEnabled = ConfigProvider.SelectedId != "none";
        ConfigModel.SetOptions(Models, modelId, ConfigModel.IsEnabled ? (modelId.Length > 0 ? Loc.T("{0} (unavailable)", modelId) : Loc.T("Choose a model")) : Loc.T("No model selected"));
    }

    private void Configuration_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingConfiguration && _page == Page.Configuration) UpdateConfigurationState();
    }

    private void ConfigEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loadingConfiguration && _page == Page.Configuration) UpdateConfigurationState();
    }

    private void UpdateConfigurationState()
    {
        if (_loadingConfiguration) return;
        SettingsHelp.Update(_shortcutHelp, ConfigTrigger.SelectedId == "DictationHotkey"
            ? Loc.T("Focus a text field in another app. Press this shortcut to start recording and press again to stop. The transcript is processed by this workflow using your dictation paste and History settings.")
            : Loc.T("Select text in another app, then press this shortcut to send it to the configured provider. The result opens for review. Nothing is pasted or saved to History."));
        var contextual = ConfigTrigger.SelectedId is "App" or "Website";
        ConfigTaskSection.Visibility = ConfigUsesRecordingTask ? Visibility.Visible : Visibility.Collapsed;
        ConfigTaskHint.Text = Loc.T("Applies only to this recording. Native translation outputs English and requires a compatible transcription model. With Dictation Only, no LLM is needed; local models work offline.")
            + (ConfigTask.SelectedId == "translate" && _session?.SupportsTranslation != true
                ? " " + Loc.T("The current model cannot translate to English. Choose a compatible model in Dictation before running this workflow.") : "");
        ConfigShortcutSection.Visibility = ConfigTrigger.SelectedId is "Hotkey" or "DictationHotkey" ? Visibility.Visible : Visibility.Collapsed;
        ConfigAppSection.Visibility = ConfigWebsiteSection.Visibility = contextual ? Visibility.Visible : Visibility.Collapsed;
        ConfigContextSection.Visibility = contextual && !string.IsNullOrWhiteSpace(ConfigAppProcesses.Text) && !string.IsNullOrWhiteSpace(ConfigWebsiteDomains.Text) ? Visibility.Visible : Visibility.Collapsed;
        var template = Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var selected) ? selected : WorkflowTemplate.Custom;
        ConfigMemorySection.Visibility = ConfigInstructionSection.Visibility = ConfigProviderSection.Visibility = ConfigModelSection.Visibility = template == WorkflowTemplate.Dictation ? Visibility.Collapsed : Visibility.Visible;
        if (template == WorkflowTemplate.Custom) ConfigAdvanced.IsExpanded = true;
        ConfigTranslationSection.Visibility = template == WorkflowTemplate.Translation ? Visibility.Visible : Visibility.Collapsed;
        ConfigInstructionLabel.Text = template == WorkflowTemplate.Custom ? Loc.T("INSTRUCTIONS (REQUIRED)") : Loc.T("FINE-TUNING (OPTIONAL)");
        SettingsHelp.Update(_templateHelp, Loc.T(WorkflowTemplateCatalog.DefinitionFor(template).Description));
        ConfigActionHint.Text = string.IsNullOrEmpty(ConfigActionTarget.SelectedId)
            ? Loc.T("Use the normal text output. Choose a plugin action to send the finished result there instead.")
            : _session?.PluginRuntime.Actions.Any(a => a.PluginId == ConfigActionTarget.SelectedId) == true
                ? Loc.T("The finished text is sent to this action instead of being pasted. Running this workflow can create an item in the selected service.")
                : Loc.T("This action is unavailable. Its saved selection is preserved; enable the plugin or choose another target.");
        var error = ConfigurationError;
        ConfigurationValidation.Text = error ?? (!ConfigEnabled.IsOn ? Loc.T("Save as disabled. Enable this workflow before running it.")
            : !string.IsNullOrEmpty(ConfigActionTarget.SelectedId) ? Loc.T("Runs the selected plugin action after processing. The result is sent there instead of being pasted.")
            : template == WorkflowTemplate.Dictation ? Loc.T("No LLM processing. Dictation uses your selected transcription model.")
            : EffectiveConfigurationError(ConfigProvider.SelectedId, ConfigModel.SelectedId) is { } providerError
                ? providerError + " " + Loc.T("You can save now and complete the setup later.")
                : (ConfigTrigger.SelectedId == "DictationHotkey" ? Loc.T("Press once to start and again to stop. Applies only to this recording and uses your dictation paste and history settings.")
                    : ConfigTrigger.SelectedId == "Hotkey" ? Loc.T("The shortcut processes selected text with this provider. Results open for review.")
                    : ConfigTrigger.SelectedId == "Manual" ? Loc.T("Saved on this device. Run manually and review before copying.") : Loc.T("Applies automatically to matching dictations. Uses your dictation paste and history settings.")));
        ConfigurationValidation.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            error is null && (!ConfigEnabled.IsOn || template == WorkflowTemplate.Dictation || EffectiveAvailable(ConfigProvider.SelectedId, ConfigModel.SelectedId)) ? "MutedBrush" : "AccentBrush"];
        WorkflowSummary.Text = ConfigurationDirty ? Loc.T("Unsaved changes") : Loc.T("Workflow configuration");
        WorkflowPrimaryButton.IsEnabled = error is null && (_creating || ConfigurationDirty) && ConfigurationDiscardPrompt.Visibility != Visibility.Visible;
    }

    private void SaveConfiguration()
    {
        if (_closing || _opened is null || ConfigurationError is not null || (!_creating && !ConfigurationDirty) || ConfigurationDiscardPrompt.Visibility == Visibility.Visible) return;
        var updated = _opened with { IconKind = _draftIcon, Title = ConfigName.Text.Trim(), Instruction = ConfigInstruction.Text.Trim().ReplaceLineEndings("\n"),
            TriggerKind = ConfigTrigger.SelectedId == "DictationHotkey" ? WorkflowTriggerKind.Hotkey : Enum.Parse<WorkflowTriggerKind>(ConfigTrigger.SelectedId),
            HotkeyBehavior = ConfigTrigger.SelectedId == "DictationHotkey" ? WorkflowHotkeyBehavior.StartDictation : WorkflowHotkeyBehavior.ProcessSelectedText, AppProcesses = ConfigAppProcesses.Text.Trim(),
            Hotkeys = WorkflowShortcutCatalog.Canonical(DraftHotkeys),
            WebsiteDomains = ConfigWebsiteDomains.Text.Trim(), ContextMatchMode = Enum.Parse<WorkflowContextMatchMode>(ConfigContextMode.SelectedId),
            Priority = int.Parse(ConfigPriority.Text),
            Template = Enum.Parse<WorkflowTemplate>(ConfigTemplate.SelectedId),
            TranslationTarget = string.IsNullOrWhiteSpace(ConfigTranslationTarget.Text) ? null : ConfigTranslationTarget.Text.Trim(),
            SelectedTask = ConfigSelectedTask,
            TargetActionPluginId = string.IsNullOrEmpty(ConfigActionTarget.SelectedId) ? null : ConfigActionTarget.SelectedId,
            MemoryPluginId = string.IsNullOrEmpty(ConfigMemory.SelectedId) ? null : ConfigMemory.SelectedId,
            ProviderId = ConfigProvider.SelectedId, ModelId = ConfigModel.SelectedId,
            IsEnabled = ConfigEnabled.IsOn, Description = ConfigEnabled.IsOn ? Loc.T("Manual workflow") : Loc.T("Disabled manual workflow") };
        try
        {
            if (_store is null || _loadError is not null) throw new InvalidOperationException(Loc.T("Workflow storage is unavailable."));
            RequireUnchangedApiWorkflow(_opened);
            var stored = updated.ToStored();
            if (Shortcuts is { } shortcuts) shortcuts.Save(stored);
            else if (stored.Trigger.Kind == WorkflowTriggerKind.Hotkey) throw new InvalidOperationException(Loc.T("Wait for workflow shortcuts to initialize."));
            else _store.Save(stored, allowAutomatic: true);
            updated = WorkflowDraft.FromStored(stored);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConfigurationValidation.Text = Loc.T("Changes were not saved. {0}", ex.Message);
            return;
        }
        var created = _creating;
        _afterConfigurationExit = null;
        if (created) _workflows.Add(updated);
        else _workflows[_workflows.FindIndex(workflow => workflow.Id == updated.Id)] = updated;
        _creating = false;
        _opened = updated;
        WorkflowInstruction.Text = updated.InstructionDescription;
        if (created) _query = string.Empty;
        LeaveConfiguration();
        if (created)
        {
            ClearSearchRequested?.Invoke(this, EventArgs.Empty);
            WorkflowList.ScrollIntoView(WorkflowList.SelectedItem);
        }
        else WorkflowSummary.Text = Loc.T("Workflow saved");
        ConfigurationSaved?.Invoke(updated.Id);
    }

    private async void DeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _deleteCompletion is { Task.IsCompleted: false } || _creating || _opened is null || _store is null) return;
        var completion = _deleteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = _opened;
        DeleteWorkflowButton.IsEnabled = false;
        try
        {
            var dialog = _deleteDialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = Loc.T("Delete this workflow?"),
                Content = Loc.T("Delete \"{0}\" and its saved instructions? This cannot be undone. Unsaved edits and this workflow's source-text draft will also be discarded.", workflow.Title),
                PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing) return;
            RequireUnchangedApiWorkflow(workflow);
            if (Shortcuts is { } shortcuts) shortcuts.Delete(workflow.Id);
            else _store.Delete(workflow.Id, allowAutomatic: true);
            _workflows.RemoveAll(w => w.Id == workflow.Id);
            _drafts.Remove(workflow.Id);
            _opened = null;
            _afterConfigurationExit = null;
            _configurationReturnPage = Page.List;
            LeaveConfiguration();
            WorkflowSummary.Text = Loc.T("Workflow deleted");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConfigurationValidation.Text = Loc.T("The workflow was not deleted. {0}", ex.Message);
        }
        finally
        {
            _deleteDialog = null;
            try { DeleteWorkflowButton.IsEnabled = !_closing; }
            finally { completion.TrySetResult(); }
        }
    }

    private void LeaveConfiguration()
    {
        DismissDiscard();
        if (_creating)
        {
            _opened = _selectionBeforeCreate;
            _creating = false;
        }
        ShowPage(_configurationReturnPage);
        if (_configurationReturnPage == Page.List)
        {
            Filter(_query);
            WorkflowList.SelectedItem = FilteredWorkflows.FirstOrDefault(item => item.Id == _opened?.Id) ?? FilteredWorkflows.FirstOrDefault();
            WorkflowList.Focus(FocusState.Programmatic);
        }
        else FocusEntry();
        var navigate = _afterConfigurationExit;
        _afterConfigurationExit = null;
        navigate?.Invoke();
    }

    private void DismissDiscard()
    {
        ConfigurationDiscardPrompt.Visibility = Visibility.Collapsed;
        ConfigurationScroll.IsEnabled = true;
        WorkflowConfigurationPage.Opacity = 1;
        UpdateConfigurationState();
    }
    private void KeepEditing_Click(object sender, RoutedEventArgs e) { _afterConfigurationExit = null; DismissDiscard(); FocusEntry(); }
    private void DiscardConfiguration_Click(object sender, RoutedEventArgs e) => LeaveConfiguration();
    private void NavigateToList()
    {
        if (_closing) return;
        if (_run is not null) { _run.Cancel(); return; }
        if (_page == Page.Configuration)
        {
            _afterConfigurationExit = NavigateToList;
            GoBack();
            return;
        }
        ShowPage(Page.List);
        Filter(string.Empty);
        ClearSearchRequested?.Invoke(this, EventArgs.Empty);
        WorkflowList.Focus(FocusState.Programmatic);
    }

    private void UpdateBreadcrumbs()
    {
        var crumbs = new List<Crumb>();
        if (_page == Page.List) crumbs.Add(new(Loc.T("Workflows")));
        else
        {
            var directParent = _page == Page.Editor || _page == Page.Configuration && _configurationReturnPage == Page.List;
            crumbs.Add(new(Loc.T("Workflows"), NavigateToList, directParent ? Loc.T("Back from workflows") : Loc.T("Workflow breadcrumb Workflows")));
            if (_page == Page.Editor) crumbs.Add(new(_opened?.Title ?? Loc.T("Source text")));
            else if (_page == Page.Result)
            {
                crumbs.Add(new(Loc.T("Source text"), GoBack, Loc.T("Back from workflows")));
                crumbs.Add(new(Loc.T("Result")));
            }
            else
            {
                if (_configurationReturnPage == Page.Editor) crumbs.Add(new(Loc.T("Source text"), GoBack, Loc.T("Back from workflows")));
                crumbs.Add(new(_creating ? Loc.T("New workflow") : Loc.T("Edit")));
            }
        }
        WorkflowBreadcrumbs.SetItems(crumbs.ToArray());
    }
    private void Configuration_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_page != Page.Configuration) return;
        if (e.Key == global::Windows.System.VirtualKey.S
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            SaveConfiguration();
            e.Handled = true;
        }
    }
}
