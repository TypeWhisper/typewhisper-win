using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUIPrototype;

public sealed partial class PrototypeWorkflowsView : UserControl
{
    private enum Page { List, Editor, Result, Configuration }
    private Page _page;
    private PrototypeWorkflow? _opened;
    private string _query = string.Empty;
    private readonly Dictionary<string, string> _drafts = [];
    private readonly List<PrototypeWorkflow> _workflows = PrototypeWorkflow.Samples.ToList();
    private Page _configurationReturnPage;
    private bool _loadingConfiguration;
    private bool _creating;
    private PrototypeWorkflow? _selectionBeforeCreate;
    private Action? _afterConfigurationExit;
    private static readonly PrototypeChoice[] Providers = [new("none", "Not configured", "Set up a provider later"),
        new("local", "Local engine", "Demo provider · no model loaded"), new("cloud", "Cloud provider", "Demo provider · no account connected")];
    private static readonly PrototypeChoice[] LocalModels = [new("local-small", "Small demo model", "Example model choice"), new("local-large", "Large demo model", "Example model choice")];
    private static readonly PrototypeChoice[] CloudModels = [new("cloud-fast", "Fast demo model", "Example model choice"), new("cloud-quality", "Quality demo model", "Example model choice")];
    private static readonly PrototypeChoice[] Outputs = [new("preview", "Preview in Quick Launch", "Review the result before using it"),
        new("clipboard", "Copy to clipboard", "Configuration only · no automatic copy"), new("insert", "Insert in active app", "Configuration only · no text insertion")];
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
        Filter(string.Empty);
    }

    internal void Filter(string query)
    {
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
        WorkflowInstruction.Text = workflow.Instruction;
        WorkflowSource.Text = _drafts.GetValueOrDefault(workflow.Id) ?? workflow.ExampleInput;
        ShowPage(Page.Editor);
        FocusEntry();
    }

    internal void FocusEntry()
    {
        if (_page == Page.Editor) WorkflowSource.Focus(FocusState.Programmatic);
        else if (_page == Page.Configuration)
        {
            if (ConfigurationDiscardPrompt.Visibility == Visibility.Visible) KeepWorkflowEditing.Focus(FocusState.Programmatic);
            else if (!ConfigProvider.IsPopupOpen && !ConfigModel.IsPopupOpen && !ConfigOutput.IsPopupOpen) ConfigName.Focus(FocusState.Programmatic);
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
            ? $"{FilteredWorkflows.Count} {(FilteredWorkflows.Count == 1 ? "workflow" : "workflows")} · no AI calls" : "Demo · no AI calls";
        UpdateBreadcrumbs();
        WorkflowNavigationHint.Text = page switch { Page.Configuration => "Esc Cancel   Ctrl S Save", Page.Editor => "Esc Back   Ctrl Enter Preview", Page.Result => "⌫ / Esc Back", _ => "⌫ / Esc Back   ↑↓ Navigate   Enter Open" };
        WorkflowPrimaryButton.Visibility = page == Page.List ? Visibility.Collapsed : Visibility.Visible;
        WorkflowPrimaryButton.Content = page == Page.Configuration ? (_creating ? "Create workflow" : "Save changes") : page == Page.Result ? "Copy example" : "Preview example";
        WorkflowExecutionSummary.Text = _opened is null || _opened.ProviderId == "none"
            ? "No provider connected · nothing leaves this prototype"
            : $"{Providers.First(item => item.Id == _opened.ProviderId).Label} · {LocalModels.Concat(CloudModels).First(item => item.Id == _opened.ModelId).Label} · {Outputs.First(item => item.Id == _opened.OutputTarget).Label} · demo only";
        ConfigureWorkflowButton.Visibility = page is Page.List or Page.Editor ? Visibility.Visible : Visibility.Collapsed;
        NewWorkflowButton.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        UseExampleButton.Visibility = _opened?.HasExample == true ? Visibility.Visible : Visibility.Collapsed;
        ConfigureWorkflowButton.IsEnabled = page != Page.List || FilteredWorkflows.Count > 0;
        ConfigurationModeChanged?.Invoke(page == Page.Configuration);
        UpdateSourceState();
    }

    internal void GoBack()
    {
        if (_page == Page.Configuration)
        {
            foreach (var picker in new[] { ConfigProvider, ConfigModel, ConfigOutput })
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

    private void PreviewExample()
    {
        if (_page != Page.Editor || _opened?.HasExample != true || string.IsNullOrWhiteSpace(WorkflowSource.Text)) return;
        WorkflowResultText.Text = _opened.ExampleOutput;
        ShowPage(Page.Result);
        WorkflowResultScroll.ChangeView(null, 0, null, true);
        FocusEntry();
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
        WorkflowPrimaryButton.IsEnabled = _page == Page.Result || !empty && _opened?.HasExample == true;
        SourceWatermark.Visibility = WorkflowSource.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkflowInputHint.Text = _opened is { HasExample: false } ? "Custom workflow · execution is not connected in this prototype."
            : empty ? "Add some text or use the example to continue."
            : "Preview uses a fixed example result, not your edited text.";
    }

    private void Source_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Never use Enter or Backspace in the multiline editor for navigation.
        if (e.Key == Windows.System.VirtualKey.Enter
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            PreviewExample();
            e.Handled = true;
        }
    }

    private void Workflow_Click(object sender, ItemClickEventArgs e) { WorkflowList.SelectedItem = e.ClickedItem; OpenSelected(); }
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { Filter(string.Empty); ClearSearchRequested?.Invoke(this, EventArgs.Empty); }
    private void Example_Click(object sender, RoutedEventArgs e)
    {
        if (_opened is not null) WorkflowSource.Text = _opened.ExampleInput;
        FocusEntry();
    }
    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_page == Page.Configuration) { SaveConfiguration(); return; }
        if (_page == Page.Editor) { PreviewExample(); return; }
        if (_page != Page.Result) return;
        try
        {
            var data = new DataPackage();
            data.SetText(WorkflowResultText.Text);
            Clipboard.SetContent(data);
            WorkflowSummary.Text = "Example copied";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WorkflowSummary.Text = "Clipboard unavailable";
        }
    }

    private IReadOnlyList<PrototypeChoice> Models => ConfigProvider.SelectedId switch { "local" => LocalModels, "cloud" => CloudModels, _ => Array.Empty<PrototypeChoice>() };
    private bool ConfigurationDirty => _opened is not null && (ConfigName.Text != _opened.Title || ConfigInstruction.Text != _opened.Instruction
        || ConfigProvider.SelectedId != _opened.ProviderId || ConfigModel.SelectedId != _opened.ModelId || ConfigOutput.SelectedId != _opened.OutputTarget);
    private string? ConfigurationError => string.IsNullOrWhiteSpace(ConfigName.Text) ? "Enter a workflow name."
        : string.IsNullOrWhiteSpace(ConfigInstruction.Text) ? "Add instructions for this workflow."
        : ConfigProvider.SelectedId != "none" && !Models.Any(model => model.Id == ConfigModel.SelectedId) ? "Choose a model for this provider." : null;

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
        _opened = new PrototypeWorkflow(Guid.NewGuid().ToString("N"), "", "Custom workflow · this session only", "workflow", "", "", "");
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
        ConfigName.Text = _opened.Title;
        ConfigInstruction.Text = _opened.Instruction;
        ConfigProvider.SetOptions(Providers, _opened.ProviderId);
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
        ConfigModel.SetOptions(Models, modelId, ConfigModel.IsEnabled ? "Choose a model" : "No model selected");
    }

    private void Configuration_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingConfiguration && _page == Page.Configuration) UpdateConfigurationState();
    }

    private void UpdateConfigurationState()
    {
        if (_loadingConfiguration) return;
        var error = ConfigurationError;
        ConfigurationValidation.Text = error ?? "Saved only for this prototype session.";
        ConfigurationValidation.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[error is null ? "MutedBrush" : "AccentBrush"];
        WorkflowSummary.Text = ConfigurationDirty ? "Unsaved changes" : "Prototype configuration";
        WorkflowPrimaryButton.IsEnabled = error is null && ConfigurationDirty && ConfigurationDiscardPrompt.Visibility != Visibility.Visible;
    }

    private void SaveConfiguration()
    {
        if (_opened is null || ConfigurationError is not null || !ConfigurationDirty || ConfigurationDiscardPrompt.Visibility == Visibility.Visible) return;
        var updated = _opened with { Title = ConfigName.Text.Trim(), Instruction = ConfigInstruction.Text.Trim(),
            ProviderId = ConfigProvider.SelectedId, ModelId = ConfigModel.SelectedId, OutputTarget = ConfigOutput.SelectedId };
        var created = _creating;
        _afterConfigurationExit = null;
        if (created) _workflows.Add(updated);
        else _workflows[_workflows.FindIndex(workflow => workflow.Id == updated.Id)] = updated;
        _creating = false;
        _opened = updated;
        WorkflowInstruction.Text = updated.Instruction;
        if (created) _query = string.Empty;
        LeaveConfiguration();
        if (created)
        {
            ClearSearchRequested?.Invoke(this, EventArgs.Empty);
            WorkflowList.ScrollIntoView(WorkflowList.SelectedItem);
        }
        else WorkflowSummary.Text = "Saved · this session only";
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
        if (e.Key == Windows.System.VirtualKey.S
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            SaveConfiguration();
            e.Handled = true;
        }
    }
}
