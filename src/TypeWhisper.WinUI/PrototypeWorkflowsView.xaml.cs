using System.Collections.ObjectModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeWorkflowsView : UserControl
{
    private enum Page { List, Editor, Result, Configuration }
    private Page _page;
    private PrototypeWorkflow? _opened;
    private string _query = string.Empty;
    private readonly Dictionary<string, string> _drafts = [];
    private readonly List<PrototypeWorkflow> _workflows = [];
    private Page _configurationReturnPage;
    private bool _loadingConfiguration;
    private bool _creating;
    private PrototypeWorkflow? _selectionBeforeCreate;
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
        try { _deleteDialog?.Hide(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Workflow dialog close failed: " + ex); }
        return Task.WhenAll(_runCompletion?.Task ?? Task.CompletedTask, _deleteCompletion?.Task ?? Task.CompletedTask);
    }

    private ManualWorkflowStore? _store;
    private string? _loadError;
    private CancellationTokenSource? _run;
    private IReadOnlyList<PrototypeChoice> Providers => [new("none", "Not configured", "Choose an installed LLM provider"),
        .. (_session?.LlmProviders.Select(p => new PrototypeChoice(p.SelectionId, p.Name, p.Ready ? "Ready" : "Requires configuration")) ?? [])];
    private static readonly PrototypeChoice[] Outputs = [new("preview", "Review result", "Copy the result when ready")];

    internal void Connect(LocalDictationSession session)
    {
        if (_session is not null) _session.Changed -= RuntimeChanged;
        _session = session;
        _session.Changed += RuntimeChanged;
        _store = new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json"));
        try
        {
            _workflows.Clear();
            _workflows.AddRange(_store.Read().Where(ManualWorkflowStore.IsEditable)
                .Select(PrototypeWorkflow.FromStored));
            _loadError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _loadError = "Workflows could not be loaded. Existing data has not been changed."; }
        Filter("");
    }

    private void RuntimeChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_page == Page.Configuration)
        {
            ConfigProvider.SetOptions(Providers, ConfigProvider.SelectedId, ConfigProvider.SelectedId + " (unavailable)");
            ConfigureModels(ConfigModel.SelectedId);
            UpdateConfigurationState();
        }
        else UpdateSourceState();
    });

    private bool Available(string provider, string model) => _session?.LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model)) == true;
    internal ObservableCollection<PrototypeWorkflow> FilteredWorkflows { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? ConfigurationModeChanged;
    internal bool IsDetail => _page != Page.List;
    internal bool IsConfiguring => _page == Page.Configuration;

    public PrototypeWorkflowsView()
    {
        InitializeComponent();
        ConfigTrigger.Configure("Activation", "workflow", "Workflow activation");
        ConfigTrigger.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTemplate.Configure("Template", "workflow", "Workflow template");
        ConfigTemplate.SelectionChanged += _ => UpdateConfigurationState();
        ConfigProvider.Configure("Provider", "plugin", "Workflow provider");
        ConfigModel.Configure("Model", "chip", "Workflow model");
        ConfigOutput.Configure("Output destination", "run", "Workflow output");
        ConfigProvider.SelectionChanged += _ =>
        {
            ConfigureModels(string.Empty);
            UpdateConfigurationState();
        };
        ConfigModel.SelectionChanged += _ => UpdateConfigurationState();
        ConfigOutput.SelectionChanged += _ => UpdateConfigurationState();
        Unloaded += (_, _) => _run?.Cancel();
        Filter(string.Empty);
    }

    internal void Filter(string query)
    {
        if (_run is not null) { if (query != _query) _run.Cancel(); return; }
        if (_page == Page.Configuration) return;
        if (query == _query && IsDetail) return;
        _query = query;
        var selected = WorkflowList.SelectedItem as PrototypeWorkflow;
        FilteredWorkflows.Clear();
        foreach (var item in _workflows.Where(item => query.Length == 0
            || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase))) FilteredWorkflows.Add(item);
        WorkflowList.SelectedItem = FilteredWorkflows.FirstOrDefault(item => item.Id == selected?.Id) ?? FilteredWorkflows.FirstOrDefault();
        ShowPage(Page.List);
        WorkflowEmptyState.Visibility = FilteredWorkflows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void MoveSelection(int offset)
    {
        if (IsDetail || FilteredWorkflows.Count == 0) return;
        WorkflowList.SelectedIndex = Math.Clamp(WorkflowList.SelectedIndex + offset, 0, FilteredWorkflows.Count - 1);
        WorkflowList.ScrollIntoView(WorkflowList.SelectedItem);
    }

    internal void OpenSelected()
    {
        if (IsDetail || WorkflowList.SelectedItem is not PrototypeWorkflow workflow) return;
        _opened = workflow;
        WorkflowInstruction.Text = workflow.InstructionDescription;
        WorkflowSource.Text = (_drafts.GetValueOrDefault(workflow.Id) ?? "").ReplaceLineEndings("\r");
        ShowPage(Page.Editor);
        FocusEntry();
    }

    internal void FocusEntry()
    {
        if (_page == Page.Editor) WorkflowSource.Focus(FocusState.Programmatic);
        else if (_page == Page.Configuration)
        {
            if (ConfigurationDiscardPrompt.Visibility == Visibility.Visible) KeepWorkflowEditing.Focus(FocusState.Programmatic);
            else if (!ConfigTrigger.IsPopupOpen && !ConfigTemplate.IsPopupOpen && !ConfigProvider.IsPopupOpen && !ConfigModel.IsPopupOpen && !ConfigOutput.IsPopupOpen) ConfigName.Focus(FocusState.Programmatic);
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
        WorkflowPageTitle.Text = page == Page.Configuration ? (_creating ? "New workflow" : "Edit workflow") : page == Page.List ? "Workflows" : _opened?.Title ?? "Workflow";
        WorkflowSummary.Text = page == Page.List
            ? $"{FilteredWorkflows.Count} workflow{(FilteredWorkflows.Count == 1 ? "" : "s")}" : "Workflow";
        UpdateBreadcrumbs();
        WorkflowNavigationHint.Text = page switch { Page.Configuration => "Esc Cancel   Ctrl S Save", Page.Editor => "Esc Back   Ctrl Enter Run", Page.Result => "⌫ / Esc Back", _ => "⌫ / Esc Back   ↑↓ Navigate   Enter Open" };
        WorkflowPrimaryButton.Visibility = page == Page.List ? Visibility.Collapsed : Visibility.Visible;
        WorkflowPrimaryButton.Content = page == Page.Configuration ? (_creating ? "Create workflow" : "Save changes") : page == Page.Result ? "Copy result" : "Run workflow";
        WorkflowExecutionSummary.Text = _opened is null || _opened.ProviderId == "none"
            ? "Choose a provider and model in Edit workflow."
            : $"{Providers.FirstOrDefault(item => item.Id == _opened.ProviderId)?.Label ?? _opened.ProviderId} · {_opened.ModelId} · input is sent to this provider when you run";
        if (_loadError is not null) WorkflowSummary.Text = _loadError;
        ConfigureWorkflowButton.Visibility = page is Page.List or Page.Editor ? Visibility.Visible : Visibility.Collapsed;
        NewWorkflowButton.IsEnabled = _loadError is null && _store is not null;
        NewWorkflowButton.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        ConfigureWorkflowButton.IsEnabled = page != Page.List || FilteredWorkflows.Count > 0;
        ConfigurationModeChanged?.Invoke(page == Page.Configuration);
        UpdateSourceState();
    }

    internal void GoBack()
    {
        if (_closing) return;
        if (_run is not null) { _run.Cancel(); return; }
        if (_page == Page.Configuration)
        {
            foreach (var picker in new[] { ConfigTrigger, ConfigTemplate, ConfigProvider, ConfigModel, ConfigOutput })
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
        if (_page != Page.Editor || _opened is null || _session is null) return;
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        var completion = _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WorkflowSource.IsReadOnly = true;
            ConfigureWorkflowButton.IsEnabled = false;
            WorkflowPrimaryButton.Content = "Cancel run";
            WorkflowInputHint.Text = "Processing with the saved provider and model…";
            var result = await ManualWorkflowRunner.RunAsync(_opened.ToStored(), WorkflowSource.Text,
                Available, _session.ProcessLlmAsync, cancellation.Token);
            if (_closing) return;
            WorkflowResultText.Text = result;
            ShowPage(Page.Result);
            WorkflowResultScroll.ChangeView(null, 0, null, true);
            FocusEntry();
        }
        catch (OperationCanceledException) { WorkflowInputHint.Text = "Run cancelled. Your source text is unchanged."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { WorkflowInputHint.Text = "Run failed. " + ex.Message; }
        finally
        {
            try
            {
            _run = null;
            WorkflowSource.IsReadOnly = false;
            ConfigureWorkflowButton.IsEnabled = true;
            WorkflowPrimaryButton.Content = _page == Page.Result ? "Copy result" : "Run workflow";
            WorkflowPrimaryButton.IsEnabled = _page == Page.Result || _opened is { IsEnabled: true } && Available(_opened.ProviderId, _opened.ModelId) && !string.IsNullOrWhiteSpace(WorkflowSource.Text);
            }
            finally { completion.TrySetResult(); }
        }
    }

    private void Source_Changed(object sender, TextChangedEventArgs e)
    {
        if (_opened is not null) _drafts[_opened.Id] = WorkflowSource.Text;
        if (WorkflowPrimaryButton is not null) UpdateSourceState();
    }

    private void UpdateSourceState()
    {
        if (_page == Page.Configuration) { UpdateConfigurationState(); return; }
        var empty = string.IsNullOrWhiteSpace(WorkflowSource.Text);
        if (_run is not null) return;
        WorkflowPrimaryButton.IsEnabled = _page == Page.Result || !empty && _opened is { IsEnabled: true } && Available(_opened.ProviderId, _opened.ModelId);
        SourceWatermark.Visibility = WorkflowSource.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkflowInputHint.Text = _opened is { IsEnabled: false } ? "This workflow is disabled. Enable it in Edit workflow to run it."
            : _opened is null || !Available(_opened.ProviderId, _opened.ModelId)
            ? "The saved provider or model is unavailable. Edit the workflow or configure the plugin."
            : empty ? "Paste or type the text to process." : "Run sends this text to the selected provider. Review the result before copying.";
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
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { Filter(string.Empty); ClearSearchRequested?.Invoke(this, EventArgs.Empty); }
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
            WorkflowSummary.Text = "Result copied";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WorkflowSummary.Text = "Clipboard unavailable";
        }
    }

    private IReadOnlyList<PrototypeChoice> Models => _session?.LlmProviders.FirstOrDefault(p => p.SelectionId == ConfigProvider.SelectedId)?.Models
        .Select(m => new PrototypeChoice(m.Id, m.DisplayName, m.Id)).ToArray() ?? [];
    private bool ConfigurationDirty => _opened is not null && (ConfigName.Text != _opened.Title || ConfigInstruction.Text.ReplaceLineEndings("\n") != _opened.Instruction.ReplaceLineEndings("\n")
        || ConfigTrigger.SelectedId != _opened.TriggerKind.ToString() || ConfigAppProcesses.Text != _opened.AppProcesses
        || ConfigPriority.Text != _opened.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
        || ConfigTemplate.SelectedId != _opened.Template.ToString() || ConfigTranslationTarget.Text != (_opened.TranslationTarget ?? "")
        || ConfigProvider.SelectedId != _opened.ProviderId || ConfigModel.SelectedId != _opened.ModelId || ConfigOutput.SelectedId != _opened.OutputTarget || ConfigEnabled.IsOn != _opened.IsEnabled);
    private string? ConfigurationError => string.IsNullOrWhiteSpace(ConfigName.Text) ? "Enter a workflow name."
        : ConfigTrigger.SelectedId == "App" && (string.IsNullOrWhiteSpace(ConfigAppProcesses.Text)
            || ConfigAppProcesses.Text.Split(',').Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || value.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) ? "Enter process names such as notepad, chrome (without paths or .exe)."
        : !int.TryParse(ConfigPriority.Text, out _) ? "Enter a whole-number priority. Lower numbers win."
        : !Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var template) || !Enum.IsDefined(template) ? "Choose a workflow template."
        : template == WorkflowTemplate.Custom && string.IsNullOrWhiteSpace(ConfigInstruction.Text) ? "Add instructions for this custom workflow."
        : ConfigProvider.SelectedId != "none" && !Models.Any(model => model.Id == ConfigModel.SelectedId)
            && (_creating || ConfigProvider.SelectedId != _opened?.ProviderId || ConfigModel.SelectedId != _opened?.ModelId)
                ? "Choose a model for this provider." : null;

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        _creating = false;
        if (_page == Page.List) _opened = WorkflowList.SelectedItem as PrototypeWorkflow;
        if (_opened is null) return;
        _configurationReturnPage = _page;
        LoadConfiguration();
    }

    private void NewWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_page != Page.List) return;
        _selectionBeforeCreate = WorkflowList.SelectedItem as PrototypeWorkflow;
        _creating = true;
        _opened = new PrototypeWorkflow(Guid.NewGuid().ToString("N"), "", "Manual workflow", "workflow", "");
        _configurationReturnPage = Page.List;
        LoadConfiguration();
    }

    private void LoadConfiguration()
    {
        if (_opened is null) return;
        ConfigurationDiscardTitle.Text = _creating ? "Discard this new workflow?" : "Discard your changes?";
        ConfigurationDiscardDescription.Text = _creating ? "This draft has not been created. Discarding it leaves your workflow list unchanged."
            : "The saved workflow will stay unchanged. Your source text will also be kept.";
        _loadingConfiguration = true;
        ConfigEnabled.IsOn = _opened.IsEnabled;
        DeleteWorkflowButton.Visibility = _creating ? Visibility.Collapsed : Visibility.Visible;
        ConfigName.Text = _opened.Title;
        ConfigTrigger.SetOptions([
            new("Manual", "Manual", "Run explicitly with source text"),
            new("App", "App", "Apply to dictation in matching Windows processes"),
            new("Global", "Global fallback", "Apply when no app rule matches")], _opened.TriggerKind.ToString());
        ConfigAppProcesses.Text = _opened.AppProcesses;
        ConfigPriority.Text = _opened.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ConfigTemplate.SetOptions(WorkflowTemplateCatalog.All.Select(definition => new PrototypeChoice(
            definition.Template.ToString(), definition.Name, definition.Description)).ToArray(), _opened.Template.ToString());
        ConfigTranslationTarget.Text = _opened.TranslationTarget ?? "";
        ConfigInstruction.Text = _opened.Instruction.ReplaceLineEndings("\r");
        ConfigProvider.SetOptions(Providers, _opened.ProviderId, _opened.ProviderId + " (unavailable)");
        ConfigureModels(_opened.ModelId);
        ConfigOutput.SetOptions(Outputs, _opened.OutputTarget);
        _loadingConfiguration = false;
        ShowPage(Page.Configuration);
        ConfigurationScroll.ChangeView(null, 0, null, true);
        FocusEntry();
    }

    private void ConfigureModels(string modelId)
    {
        ConfigModel.IsEnabled = ConfigProvider.SelectedId != "none";
        ConfigModel.SetOptions(Models, modelId, ConfigModel.IsEnabled ? (modelId.Length > 0 ? modelId + " (unavailable)" : "Choose a model") : "No model selected");
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
        ConfigAppSection.Visibility = ConfigTrigger.SelectedId == "App" ? Visibility.Visible : Visibility.Collapsed;
        ConfigOutputSection.Visibility = ConfigTrigger.SelectedId == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        var template = Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var selected) ? selected : WorkflowTemplate.Custom;
        ConfigTranslationSection.Visibility = template == WorkflowTemplate.Translation ? Visibility.Visible : Visibility.Collapsed;
        ConfigInstructionLabel.Text = template == WorkflowTemplate.Custom ? "INSTRUCTIONS (REQUIRED)" : "FINE-TUNING (OPTIONAL)";
        ConfigTemplateDescription.Text = WorkflowTemplateCatalog.DefinitionFor(template).Description;
        var error = ConfigurationError;
        ConfigurationValidation.Text = error ?? (!ConfigEnabled.IsOn ? "Save as disabled. Enable this workflow before running it."
            : !Available(ConfigProvider.SelectedId, ConfigModel.SelectedId)
                ? "You can save this workflow now. Configure the selected plugin before running it."
                : (ConfigTrigger.SelectedId == "Manual" ? "Saved on this device. Run manually and review before copying." : "Applies automatically to matching dictations. Uses your dictation paste and history settings."));
        ConfigurationValidation.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[error is null ? "MutedBrush" : "AccentBrush"];
        WorkflowSummary.Text = ConfigurationDirty ? "Unsaved changes" : "Workflow configuration";
        WorkflowPrimaryButton.IsEnabled = error is null && ConfigurationDirty && ConfigurationDiscardPrompt.Visibility != Visibility.Visible;
    }

    private void SaveConfiguration()
    {
        if (_closing || _opened is null || ConfigurationError is not null || !ConfigurationDirty || ConfigurationDiscardPrompt.Visibility == Visibility.Visible) return;
        var updated = _opened with { Title = ConfigName.Text.Trim(), Instruction = ConfigInstruction.Text.Trim().ReplaceLineEndings("\n"),
            TriggerKind = Enum.Parse<WorkflowTriggerKind>(ConfigTrigger.SelectedId), AppProcesses = ConfigAppProcesses.Text.Trim(),
            Priority = int.Parse(ConfigPriority.Text),
            Template = Enum.Parse<WorkflowTemplate>(ConfigTemplate.SelectedId),
            TranslationTarget = string.IsNullOrWhiteSpace(ConfigTranslationTarget.Text) ? null : ConfigTranslationTarget.Text.Trim(),
            ProviderId = ConfigProvider.SelectedId, ModelId = ConfigModel.SelectedId, OutputTarget = ConfigOutput.SelectedId,
            IsEnabled = ConfigEnabled.IsOn, Description = ConfigEnabled.IsOn ? "Manual workflow" : "Disabled manual workflow" };
        try
        {
            if (_store is null || _loadError is not null) throw new InvalidOperationException("Workflow storage is unavailable.");
            var stored = updated.ToStored();
            _store.Save(stored, allowAutomatic: true);
            updated = PrototypeWorkflow.FromStored(stored);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConfigurationValidation.Text = "Changes were not saved. " + ex.Message;
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
        else WorkflowSummary.Text = "Workflow saved";
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
                XamlRoot = XamlRoot, Title = "Delete this workflow?",
                Content = $"Delete \"{workflow.Title}\" and its saved instructions? This cannot be undone. Unsaved edits and this workflow's source-text draft will also be discarded.",
                PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing) return;
            _store.Delete(workflow.Id, allowAutomatic: true);
            _workflows.RemoveAll(w => w.Id == workflow.Id);
            _drafts.Remove(workflow.Id);
            _opened = null;
            _afterConfigurationExit = null;
            _configurationReturnPage = Page.List;
            LeaveConfiguration();
            WorkflowSummary.Text = "Workflow deleted";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConfigurationValidation.Text = "The workflow was not deleted. " + ex.Message;
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
    private void NavigateToAncestor(bool launcher)
    {
        if (_closing) return;
        if (_run is not null) { _run.Cancel(); return; }
        if (_page == Page.Configuration)
        {
            _afterConfigurationExit = () => NavigateToAncestor(launcher);
            GoBack();
            return;
        }
        ShowPage(Page.List);
        if (launcher) LauncherRequested?.Invoke(this, EventArgs.Empty);
        else
        {
            Filter(string.Empty);
            ClearSearchRequested?.Invoke(this, EventArgs.Empty);
            WorkflowList.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateBreadcrumbs()
    {
        var crumbs = new List<PrototypeCrumb>
        {
            new("Quick Launch", () => NavigateToAncestor(true), _page == Page.List ? "Back from workflows" : "Workflow breadcrumb Quick Launch")
        };
        if (_page == Page.List) crumbs.Add(new("Workflows"));
        else
        {
            var directParent = _page == Page.Editor || _page == Page.Configuration && _configurationReturnPage == Page.List;
            crumbs.Add(new("Workflows", () => NavigateToAncestor(false), directParent ? "Back from workflows" : "Workflow breadcrumb Workflows"));
            if (_page == Page.Editor) crumbs.Add(new(_opened?.Title ?? "Source text"));
            else if (_page == Page.Result)
            {
                crumbs.Add(new("Source text", GoBack, "Back from workflows"));
                crumbs.Add(new("Result"));
            }
            else
            {
                if (_configurationReturnPage == Page.Editor) crumbs.Add(new("Source text", GoBack, "Back from workflows"));
                crumbs.Add(new(_creating ? "New workflow" : "Edit"));
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
