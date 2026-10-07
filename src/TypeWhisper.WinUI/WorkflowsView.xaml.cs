using System.Collections.ObjectModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

// The list of workflows and the page that edits one of them. A test run opens in a dialog.
public sealed partial class WorkflowsView : UserControl
{
    private enum Page { List, Configuration }
    private Page _page;
    private WorkflowDraft? _opened;
    private (string Id, string Message)? _workflowRunFailure;
    private string _query = string.Empty;
    private readonly Dictionary<string, string> _drafts = [];
    private readonly List<WorkflowDraft> _workflows = [];
    private bool _loadingConfiguration;
    private bool _creating;
    private WorkflowDraft? _selectionBeforeCreate;
    private LocalDictationSession? _session;
    // All lifecycle and UI callbacks are owned by the dispatcher thread.
    private bool _closing;
    private TaskCompletionSource? _runCompletion;
    private TaskCompletionSource? _deleteCompletion;
    private ContentDialog? _deleteDialog;
    private ContentDialog? _testDialog;
    internal Task ShutdownAsync()
    {
        _closing = true;
        IsEnabled = false;
        _run?.Cancel();
        try { _deleteDialog?.Hide(); _defaultsDialog?.Hide(); _testDialog?.Hide(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Workflow dialog close failed: " + ex); }
        return Task.WhenAll(_defaultsCompletion?.Task ?? Task.CompletedTask, _runCompletion?.Task ?? Task.CompletedTask, _deleteCompletion?.Task ?? Task.CompletedTask);
    }

    // The fields of the configuration page. They are created in code so that they can sit in settings rows.
    private readonly WorkflowTemplatePicker ConfigTemplate = new();
    private readonly ChoicePicker ConfigActionTarget = new(), ConfigMemory = new(), ConfigTrigger = new(), ConfigContextMode = new(),
        ConfigTask = new(), ConfigLanguage = new(), ConfigTranscriptionModel = new(), ConfigProvider = new(), ConfigModel = new();
    private readonly ToggleSwitch ConfigEnabled = AppToggleSwitch.Create(true);
    private readonly StackPanel ConfigShortcutHost = new();
    private readonly SettingsRow ConfigShortcutSection = new(), ConfigAppSection = new(), ConfigWebsiteSection = new(), ConfigContextSection = new(),
        ConfigTaskSection = new(), ConfigLanguageSection = new(), ConfigTranscriptionModelSection = new(), ConfigMemorySection = new(), ConfigInstructionSection = new(), ConfigProviderSection = new(),
        ConfigModelSection = new(), ConfigTranslationSection = new(), ConfigActionSection = new();
    private TextBox ConfigName = null!, ConfigAppProcesses = null!, ConfigWebsiteDomains = null!, ConfigTranslationTarget = null!,
        ConfigPriority = null!, ConfigInstruction = null!;
    private HandCursorButton DeleteWorkflowButton = null!;

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
    internal bool IsBusy => _defaultsDialog is not null || _testDialog is not null || _run is not null || _page == Page.Configuration || _deleteCompletion is { Task.IsCompleted: false };
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

    private void RuntimeChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_page != Page.Configuration) return;
        ConfigureActionTargets(ConfigActionTarget.SelectedId);
        ConfigureMemorySources(ConfigMemory.SelectedId);
        ConfigProvider.SetOptions(Providers, ConfigProvider.SelectedId, Loc.T("{0} (unavailable)", ConfigProvider.SelectedId));
        ConfigureModels(ConfigModel.SelectedId);
        UpdateConfigurationState();
    });

    private void ConfigureMemorySources(string selected) => ConfigMemory.SetOptions([new("", Loc.T("Off"), Loc.T("Do not use saved memories")), .. (_session?.PluginRuntime.MemoryProviders.OrderBy(p => p.Name).Select(p => new Choice(p.PluginId, p.Name, Loc.T("Use matching saved facts")) { PluginId = p.PluginId }) ?? [])], selected, Loc.T("Unavailable memory source: {0}", selected));

    private void ConfigureActionTargets(string selected) => ConfigActionTarget.SetOptions([new("", Loc.T("Insert Text"), Loc.T("Use dictation output preferences")), .. (_session?.PluginRuntime.Actions.OrderBy(a => a.Name).Select(a => new Choice(a.PluginId, a.Name, Loc.T("Send the workflow result to this action")) { PluginId = a.PluginId }) ?? [])], selected, Loc.T("Unavailable action: {0}", selected));

    private bool Available(string provider, string model) => _session?.LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model)) == true;
    internal ObservableCollection<WorkflowDraft> FilteredWorkflows { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? DetailModeChanged;
    internal event Action<string>? ConfigurationSaved;
    internal bool EditWorkflow(string id)
    {
        if (_closing || IsBusy) return false;
        var workflow = _workflows.FirstOrDefault(item => item.Id == id && item.IsEditable);
        if (workflow is null) return false;
        Edit(workflow);
        return true;
    }
    internal bool IsDetail => _page != Page.List;
    // The settings page owns the search box; it sits between the title and the list.
    internal void SetSearch(UIElement search) => WorkflowSearchHost.Child = search;

    public WorkflowsView()
    {
        InitializeComponent();
        AutomationProperties.SetName(WorkflowList, Loc.T("Saved workflows"));
        KeepWorkflowEditing.Content = Loc.T("Keep editing");
        AutomationProperties.SetName(KeepWorkflowEditing, Loc.T("Keep workflow changes"));
        DiscardWorkflowChanges.Content = Loc.T("Discard changes");
        AutomationProperties.SetName(DiscardWorkflowChanges, Loc.T("Discard workflow changes"));
        DefaultLlmButton.Content = Loc.T("Default LLM");
        TestWorkflowButton.Content = Loc.T("Test workflow…");
        CancelConfigurationButton.Content = Loc.T("Cancel");
        NewWorkflowButton.Content = Loc.T("+  New workflow");
        AutomationProperties.SetName(NewWorkflowButton, Loc.T("Create new workflow"));
        AutomationProperties.SetName(WorkflowPrimaryButton, Loc.T("Workflow primary action"));
        EntryActionMenu.Attach(this, WorkflowContextActions);
        InitializeIconPicker();
        BuildConfiguration();
        // Row switches are created by the template, so their colors come from the list.
        if (!new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            foreach (var state in new[] { "", "PointerOver", "Pressed" })
            {
                WorkflowList.Resources[$"ToggleSwitchFillOn{state}"] = (Brush)Application.Current.Resources["AccentBrush"];
                WorkflowList.Resources[$"ToggleSwitchKnobFillOn{state}"] = (Brush)Application.Current.Resources["InkBrush"];
            }
        // The first row follows the card's upper corners and the last its lower ones; the others get a line above.
        WorkflowList.ContainerContentChanging += (_, args) =>
        {
            var first = args.ItemIndex == 0;
            var last = args.ItemIndex == FilteredWorkflows.Count - 1;
            args.ItemContainer.CornerRadius = new CornerRadius(first ? 11 : 0, first ? 11 : 0, last ? 11 : 0, last ? 11 : 0);
            args.ItemContainer.BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0);
            // A recycled row keeps its controls, so their names follow the workflow now shown.
            if (args.Item is WorkflowDraft workflow && args.ItemContainer.ContentTemplateRoot is Panel row)
                foreach (var control in row.Children.OfType<Control>()) NameRowAction(control, workflow);
        };
        ConfigMemory.Configure(Loc.T("Memory context"), "file", Loc.T("Workflow memory source"));
        ConfigMemory.SelectionChanged += _ => UpdateConfigurationState();
        ConfigActionTarget.Configure(Loc.T("Action Target"), "plugin", Loc.T("Workflow action target"));
        ConfigActionTarget.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTrigger.Configure(Loc.T("Activation"), "workflow", Loc.T("Workflow activation"));
        ConfigTrigger.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTask.Configure(Loc.T("Transcription task"), "microphone", Loc.T("Workflow transcription task"));
        ConfigTask.SelectionChanged += _ => UpdateConfigurationState();
        ConfigLanguage.Configure(Loc.T("Spoken language"), "language", Loc.T("Workflow spoken language"));
        ConfigLanguage.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTranscriptionModel.Configure(Loc.T("Transcription model"), "microphone", Loc.T("Workflow transcription model"));
        ConfigTranscriptionModel.SelectionChanged += _ => UpdateConfigurationState();
        ConfigContextMode.Configure(Loc.T("App and website conditions"), "workflow", Loc.T("Workflow context match mode"));
        ConfigContextMode.SelectionChanged += _ => UpdateConfigurationState();
        ConfigTemplate.SelectionChanged += _ =>
        {
            if (!_loadingConfiguration && Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var template))
            {
                var suggested = WorkflowTemplateCatalog.DefinitionFor(template).Name;
                ConfigName.Text = WorkflowTemplateNames.ForSelection(ConfigName.Text, _suggestedName, suggested);
                _suggestedName = suggested;
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

    // The configuration page: one card per group, as on the other settings pages.
    private void BuildConfiguration()
    {
        TextBox Field(string name, int maxLength, double width = double.NaN, bool multiline = false)
        {
            var box = new TextBox { Style = (Style)Resources["WorkflowEditorStyle"], MaxLength = maxLength, Width = width };
            if (multiline) box.Height = 110;
            else { box.AcceptsReturn = false; box.TextWrapping = TextWrapping.NoWrap; box.Height = 38; }
            AutomationProperties.SetName(box, name);
            box.TextChanged += Configuration_Changed;
            return box;
        }
        ConfigName = Field(Loc.T("Workflow name"), 80, 260);
        ConfigAppProcesses = Field(Loc.T("Workflow process names"), 1000);
        ConfigAppProcesses.PlaceholderText = Loc.T("notepad, chrome (without .exe)");
        ConfigWebsiteDomains = Field(Loc.T("Workflow website domains"), 2000);
        ConfigWebsiteDomains.PlaceholderText = "example.com, docs.example.org";
        ConfigTranslationTarget = Field(Loc.T("Workflow translation target language"), 100, 260);
        ConfigTranslationTarget.PlaceholderText = Loc.T("English (default)");
        ConfigPriority = Field(Loc.T("Workflow priority"), 11, 120);
        ConfigInstruction = Field(Loc.T("Workflow instructions"), 8000, multiline: true);
        AutomationProperties.SetName(ConfigEnabled, Loc.T("Enable workflow"));
        ConfigEnabled.Toggled += ConfigEnabled_Toggled;

        var general = new SettingsCard();
        general.Children.Add(new SettingsRow().Set(Loc.T("Name"), control: ConfigName));
        general.Children.Add(new SettingsRow().Set(Loc.T("Icon"), control: _iconPicker));
        general.Children.Add(new SettingsRow().Set(Loc.T("Enable workflow"), control: ConfigEnabled));
        ConfigurationFields.Children.Add(general);

        var template = new SettingsCard(Loc.T("Template"), Loc.T("Choose the result this workflow should produce."));
        ConfigTemplate.Margin = new Thickness(0, 14, 0, 14);
        template.Add(ConfigTemplate);
        template.Children.Add(ConfigTranslationSection.Set(Loc.T("Translation language"),
            Loc.T("Leave empty to translate into English. This is a text workflow using the selected LLM provider."), ConfigTranslationTarget));
        ConfigurationFields.Children.Add(template);

        var trigger = new SettingsCard(Loc.T("Trigger"));
        trigger.Children.Add(new SettingsRow().Set(Loc.T("Activation"), "",
            Loc.T("Matching app and website rules take precedence, followed by website, app, then global fallback. Lower priority numbers win within a group; equal priorities use the workflow name. Dictation shortcuts apply their workflow and transcription task for one recording, overriding these automatic rules. The selected action target receives the finished workflow result."), ConfigTrigger));
        // Its description says what the shortcut does for the chosen activation.
        trigger.Children.Add(ConfigShortcutSection.Set(Loc.T("Shortcut")).Below(ConfigShortcutHost));
        trigger.Children.Add(ConfigAppSection.Set(Loc.T("Windows process names"),
            Loc.T("Required for App activation; optional for Website activation. Separate process names with commas."), (FrameworkElement?)null).Below(ConfigAppProcesses));
        trigger.Children.Add(ConfigWebsiteSection.Set(Loc.T("Website domains"),
            Loc.T("Required for Website activation; optional for App activation. Domains include subdomains (example.com also matches mail.example.com). Use commas, without paths or query strings. The browser address is read once before recording; only the hostname can enter saved History. Chrome, Edge, Brave, Chromium and Firefox require a recognized address bar. Missing context leaves app/global fallback rules available."), (FrameworkElement?)null).Below(ConfigWebsiteDomains));
        trigger.Children.Add(ConfigContextSection.Set(Loc.T("App and website conditions"),
            Loc.T("Match all requires an app from your list AND a domain from your list. Match any allows either component, so the app rule can still run when a browser address is unavailable."), ConfigContextMode));
        // Its description names the limits of the chosen task.
        trigger.Children.Add(ConfigTaskSection.Set(Loc.T("Transcription task"), control: ConfigTask));
        // Its description names a language the current model cannot use.
        trigger.Children.Add(ConfigLanguageSection.Set(Loc.T("Spoken language"), control: ConfigLanguage));
        trigger.Children.Add(ConfigTranscriptionModelSection.Set(Loc.T("Transcription model"),
            Loc.T("Dictation Only shortcuts can use their own model. It loads when you press the shortcut and your selected model returns after the recording, so a different local model adds loading time."), ConfigTranscriptionModel));
        ConfigurationFields.Children.Add(trigger);

        var behavior = new SettingsCard(Loc.T("Behavior"));
        // Its description says where the result goes.
        behavior.Children.Add(ConfigActionSection.Set(Loc.T("Action Target"), control: ConfigActionTarget));
        behavior.Children.Add(ConfigMemorySection.Set(Loc.T("Memory context"),
            Loc.T("Optional: search saved facts related to your text. Up to five matches are sent to this workflow’s LLM provider. Nothing is remembered automatically. Dictation-only workflows do not use memory context."), ConfigMemory));
        ConfigurationFields.Children.Add(behavior);

        var advanced = new SettingsCard(Loc.T("Advanced"));
        advanced.Children.Add(ConfigProviderSection.Set(Loc.T("Provider"),
            Loc.T("Manual workflows run when you choose Run. Selected-text shortcuts send your selection and instructions to the configured provider and open the result for review. App, Website and Global workflows run on matching dictations and use dictation output settings; failures open review without pasting."), ConfigProvider));
        advanced.Children.Add(ConfigModelSection.Set(Loc.T("Model"), control: ConfigModel));
        advanced.Children.Add(ConfigInstructionSection.Set(Loc.T("Instructions (required)")).Below(ConfigInstruction));
        advanced.Children.Add(new SettingsRow().Set(Loc.T("Priority"), Loc.T("Lower numbers win."), "", ConfigPriority));
        ConfigurationFields.Children.Add(advanced);

        DeleteWorkflowButton = new HandCursorButton { Content = Loc.T("Delete workflow"), HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["DestructiveButtonStyle"] };
        AutomationProperties.SetName(DeleteWorkflowButton, Loc.T("Delete workflow"));
        DeleteWorkflowButton.Click += (_, _) => { if (_opened is { } workflow && !_creating) Delete(workflow); };
        ConfigurationFields.Children.Add(DeleteWorkflowButton);
    }

    internal void Filter(string query)
    {
        if (_run is not null) { if (query != _query) _run.Cancel(); return; }
        if (_page == Page.Configuration) return;
        _query = query;
        var selected = WorkflowList.SelectedItem as WorkflowDraft;
        FilteredWorkflows.Clear();
        foreach (var item in _workflows.Where(item => query.Length == 0
            || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase))) FilteredWorkflows.Add(item);
        WorkflowList.SelectedItem = FilteredWorkflows.FirstOrDefault(item => item.Id == selected?.Id) ?? FilteredWorkflows.FirstOrDefault();
        ShowPage(Page.List);
        WorkflowEmptyState.Visibility = FilteredWorkflows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkflowListCard.Visibility = FilteredWorkflows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Without any workflow, a search hint would be misleading; offer the first workflow instead.
        var none = _workflows.Count == 0;
        WorkflowEmptyTitle.Text = none ? Loc.T("No workflows yet") : Loc.T("No workflows found");
        WorkflowEmptyAction.Content = none ? Loc.T("Create first workflow") : Loc.T("Clear search");
        AutomationProperties.SetName(WorkflowEmptyAction, none ? Loc.T("Create first workflow") : Loc.T("Clear workflow search"));
    }

    internal void MoveSelection(int offset)
    {
        if (IsDetail || FilteredWorkflows.Count == 0) return;
        WorkflowList.SelectedIndex = Math.Clamp(WorkflowList.SelectedIndex + offset, 0, FilteredWorkflows.Count - 1);
        WorkflowList.ScrollIntoView(WorkflowList.SelectedItem);
    }

    internal void FocusEntry()
    {
        if (_page != Page.Configuration) return;
        if (ConfigurationDiscardPrompt.Visibility == Visibility.Visible) KeepWorkflowEditing.Focus(FocusState.Programmatic);
        else if (!ConfigTrigger.IsPopupOpen && !ConfigContextMode.IsPopupOpen && !ConfigProvider.IsPopupOpen && !ConfigModel.IsPopupOpen && !ConfigActionTarget.IsPopupOpen && !ConfigMemory.IsPopupOpen && !ConfigTask.IsPopupOpen && !ConfigLanguage.IsPopupOpen && !ConfigTranscriptionModel.IsPopupOpen) ConfigName.Focus(FocusState.Programmatic);
    }

    private void ShowPage(Page page)
    {
        _page = page;
        var list = page == Page.List;
        WorkflowListPage.Visibility = WorkflowSearchHost.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        WorkflowConfigurationPage.Visibility = ConfigurationValidation.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        WorkflowPageTitle.Text = list ? Loc.T("Workflows") : _creating ? Loc.T("New workflow") : _opened?.Title ?? Loc.T("Edit workflow");
        WorkflowSummary.Text = list ? (FilteredWorkflows.Count == 1 ? Loc.T("1 workflow") : Loc.T("{0} workflows", FilteredWorkflows.Count)) : "";
        if (_loadError is not null) WorkflowSummary.Text = _loadError;
        else if (Shortcuts?.Error is { } shortcutError) WorkflowSummary.Text = shortcutError;
        NewWorkflowButton.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        NewWorkflowButton.IsEnabled = _loadError is null && _store is not null;
        WorkflowPrimaryButton.Visibility = CancelConfigurationButton.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        WorkflowPrimaryButton.Content = _creating ? Loc.T("Create workflow") : Loc.T("Save changes");
        TestWorkflowButton.Visibility = !list && !_creating ? Visibility.Visible : Visibility.Collapsed;
        DetailModeChanged?.Invoke(!list);
        if (!list) UpdateConfigurationState();
    }

    internal void GoBack()
    {
        if (_closing) return;
        if (_run is not null) { _run.Cancel(); return; }
        if (_page != Page.Configuration) { ExitRequested?.Invoke(this, EventArgs.Empty); return; }
        foreach (var picker in new[] { ConfigTrigger, ConfigContextMode, ConfigProvider, ConfigModel, ConfigActionTarget, ConfigMemory, ConfigTask, ConfigLanguage, ConfigTranscriptionModel })
            if (picker.IsPopupOpen) { picker.ClosePopup(); return; }
        if (ConfigurationDiscardPrompt.Visibility == Visibility.Visible) { DismissDiscard(); return; }
        if (!ConfigurationDirty) { LeaveConfiguration(); return; }
        ConfigurationDiscardPrompt.Visibility = Visibility.Visible;
        ConfigurationScroll.IsEnabled = WorkflowPrimaryButton.IsEnabled = TestWorkflowButton.IsEnabled = CancelConfigurationButton.IsEnabled = false;
        WorkflowConfigurationPage.Opacity = 0.2;
        KeepWorkflowEditing.Focus(FocusState.Programmatic);
    }

    private void Edit(WorkflowDraft workflow)
    {
        if (_closing || IsBusy) return;
        if (!workflow.IsEditable)
        {
            WorkflowSummary.Text = Loc.T("Unsupported workflow: editing and execution are unavailable. Enablement can change without changing other settings. An enabled matching App/Global rule with unsupported overrides sends dictation to review without pasting.");
            return;
        }
        _opened = workflow;
        _creating = false;
        LoadConfiguration();
    }

    private void Workflow_Click(object sender, ItemClickEventArgs e)
    {
        WorkflowList.SelectedItem = e.ClickedItem;
        if (e.ClickedItem is WorkflowDraft workflow) Edit(workflow);
    }

    private void RowToggle_Loaded(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        AppToggleSwitch.Configure(toggle);
        if (toggle.Tag is WorkflowDraft workflow) NameRowAction(toggle, workflow);
    }

    private void RowDelete_Loaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        ToolTipService.SetToolTip(button, Loc.T("Delete"));
        if (button.Tag is WorkflowDraft workflow) NameRowAction(button, workflow);
    }

    // Screen readers hear which workflow a row's switch or delete button belongs to.
    private static void NameRowAction(Control control, WorkflowDraft workflow) => AutomationProperties.SetName(control,
        (control is ToggleSwitch ? Loc.T("Enable workflow") : Loc.T("Delete workflow")) + ": " + workflow.Title);

    // A recycled row sets its switch before its workflow; compare once both are in place.
    private void RowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (toggle.Tag is WorkflowDraft workflow && workflow.IsEnabled != toggle.IsOn && _page == Page.List) SetEnabled(workflow, toggle.IsOn);
        });
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is WorkflowDraft workflow) Delete(workflow);
    }

    private void SetEnabled(WorkflowDraft workflow, bool enabled)
    {
        if (_closing || _run is not null || _store is null || _loadError is not null) return;
        string summary;
        try
        {
            RequireUnchangedApiWorkflow(workflow);
            var updated = WorkflowDraft.FromStored(Shortcuts is { } shortcuts
                ? shortcuts.SetEnabled(workflow.Id, enabled) : _store.SetEnabled(workflow.Id, enabled));
            var index = _workflows.FindIndex(item => item.Id == updated.Id);
            if (index >= 0) _workflows[index] = updated;
            summary = updated.IsEnabled ? Loc.T("Workflow enabled") : Loc.T("Workflow disabled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { summary = Loc.T("Enablement was not changed. {0}", ex.Message); }
        // Rebuilding the list also puts a switch back that could not be saved.
        Filter(_query);
        WorkflowSummary.Text = summary;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0) { NewWorkflow_Click(sender, e); return; }
        Filter(string.Empty); ClearSearchRequested?.Invoke(this, EventArgs.Empty);
    }
    private void Primary_Click(object sender, RoutedEventArgs e) { if (_page == Page.Configuration) SaveConfiguration(); }
    private void CancelConfiguration_Click(object sender, RoutedEventArgs e) => GoBack();
    private void TestWorkflow_Click(object sender, RoutedEventArgs e) { if (_opened is { } workflow && !_creating && !ConfigurationDirty) TestWorkflow(workflow); }

    private IReadOnlyList<Choice> Models => _session?.LlmProviders.FirstOrDefault(p => p.SelectionId == ConfigProvider.SelectedId)?.Models
        .Select(m => new Choice(m.Id, m.DisplayName, m.Id)).ToArray() ?? [];
    private bool ConfigUsesRecordingTask => ConfigTrigger.SelectedId is "DictationHotkey" or "App" or "Website" or "Global";
    private string? ConfigSelectedTask => ConfigUsesRecordingTask
        ? (string.IsNullOrEmpty(ConfigTask.SelectedId) ? null : ConfigTask.SelectedId)
        : _opened is { } opened && ConfigTrigger.SelectedId == opened.ActivationId ? opened.SelectedTask : null;
    private string? ConfigSelectedLanguage => ConfigUsesRecordingTask
        ? (string.IsNullOrEmpty(ConfigLanguage.SelectedId) ? null : ConfigLanguage.SelectedId)
        : _opened is { } opened && ConfigTrigger.SelectedId == opened.ActivationId ? opened.InputLanguage : null;
    private bool ConfigUsesTranscriptionModel => ConfigTrigger.SelectedId == "DictationHotkey" && ConfigTemplate.SelectedId == nameof(WorkflowTemplate.Dictation);
    // Other activations and templates cannot switch models, so their saved choice is dropped.
    private string? ConfigSelectedTranscriptionModel => ConfigUsesTranscriptionModel && !string.IsNullOrEmpty(ConfigTranscriptionModel.SelectedId)
        ? ConfigTranscriptionModel.SelectedId : null;
    // The languages the current transcription model accepts; a workflow model is checked once it loads.
    private bool ConfigLanguageUnsupported => ConfigSelectedTranscriptionModel is null && ConfigSelectedLanguage is { } language && language != "auto"
        && _session is { } session && !session.LanguageChoices.Contains(language, StringComparer.OrdinalIgnoreCase);
    private bool ConfigurationDirty => _opened is not null && (ConfigName.Text != _opened.Title || ConfigInstruction.Text.ReplaceLineEndings("\n") != _opened.Instruction.ReplaceLineEndings("\n")
        || ConfigActionTarget.SelectedId != (_opened.TargetActionPluginId ?? "")
        || ConfigMemory.SelectedId != (_opened.MemoryPluginId ?? "")
        || _draftIcon != _opened.IconKind
        || ConfigTrigger.SelectedId != _opened.ActivationId || ConfigAppProcesses.Text != _opened.AppProcesses
        || DraftHotkeys != _opened.Hotkeys
        || ConfigSelectedTask != (string.IsNullOrWhiteSpace(_opened.SelectedTask) ? null : _opened.SelectedTask)
        || ConfigSelectedLanguage != _opened.InputLanguage || ConfigSelectedTranscriptionModel != _opened.TranscriptionModel
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

    private IEnumerable<EntryActionMenu.Action> WorkflowContextActions()
    {
        foreach (var action in EntryActionMenu.FromButtons(ContextActionsFooter)) yield return action;
        if (_page != Page.List || WorkflowList.SelectedItem is not WorkflowDraft { IsEditable: true } workflow) yield break;
        yield return new(Loc.T("Edit workflow"), () => Edit(workflow), !IsBusy);
        yield return new(Loc.T("Test workflow…"), () => TestWorkflow(workflow), !IsBusy);
        yield return new(Loc.T("Set shortcut for selected text…"), () => ConfigureShortcut("Hotkey"), !IsBusy);
        yield return new(Loc.T("Set shortcut for dictation…"), () => ConfigureShortcut("DictationHotkey"), !IsBusy);
    }

    private void ConfigureShortcut(string activation)
    {
        if (_closing || IsBusy) return;
        if (WorkflowList.SelectedItem is not WorkflowDraft { IsEditable: true } workflow) return;
        _opened = workflow;
        _creating = false;
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
        if (_page != Page.List || _closing || IsBusy) return;
        _selectionBeforeCreate = WorkflowList.SelectedItem as WorkflowDraft;
        _creating = true;
        _opened = new WorkflowDraft(Guid.NewGuid().ToString("N"), WorkflowTemplateCatalog.DefinitionFor(WorkflowTemplate.CleanedText).Name, Loc.T("Manual workflow"), "workflow", "") { Template = WorkflowTemplate.CleanedText };
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
        _suggestedName = _creating ? WorkflowTemplateCatalog.DefinitionFor(_opened.Template).Name : null;
        ConfigName.Text = _opened.Title;
        SetDraftIcon(_opened.IconKind);
        ConfigTrigger.SetOptions([
            new("Manual", Loc.T("Manual"), Loc.T("Run explicitly with source text")),
            new("Hotkey", Loc.T("Shortcut · selected text"), Loc.T("Send the selected text to this workflow and review the result")),
            new("DictationHotkey", Loc.T("Shortcut · dictation"), Loc.T("Press to start dictation with this workflow; press again to stop")),
            new("App", Loc.T("App"), Loc.T("Apply to dictation in matching Windows processes")),
            new("Website", Loc.T("Website"), Loc.T("Apply to dictation on matching browser domains")),
            new("Global", Loc.T("Global fallback"), Loc.T("Apply when no app or website rule matches"))], activation ?? _opened.ActivationId);
        ConfigTask.SetOptions([
            new("", Loc.T("Use global setting"), Loc.T("Use the transcription task selected in Dictation")),
            new("transcribe", Loc.T("Transcribe"), Loc.T("Keep speech in its original language")),
            new("translate", Loc.T("Translate to English"), Loc.T("Use the transcription model's native English translation"))], string.IsNullOrWhiteSpace(_opened.SelectedTask) ? "" : _opened.SelectedTask);
        ConfigureTranscriptionModels(_opened.TranscriptionModel ?? "");
        ConfigureLanguages(_opened.InputLanguage ?? "");
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

    // Without a workflow model the list follows the selected model; a workflow model is checked once it loads.
    private bool _languagesForWorkflowModel;
    private void ConfigureLanguages(string language)
    {
        _languagesForWorkflowModel = ConfigSelectedTranscriptionModel is not null;
        var codes = _languagesForWorkflowModel ? SpokenLanguageChoices.All : _session?.LanguageChoices ?? [];
        var detects = _languagesForWorkflowModel || _session?.SupportedLanguages.Count == 0;
        var languages = codes.Select(code => new Choice(code, LiveDictationSettings.LanguageName(code), Loc.T("Use this language for recordings with this workflow")))
            .OrderBy(choice => detects ? choice.Label : "", StringComparer.CurrentCulture);
        Choice[] options = [new("", Loc.T("Use global setting"), Loc.T("Use the spoken language selected in Dictation")),
            new("auto", Loc.T("Automatic"), Loc.T("Language detection by the model")), .. languages];
        ConfigLanguage.SetOptions(options, language, Loc.T("{0} (unavailable)", LiveDictationSettings.LanguageName(language)));
    }

    private void ConfigureTranscriptionModels(string model)
    {
        var models = (_session?.DictationProviders ?? []).Where(provider => provider.Enabled && provider.Configured)
            .SelectMany(provider => provider.Models.Where(item => item.Ready).Select(item =>
                new Choice(provider.Id + ":" + item.Id, item.Name, provider.Name) { PluginId = provider.PluginId }));
        Choice[] options = [new("", Loc.T("Use selected model"), Loc.T("Use the transcription model selected in Dictation")), .. models];
        ConfigTranscriptionModel.SetOptions(options, model, Loc.T("{0} (unavailable)", model));
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
        ConfigShortcutSection.Description = ConfigTrigger.SelectedId == "DictationHotkey"
            ? Loc.T("Focus a text field in another app. Press this shortcut to start recording and press again to stop. The transcript is processed by this workflow using your dictation paste and History settings.")
            : Loc.T("Select text in another app, then press this shortcut to send it to the configured provider. The result opens for review. Nothing is pasted or saved to History.");
        var contextual = ConfigTrigger.SelectedId is "App" or "Website";
        ConfigTaskSection.Visibility = ConfigUsesRecordingTask ? Visibility.Visible : Visibility.Collapsed;
        ConfigTaskSection.Description = Loc.T("Applies only to this recording. Native translation outputs English and requires a compatible transcription model. With Dictation Only, no LLM is needed; local models work offline.")
            + (ConfigTask.SelectedId == "translate" && _session?.SupportsTranslation != true
                ? " " + Loc.T("The current model cannot translate to English. Choose a compatible model in Dictation before running this workflow.") : "");
        ConfigLanguageSection.Visibility = ConfigTaskSection.Visibility;
        ConfigTranscriptionModelSection.Visibility = ConfigUsesTranscriptionModel ? Visibility.Visible : Visibility.Collapsed;
        if ((ConfigSelectedTranscriptionModel is not null) != _languagesForWorkflowModel) ConfigureLanguages(ConfigLanguage.SelectedId);
        ConfigLanguageSection.Description = Loc.T("Applies only to this recording. Use it for a shortcut per language; the global spoken language stays unchanged.")
            + (ConfigLanguageUnsupported ? " " + Loc.T("The current transcription model does not support this language. Choose another language, or another model in Dictation, before running this workflow.") : "");
        ConfigShortcutSection.Visibility = ConfigTrigger.SelectedId is "Hotkey" or "DictationHotkey" ? Visibility.Visible : Visibility.Collapsed;
        ConfigAppSection.Visibility = ConfigWebsiteSection.Visibility = contextual ? Visibility.Visible : Visibility.Collapsed;
        ConfigContextSection.Visibility = contextual && !string.IsNullOrWhiteSpace(ConfigAppProcesses.Text) && !string.IsNullOrWhiteSpace(ConfigWebsiteDomains.Text) ? Visibility.Visible : Visibility.Collapsed;
        var template = Enum.TryParse<WorkflowTemplate>(ConfigTemplate.SelectedId, out var selected) ? selected : WorkflowTemplate.Custom;
        ConfigMemorySection.Visibility = ConfigInstructionSection.Visibility = ConfigProviderSection.Visibility = ConfigModelSection.Visibility = template == WorkflowTemplate.Dictation ? Visibility.Collapsed : Visibility.Visible;
        ConfigTranslationSection.Visibility = template == WorkflowTemplate.Translation ? Visibility.Visible : Visibility.Collapsed;
        ConfigInstructionSection.Title = template == WorkflowTemplate.Custom ? Loc.T("Instructions (required)") : Loc.T("Fine-tuning (optional)");
        ConfigActionSection.Description = string.IsNullOrEmpty(ConfigActionTarget.SelectedId)
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
                    : ConfigTrigger.SelectedId == "Manual" ? "" : Loc.T("Applies automatically to matching dictations. Uses your dictation paste and history settings.")));
        ConfigurationValidation.Foreground = (Brush)Application.Current.Resources[
            error is null && (!ConfigEnabled.IsOn || template == WorkflowTemplate.Dictation || EffectiveAvailable(ConfigProvider.SelectedId, ConfigModel.SelectedId)) ? "MutedBrush" : "AccentBrush"];
        var dirty = ConfigurationDirty;
        var prompting = ConfigurationDiscardPrompt.Visibility == Visibility.Visible;
        WorkflowSummary.Text = dirty ? Loc.T("Unsaved changes") : "";
        WorkflowPrimaryButton.IsEnabled = error is null && (_creating || dirty) && !prompting;
        CancelConfigurationButton.IsEnabled = !prompting;
        // A test runs the saved workflow, so it waits until the changes are saved.
        TestWorkflowButton.IsEnabled = !dirty && !prompting && _apiConfigurationConflict is null;
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
            SelectedTask = ConfigSelectedTask, InputLanguage = ConfigSelectedLanguage, TranscriptionModel = ConfigSelectedTranscriptionModel,
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
        if (created) _workflows.Add(updated);
        else _workflows[_workflows.FindIndex(workflow => workflow.Id == updated.Id)] = updated;
        _creating = false;
        _opened = updated;
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

    private async void Delete(WorkflowDraft workflow)
    {
        if (_closing || _deleteCompletion is { Task.IsCompleted: false } || _run is not null || _store is null) return;
        var completion = _deleteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DeleteWorkflowButton.IsEnabled = false;
        try
        {
            var dialog = _deleteDialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = Loc.T("Delete this workflow?"),
                Content = Loc.T("Delete \"{0}\" and its saved instructions? This cannot be undone. Unsaved edits and this workflow's source-text draft will also be discarded.", workflow.Title),
                PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close, PrimaryButtonStyle = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DestructiveConfirmButtonStyle"]
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing) return;
            RequireUnchangedApiWorkflow(workflow);
            if (Shortcuts is { } shortcuts) shortcuts.Delete(workflow.Id);
            else _store.Delete(workflow.Id, allowAutomatic: true);
            _workflows.RemoveAll(w => w.Id == workflow.Id);
            _drafts.Remove(workflow.Id);
            if (_opened?.Id == workflow.Id) _opened = null;
            if (_page == Page.Configuration) LeaveConfiguration(); else Filter(_query);
            WorkflowSummary.Text = Loc.T("Workflow deleted");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = Loc.T("The workflow was not deleted. {0}", ex.Message);
            (_page == Page.Configuration ? ConfigurationValidation : WorkflowSummary).Text = message;
        }
        finally
        {
            _deleteDialog = null;
            try { DeleteWorkflowButton.IsEnabled = !_closing; }
            finally { completion.TrySetResult(); }
        }
    }

    // A test run in a dialog: the source text, the run and its result, without leaving the page.
    private async void TestWorkflow(WorkflowDraft workflow)
    {
        if (_closing || _session is null || _testDialog is not null || _run is not null || !workflow.IsEditable) return;
        var session = _session;
        var source = new TextBox { Style = (Style)Resources["WorkflowEditorStyle"], Height = 120, PlaceholderText = Loc.T("Paste or type your text…"),
            Text = (_drafts.GetValueOrDefault(workflow.Id) ?? "").ReplaceLineEndings("\r") };
        AutomationProperties.SetName(source, Loc.T("Workflow source text"));
        TextBlock Muted(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        var hint = Muted("");
        AutomationProperties.SetLiveSetting(hint, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var resultText = new TextBlock { FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var result = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        result.Children.Add(Muted(Loc.T("Result")));
        result.Children.Add(new ScrollViewer { Content = resultText, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 0) });
        var content = new StackPanel { Spacing = 12, MinWidth = 460 };
        content.Children.Add(Muted(ExecutionSummary(workflow)));
        content.Children.Add(source); content.Children.Add(hint); content.Children.Add(result);
        var run = _session.PluginRuntime.Actions.FirstOrDefault(a => a.PluginId == workflow.TargetActionPluginId)?.Name ?? Loc.T("Run workflow");
        var dialog = _testDialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = workflow.Title, Content = content,
            PrimaryButtonText = run, SecondaryButtonText = Loc.T("Copy result"), CloseButtonText = Loc.T("Close"),
            DefaultButton = ContentDialogButton.Primary, IsSecondaryButtonEnabled = false
        };
        bool Runnable() => workflow.IsEnabled && (workflow.Template == WorkflowTemplate.Dictation || EffectiveAvailable(workflow.ProviderId, workflow.ModelId));
        void Refresh()
        {
            if (_run is not null) return;
            dialog.PrimaryButtonText = run;
            dialog.IsPrimaryButtonEnabled = Runnable() && !string.IsNullOrWhiteSpace(source.Text);
            hint.Text = _workflowRunFailure is { } failure && failure.Id == workflow.Id ? failure.Message
                : !workflow.IsEnabled ? Loc.T("This workflow is disabled. Enable it in Edit workflow to run it.")
                : !Runnable() ? Loc.T("The saved provider or model is unavailable. Edit the workflow or configure the plugin.")
                : string.IsNullOrWhiteSpace(source.Text) ? Loc.T("Paste or type the text to process.")
                : !string.IsNullOrEmpty(workflow.TargetActionPluginId) ? Loc.T("Run processes this text and sends the result to the saved plugin action.")
                : Loc.T("Run sends this text to the selected provider. Review the result before copying.");
        }
        // A result belongs to the text it was made from.
        void ClearResult() { result.Visibility = Visibility.Collapsed; resultText.Text = ""; dialog.IsSecondaryButtonEnabled = false; }
        source.TextChanged += (_, _) => { _workflowRunFailure = null; _drafts[workflow.Id] = source.Text; ClearResult(); Refresh(); };
        async Task RunAsync()
        {
            using var cancellation = new CancellationTokenSource();
            _run = cancellation;
            _workflowRunFailure = null;
            var completion = _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                source.IsReadOnly = true;
                ClearResult();
                // The workflow may have changed or gone through the API since this dialog opened.
                RequireUnchangedApiWorkflow(workflow);
                dialog.PrimaryButtonText = Loc.T("Cancel run");
                hint.Text = string.IsNullOrWhiteSpace(workflow.TargetActionPluginId) ? Loc.T("Processing with the saved provider and model…") : Loc.T("Processing and sending to the selected action…");
                var execution = await session.RunWorkflowWithActionAsync(session.WorkflowDefaults.Resolve(workflow.ToStored()), source.Text, cancellation.Token);
                if (_closing) return;
                resultText.Text = execution.Text;
                result.Visibility = Visibility.Visible;
                dialog.IsSecondaryButtonEnabled = true;
                _workflowRunFailure = (workflow.Id, execution.Message ?? Loc.T("Completed. Review and copy the result."));
            }
            catch (OperationCanceledException) { _workflowRunFailure = (workflow.Id, Loc.T("Run cancelled. Your source text is unchanged.")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _workflowRunFailure = (workflow.Id, Loc.T("Run failed. {0}", ex.Message)); }
            finally
            {
                _run = null;
                source.IsReadOnly = false;
                try
                {
                    if (!_closing) Refresh();
                    // The dialog closed while this run was still ending.
                    if (!_closing && _testDialog is null && _page == Page.List) Filter(_query);
                }
                finally { completion.TrySetResult(); }
            }
        }
        // Both buttons act inside the dialog; only Close leaves it.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (_run is not null) _run.Cancel(); else { var started = RunAsync(); }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var data = new DataPackage();
                data.SetText(resultText.Text);
                Clipboard.SetContent(data);
                hint.Text = Loc.T("Result copied");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { hint.Text = Loc.T("Clipboard unavailable"); }
        };
        dialog.Opened += (_, _) => source.Focus(FocusState.Programmatic);
        _workflowRunFailure = null;
        Refresh();
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { WorkflowSummary.Text = ex.Message; }
        finally { _run?.Cancel(); _testDialog = null; _workflowRunFailure = null; }
        // An update that arrived through the API during a run could not refresh the list.
        if (!_closing && _page == Page.List) Filter(_query);
    }

    private void LeaveConfiguration()
    {
        DismissDiscard();
        if (_creating)
        {
            _opened = _selectionBeforeCreate;
            _creating = false;
        }
        ShowPage(Page.List);
        Filter(_query);
        WorkflowList.SelectedItem = FilteredWorkflows.FirstOrDefault(item => item.Id == _opened?.Id) ?? FilteredWorkflows.FirstOrDefault();
        WorkflowList.Focus(FocusState.Programmatic);
    }

    private void DismissDiscard()
    {
        ConfigurationDiscardPrompt.Visibility = Visibility.Collapsed;
        ConfigurationScroll.IsEnabled = true;
        WorkflowConfigurationPage.Opacity = 1;
        if (_page == Page.Configuration) UpdateConfigurationState();
    }
    private void KeepEditing_Click(object sender, RoutedEventArgs e) { DismissDiscard(); FocusEntry(); }
    private void DiscardConfiguration_Click(object sender, RoutedEventArgs e) => LeaveConfiguration();

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
