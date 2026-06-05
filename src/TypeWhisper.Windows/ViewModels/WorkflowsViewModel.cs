using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Translation;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides workflows view model behavior.
/// </summary>
public sealed partial class WorkflowsViewModel : ObservableObject
{
    private readonly IWorkflowService _workflows;
    private readonly IActiveWindowService _activeWindow;
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly PluginManager _pluginManager;
    private readonly ModelManagerService _modelManager;
    private readonly WindowsAppDiscoveryService _appDiscovery;
    private bool _isRefreshingProviders;
    private string? _editingWorkflowId;
    private DateTime _editingCreatedAt;

    [ObservableProperty] private Workflow? _selectedWorkflow;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isCreatingNew = true;
    [ObservableProperty] private string? _editorError;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private WorkflowTemplate _editTemplate = WorkflowTemplate.CleanedText;
    [ObservableProperty] private WorkflowTriggerMode _editTriggerMode = WorkflowTriggerMode.Automatic;
    [ObservableProperty] private bool _editAppTriggerEnabled;
    [ObservableProperty] private bool _editWebsiteTriggerEnabled;
    [ObservableProperty] private bool _editHotkeyTriggerEnabled = true;
    [ObservableProperty] private WorkflowHotkeyBehavior _editHotkeyBehavior = WorkflowHotkeyBehavior.StartDictation;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _websitePatternInput = "";
    [ObservableProperty] private string _newHotkey = "";
    [ObservableProperty] private string? _editLanguage;
    [ObservableProperty] private string? _editTask;
    [ObservableProperty] private string? _editTranslationTarget;
    [ObservableProperty] private bool? _editWhisperModeOverride;
    [ObservableProperty] private string? _editTranscriptionModelOverride;
    [ObservableProperty] private string? _editProviderOverride;
    [ObservableProperty] private string _editFineTuning = "";
    [ObservableProperty] private string _editTranslationTargetLanguage = "";
    [ObservableProperty] private string _editCustomInstruction = "";
    [ObservableProperty] private string _editOutputFormat = "";
    [ObservableProperty] private bool _editAutoEnter;
    [ObservableProperty] private string? _editTargetActionPluginId;
    [ObservableProperty] private WorkflowNumberNormalizationMode _editNumberNormalizationMode = WorkflowNumberNormalizationMode.Inherit;
    [ObservableProperty] private bool _isAppPickerOpen;
    [ObservableProperty] private string _appPickerSearchText = "";
    [ObservableProperty] private string? _currentWebsiteDomain;

    /// <summary>
    /// Gets the configured workflows in display order.
    /// </summary>
    public ObservableCollection<Workflow> Workflows { get; } = [];
    /// <summary>
    /// Gets the filtered workflows.
    /// </summary>
    public ObservableCollection<Workflow> FilteredWorkflows { get; } = [];
    /// <summary>
    /// Gets the process name chips.
    /// </summary>
    public ObservableCollection<string> ProcessNameChips { get; } = [];
    /// <summary>
    /// Gets the website pattern chips.
    /// </summary>
    public ObservableCollection<string> WebsitePatternChips { get; } = [];

    /// <summary>
    /// Gets hotkeys configured for the workflow being edited.
    /// </summary>
    public ObservableCollection<string> EditHotkeys { get; } = [];

    /// <summary>
    /// Gets the app picker options.
    /// </summary>
    public ObservableCollection<WorkflowAppPickerOption> AppPickerOptions { get; } = [];
    /// <summary>
    /// Gets the domain suggestions.
    /// </summary>
    public ObservableCollection<WorkflowDomainSuggestionOption> DomainSuggestions { get; } = [];
    /// <summary>
    /// Gets the template options.
    /// </summary>
    public ObservableCollection<WorkflowTemplateOption> TemplateOptions { get; } = [];
    /// <summary>
    /// Gets the trigger mode options.
    /// </summary>
    public ObservableCollection<WorkflowTriggerModeOption> TriggerModeOptions { get; } = [];
    /// <summary>
    /// Gets the hotkey behavior options.
    /// </summary>
    public ObservableCollection<WorkflowHotkeyBehaviorOption> HotkeyBehaviorOptions { get; } = [];
    /// <summary>
    /// Gets the language options.
    /// </summary>
    public ObservableCollection<SettingOption> LanguageOptions { get; } = [];
    /// <summary>
    /// Gets the task options.
    /// </summary>
    public ObservableCollection<SettingOption> TaskOptions { get; } = [];
    /// <summary>
    /// Gets the translation target options.
    /// </summary>
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    /// <summary>
    /// Gets the number normalization options.
    /// </summary>
    public ObservableCollection<WorkflowNumberNormalizationOption> NumberNormalizationOptions { get; } = [];
    /// <summary>
    /// Gets the available model options.
    /// </summary>
    public ObservableCollection<ModelOption> AvailableModelOptions { get; } = [];
    /// <summary>
    /// Gets the available providers.
    /// </summary>
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    /// <summary>
    /// Gets the action plugin options.
    /// </summary>
    public ObservableCollection<ActionPluginOption> ActionPluginOptions { get; } = [];

    /// <summary>
    /// Gets the workflow count.
    /// </summary>
    public int WorkflowCount => Workflows.Count;
    /// <summary>
    /// Performs enabled workflow count.
    /// </summary>
    public int EnabledWorkflowCount => Workflows.Count(static workflow => workflow.IsEnabled);
    /// <summary>
    /// Performs workflow summary.
    /// </summary>
    public string WorkflowSummary => Loc.Instance.GetString("Workflows.SummaryFormat", WorkflowCount, EnabledWorkflowCount);
    /// <summary>
    /// Gets the editor title.
    /// </summary>
    public string EditorTitle => IsCreatingNew
        ? Loc.Instance["Workflows.NewTitle"]
        : Loc.Instance["Workflows.EditTitle"];
    /// <summary>
    /// Gets whether has workflows.
    /// </summary>
    public bool HasWorkflows => Workflows.Count > 0;
    /// <summary>
    /// Gets whether has filtered workflows.
    /// </summary>
    public bool HasFilteredWorkflows => FilteredWorkflows.Count > 0;
    /// <summary>
    /// Gets whether is automatic trigger mode selected.
    /// </summary>
    public bool IsAutomaticTriggerModeSelected => EditTriggerMode == WorkflowTriggerMode.Automatic;
    /// <summary>
    /// Gets whether is global trigger mode selected.
    /// </summary>
    public bool IsGlobalTriggerModeSelected => EditTriggerMode == WorkflowTriggerMode.Global;
    /// <summary>
    /// Gets whether is manual trigger mode selected.
    /// </summary>
    public bool IsManualTriggerModeSelected => EditTriggerMode == WorkflowTriggerMode.Manual;
    /// <summary>
    /// Gets whether show app trigger editor.
    /// </summary>
    public bool ShowAppTriggerEditor => IsAutomaticTriggerModeSelected && EditAppTriggerEnabled;
    /// <summary>
    /// Gets whether show website trigger editor.
    /// </summary>
    public bool ShowWebsiteTriggerEditor => IsAutomaticTriggerModeSelected && EditWebsiteTriggerEnabled;
    /// <summary>
    /// Gets whether show hotkey trigger editor.
    /// </summary>
    public bool ShowHotkeyTriggerEditor => IsAutomaticTriggerModeSelected && EditHotkeyTriggerEnabled;
    /// <summary>
    /// Gets whether is translation template.
    /// </summary>
    public bool IsTranslationTemplate => EditTemplate == WorkflowTemplate.Translation;
    /// <summary>
    /// Gets whether is custom template.
    /// </summary>
    public bool IsCustomTemplate => EditTemplate == WorkflowTemplate.Custom;
    /// <summary>
    /// Gets whether can change template.
    /// </summary>
    public bool CanChangeTemplate => IsCreatingNew;
    /// <summary>
    /// Returns whether editor error.
    /// </summary>
    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorError);
    /// <summary>
    /// Gets whether has app picker options.
    /// </summary>
    public bool HasAppPickerOptions => AppPickerOptions.Count > 0;
    /// <summary>
    /// Gets whether has domain suggestions.
    /// </summary>
    public bool HasDomainSuggestions => DomainSuggestions.Count > 0;
    /// <summary>
    /// Returns whether current website domain.
    /// </summary>
    public bool HasCurrentWebsiteDomain => !string.IsNullOrWhiteSpace(CurrentWebsiteDomain);

    /// <summary>
    /// Gets whether the workflow being edited has at least one hotkey.
    /// </summary>
    public bool HasEditHotkeys => EditHotkeys.Count > 0;

    /// <summary>
    /// Gets whether the selected transcription engine supports translation.
    /// </summary>
    public bool SupportsSelectedTranscriptionTranslation => SelectedTranscriptionEngineSupportsTranslation();
    /// <summary>
    /// Gets the selected template name.
    /// </summary>
    public string SelectedTemplateName => WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Name;
    /// <summary>
    /// Performs selected template description.
    /// </summary>
    public string SelectedTemplateDescription => WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Description;
    /// <summary>
    /// Performs template icon glyph.
    /// </summary>
    public string SelectedTemplateIconGlyph => TemplateIconGlyph(EditTemplate);
    /// <summary>
    /// Performs selected trigger glyph.
    /// </summary>
    public string SelectedTriggerIconGlyph => SelectedTriggerGlyph();
    /// <summary>
    /// Performs selected trigger text.
    /// </summary>
    public string SelectedTriggerLabel => SelectedTriggerText();
    /// <summary>
    /// Performs hotkey behavior description.
    /// </summary>
    public string SelectedHotkeyBehaviorDescription => HotkeyBehaviorDescription(EditHotkeyBehavior);
    /// <summary>
    /// Builds review text.
    /// </summary>
    public string ReviewText => BuildReviewText();

    /// <summary>
    /// Gets the default llm provider.
    /// </summary>
    public string? DefaultLlmProvider
    {
        get => _settings.Current.DefaultLlmProvider;
        set
        {
            if (string.Equals(_settings.Current.DefaultLlmProvider, value, StringComparison.Ordinal))
                return;

            _settings.Save(_settings.Current with { DefaultLlmProvider = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDefaultProvider));
            OnPropertyChanged(nameof(SelectedEditProvider));
        }
    }

    /// <summary>
    /// Gets the selected default provider.
    /// </summary>
    public ProviderOption? SelectedDefaultProvider
    {
        get => AvailableProviders.FirstOrDefault(option => option.Value == DefaultLlmProvider)
            ?? AvailableProviders.FirstOrDefault();
        set
        {
            if (_isRefreshingProviders)
                return;

            if (string.Equals(DefaultLlmProvider, value?.Value, StringComparison.Ordinal))
                return;

            DefaultLlmProvider = value?.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the selected edit provider.
    /// </summary>
    public ProviderOption? SelectedEditProvider
    {
        get => AvailableProviders.FirstOrDefault(option => option.Value == EditProviderOverride)
            ?? AvailableProviders.FirstOrDefault();
        set
        {
            if (string.Equals(EditProviderOverride, value?.Value, StringComparison.Ordinal))
                return;

            EditProviderOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Initializes a new instance of the WorkflowsViewModel class.
    /// </summary>
    public WorkflowsViewModel(
        IWorkflowService workflows,
        IActiveWindowService activeWindow,
        IHistoryService history,
        ISettingsService settings,
        PluginManager pluginManager,
        ModelManagerService modelManager,
        WindowsAppDiscoveryService appDiscovery)
    {
        _workflows = workflows;
        _activeWindow = activeWindow;
        _history = history;
        _settings = settings;
        _pluginManager = pluginManager;
        _modelManager = modelManager;
        _appDiscovery = appDiscovery;

        _workflows.WorkflowsChanged += RefreshWorkflows;
        EditHotkeys.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEditHotkeys));
            NotifyEditorStateChanged();
        };
        _history.RecordsChanged += RefreshDomainSuggestions;
        _pluginManager.PluginStateChanged += (_, _) => Application.Current?.Dispatcher.Invoke(() =>
        {
            RebuildProviderOptions();
            RebuildModelOptions();
            RebuildActionPluginOptions();
        });
        _settings.SettingsChanged += _ => Application.Current?.Dispatcher.Invoke(() =>
        {
            RebuildProviderOptions();
            OnPropertyChanged(nameof(DefaultLlmProvider));
        });

        BuildStaticOptions();
        RefreshWorkflows();
        RebuildProviderOptions();
        RebuildModelOptions();
        RebuildActionPluginOptions();
        StartCreate();
        IsEditorOpen = false;
    }

    [RelayCommand]
    private void StartCreate()
    {
        _editingWorkflowId = null;
        _editingCreatedAt = DateTime.UtcNow;
        IsCreatingNew = true;
        PopulateEditor(NewDraftWorkflow());
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void StartEdit(Workflow? workflow)
    {
        if (workflow is null) return;

        SelectedWorkflow = workflow;
        _editingWorkflowId = workflow.Id;
        _editingCreatedAt = workflow.CreatedAt;
        IsCreatingNew = false;
        PopulateEditor(workflow);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditorOpen = false;
        EditorError = null;
    }

    [RelayCommand]
    private void SaveEditor()
    {
        var validation = ValidateEditor();
        if (validation is not null)
        {
            EditorError = validation;
            return;
        }

        var workflow = BuildWorkflowFromEditor();
        if (IsCreatingNew)
            _workflows.AddWorkflow(workflow);
        else
            _workflows.UpdateWorkflow(workflow);

        EditorError = null;
        IsEditorOpen = false;
        SelectedWorkflow = _workflows.GetWorkflow(workflow.Id);
    }

    [RelayCommand]
    private void DeleteWorkflow(Workflow? workflow)
    {
        if (workflow is null) return;

        var result = MessageBox.Show(
            Loc.Instance.GetString("Workflows.DeleteConfirm", workflow.Name),
            Loc.Instance["Workflows.DeleteTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _workflows.DeleteWorkflow(workflow.Id);
        if (string.Equals(_editingWorkflowId, workflow.Id, StringComparison.Ordinal))
        {
            IsEditorOpen = false;
            EditorError = null;
            _editingWorkflowId = null;
        }

        if (SelectedWorkflow?.Id == workflow.Id)
            SelectedWorkflow = null;
    }

    [RelayCommand]
    private void ToggleWorkflow(Workflow? workflow)
    {
        if (workflow is null) return;
        _workflows.ToggleWorkflow(workflow.Id);
    }

    [RelayCommand]
    private void MoveUp(Workflow? workflow) => Move(workflow, -1);

    [RelayCommand]
    private void MoveDown(Workflow? workflow) => Move(workflow, 1);

    [RelayCommand]
    private void SelectTemplate(WorkflowTemplateOption? option)
    {
        if (option is null || !CanChangeTemplate)
            return;

        var currentName = EditName.Trim();
        var previousDefaultName = WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Name;
        EditTemplate = option.Template;

        if (string.IsNullOrWhiteSpace(currentName)
            || string.Equals(currentName, previousDefaultName, StringComparison.Ordinal))
        {
            EditName = WorkflowTemplateCatalog.DefinitionFor(option.Template).Name;
        }
    }

    [RelayCommand]
    private void SelectTriggerMode(WorkflowTriggerModeOption? option)
    {
        if (option is null)
            return;

        EditTriggerMode = option.Mode;
    }

    [RelayCommand]
    private void OpenAppPicker()
    {
        IsAppPickerOpen = true;
        RefreshAppPickerOptions(forceRefresh: true);
    }

    [RelayCommand]
    private void CloseAppPicker()
    {
        IsAppPickerOpen = false;
        AppPickerSearchText = "";
    }

    [RelayCommand]
    private void RefreshAppPicker()
    {
        RefreshAppPickerOptions(forceRefresh: true);
    }

    [RelayCommand]
    private void ToggleAppPickerOption(WorkflowAppPickerOption? option)
    {
        if (option is null)
            return;

        if (ProcessNameChips.Contains(option.ProcessName, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Remove(option.ProcessName);
        else
            ProcessNameChips.Add(option.ProcessName);

        RefreshAppPickerOptions();
        NotifyEditorStateChanged();
    }

    [RelayCommand]
    private void AddProcessNameChip()
    {
        var value = ProcessNameInput.Trim();
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!ProcessNameChips.Contains(value, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(value);
        ProcessNameInput = "";
        RefreshAppPickerOptions();
        NotifyEditorStateChanged();
    }

    [RelayCommand]
    private void RemoveProcessNameChip(string? value)
    {
        if (value is null) return;
        ProcessNameChips.Remove(value);
        RefreshAppPickerOptions();
        NotifyEditorStateChanged();
    }

    [RelayCommand]
    private void AddDomainSuggestion(WorkflowDomainSuggestionOption? option)
    {
        if (option is null)
            return;

        AddWebsitePattern(option.Domain);
    }

    [RelayCommand]
    private void AddCurrentWebsiteDomain()
    {
        RefreshCurrentWebsiteDomain();
        AddWebsitePattern(CurrentWebsiteDomain);
    }

    [RelayCommand]
    private void AddWebsitePatternChip()
    {
        AddWebsitePattern(WebsitePatternInput);
    }

    [RelayCommand]
    private void RemoveWebsitePatternChip(string? value)
    {
        if (value is null) return;
        WebsitePatternChips.Remove(value);
        RefreshDomainSuggestions();
        NotifyEditorStateChanged();
    }

    [RelayCommand]
    private void AddHotkey(string? value = null)
    {
        var hotkey = HotkeyParser.Normalize(value ?? NewHotkey);
        if (string.IsNullOrWhiteSpace(hotkey))
            return;

        if (EditHotkeys.Any(existing => string.Equals(HotkeyParser.Normalize(existing), hotkey, StringComparison.OrdinalIgnoreCase)))
        {
            EditorError = Loc.Instance.GetString("Workflows.ValidationHotkeyDuplicateFormat", hotkey);
            NewHotkey = "";
            return;
        }

        EditHotkeys.Add(hotkey);
        NewHotkey = "";
        RevalidateEditorError();
    }

    [RelayCommand]
    private void RemoveHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var index = EditHotkeys
            .Select((hotkey, idx) => new { Hotkey = hotkey, Index = idx })
            .FirstOrDefault(item => string.Equals(item.Hotkey, value, StringComparison.OrdinalIgnoreCase))
            ?.Index;
        if (index is null)
            return;

        EditHotkeys.RemoveAt(index.Value);
        RevalidateEditorError();
    }

    private void RevalidateEditorError()
    {
        if (!string.IsNullOrWhiteSpace(EditorError))
            EditorError = ValidateEditor();

        NotifyEditorStateChanged();
    }

    private void AddWebsitePattern(string? rawValue)
    {
        var value = NormalizeWebsitePattern(rawValue ?? "");
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!WebsitePatternChips.Contains(value, StringComparer.OrdinalIgnoreCase))
            WebsitePatternChips.Add(value);

        WebsitePatternInput = "";
        RefreshDomainSuggestions();
        NotifyEditorStateChanged();
    }

    private void Move(Workflow? workflow, int offset)
    {
        if (workflow is null) return;
        var orderedIds = Workflows.Select(w => w.Id).ToList();
        var idx = orderedIds.IndexOf(workflow.Id);
        var target = idx + offset;
        if (idx < 0 || target < 0 || target >= orderedIds.Count) return;

        (orderedIds[idx], orderedIds[target]) = (orderedIds[target], orderedIds[idx]);
        _workflows.Reorder(orderedIds);
    }

    private Workflow NewDraftWorkflow() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = WorkflowTemplateCatalog.DefinitionFor(WorkflowTemplate.CleanedText).Name,
        IsEnabled = true,
        SortOrder = _workflows.NextSortOrder(),
        Template = WorkflowTemplate.CleanedText,
        Trigger = WorkflowTrigger.Hotkey(),
        Behavior = new WorkflowBehavior { InputLanguage = "auto" },
        Output = new WorkflowOutput()
    };

    private void PopulateEditor(Workflow workflow)
    {
        EditorError = null;
        EditName = workflow.Name;
        EditIsEnabled = workflow.IsEnabled;
        EditTemplate = workflow.Template;
        PopulateTriggerEditor(workflow.Trigger);
        EditLanguage = workflow.Behavior.InputLanguage;
        EditTask = workflow.Behavior.SelectedTask;
        EditTranslationTarget = workflow.Behavior.TranslationTarget;
        EditWhisperModeOverride = workflow.Behavior.WhisperModeOverride;
        EditTranscriptionModelOverride = workflow.Behavior.TranscriptionModelOverride;
        EditProviderOverride = workflow.Behavior.ProviderOverride;
        EditFineTuning = workflow.Behavior.FineTuning;
        EditTranslationTargetLanguage = GetSetting(workflow, "targetLanguage") ?? GetSetting(workflow, "target") ?? workflow.Behavior.TranslationTarget ?? "";
        EditCustomInstruction = GetSetting(workflow, "instruction") ?? GetSetting(workflow, "goal") ?? GetSetting(workflow, "prompt") ?? "";
        EditOutputFormat = workflow.Output.Format ?? "";
        EditAutoEnter = workflow.Output.AutoEnter;
        EditTargetActionPluginId = workflow.Output.TargetActionPluginId;
        EditNumberNormalizationMode = workflow.Output.NumberNormalizationMode;
        NewHotkey = "";

        ReplaceCollection(ProcessNameChips, workflow.Trigger.ProcessNames);
        ReplaceCollection(WebsitePatternChips, workflow.Trigger.WebsitePatterns);
        ReplaceCollection(EditHotkeys, NormalizeHotkeyList(workflow.Trigger.Hotkeys));
        RefreshCurrentWebsiteDomain();
        RefreshAppPickerOptions();
        RefreshDomainSuggestions();
        NotifyEditorStateChanged();
    }

    private Workflow BuildWorkflowFromEditor()
    {
        var id = IsCreatingNew || _editingWorkflowId is null ? Guid.NewGuid().ToString() : _editingWorkflowId;
        var template = IsCreatingNew ? EditTemplate : SelectedWorkflow?.Template ?? EditTemplate;
        return new Workflow
        {
            Id = id,
            Name = ResolvedEditName(),
            IsEnabled = EditIsEnabled,
            SortOrder = IsCreatingNew ? _workflows.NextSortOrder() : SelectedWorkflow?.SortOrder ?? _workflows.NextSortOrder(),
            Template = template,
            Trigger = BuildTrigger(),
            Behavior = BuildBehavior(template),
            Output = new WorkflowOutput
            {
                Format = string.IsNullOrWhiteSpace(EditOutputFormat) ? null : EditOutputFormat.Trim(),
                AutoEnter = EditAutoEnter,
                TargetActionPluginId = string.IsNullOrWhiteSpace(EditTargetActionPluginId) ? null : EditTargetActionPluginId,
                NumberNormalizationModeRaw = EditNumberNormalizationMode.ToRawValue()
            },
            CreatedAt = IsCreatingNew ? DateTime.UtcNow : _editingCreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private WorkflowTrigger BuildTrigger() => EditTriggerMode switch
    {
        WorkflowTriggerMode.Global => WorkflowTrigger.Global(),
        WorkflowTriggerMode.Manual => WorkflowTrigger.Manual(),
        _ => BuildAutomaticTrigger()
    };

    private WorkflowTrigger BuildAutomaticTrigger()
    {
        IReadOnlyList<string> processNames = EditAppTriggerEnabled ? [.. ProcessNameChips] : [];
        IReadOnlyList<string> websitePatterns = EditWebsiteTriggerEnabled ? [.. WebsitePatternChips] : [];
        IReadOnlyList<string> hotkeys = EditHotkeyTriggerEnabled ? NormalizeHotkeyList(EditHotkeys) : [];

        var kind = processNames.Count > 0
            ? WorkflowTriggerKind.App
            : websitePatterns.Count > 0
                ? WorkflowTriggerKind.Website
                : WorkflowTriggerKind.Hotkey;

        return new WorkflowTrigger
        {
            Kind = kind,
            ProcessNames = processNames,
            WebsitePatterns = websitePatterns,
            Hotkeys = hotkeys,
            HotkeyBehavior = EditHotkeyBehavior
        };
    }

    private void PopulateTriggerEditor(WorkflowTrigger trigger)
    {
        EditTriggerMode = trigger.Kind switch
        {
            WorkflowTriggerKind.Global => WorkflowTriggerMode.Global,
            WorkflowTriggerKind.Manual => WorkflowTriggerMode.Manual,
            _ => WorkflowTriggerMode.Automatic
        };

        var hasAutomaticValues = trigger.HasAutomaticValues;
        EditAppTriggerEnabled = trigger.HasAppBindings || (!hasAutomaticValues && trigger.Kind == WorkflowTriggerKind.App);
        EditWebsiteTriggerEnabled = trigger.HasWebsiteBindings || (!hasAutomaticValues && trigger.Kind == WorkflowTriggerKind.Website);
        EditHotkeyTriggerEnabled = trigger.HasHotkeyBindings || (!hasAutomaticValues && trigger.Kind == WorkflowTriggerKind.Hotkey);
        EditHotkeyBehavior = trigger.HotkeyBehavior;

        if (EditTriggerMode == WorkflowTriggerMode.Automatic
            && !EditAppTriggerEnabled
            && !EditWebsiteTriggerEnabled
            && !EditHotkeyTriggerEnabled)
        {
            EditHotkeyTriggerEnabled = true;
        }
    }

    private WorkflowBehavior BuildBehavior(WorkflowTemplate template)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (template == WorkflowTemplate.Translation && !string.IsNullOrWhiteSpace(EditTranslationTargetLanguage))
            settings["targetLanguage"] = EditTranslationTargetLanguage.Trim();
        if (template == WorkflowTemplate.Custom && !string.IsNullOrWhiteSpace(EditCustomInstruction))
            settings["instruction"] = EditCustomInstruction.Trim();

        return new WorkflowBehavior
        {
            Settings = settings,
            FineTuning = EditFineTuning.Trim(),
            ProviderOverride = string.IsNullOrWhiteSpace(EditProviderOverride) ? null : EditProviderOverride,
            ModelOverride = ParseModelOverride(EditProviderOverride),
            InputLanguage = string.IsNullOrWhiteSpace(EditLanguage) ? null : EditLanguage,
            SelectedTask = string.IsNullOrWhiteSpace(EditTask) ? null : EditTask,
            TranslationTarget = string.IsNullOrWhiteSpace(EditTranslationTarget) ? null : EditTranslationTarget,
            WhisperModeOverride = EditWhisperModeOverride,
            TranscriptionModelOverride = string.IsNullOrWhiteSpace(EditTranscriptionModelOverride) ? null : EditTranscriptionModelOverride
        };
    }

    private string? ValidateEditor()
    {
        switch (EditTriggerMode)
        {
            case WorkflowTriggerMode.Automatic when !EditAppTriggerEnabled && !EditWebsiteTriggerEnabled && !EditHotkeyTriggerEnabled:
                return Loc.Instance["Workflows.ValidationAutomatic"];
            case WorkflowTriggerMode.Automatic when EditAppTriggerEnabled && ProcessNameChips.Count == 0:
                return Loc.Instance["Workflows.ValidationApp"];
            case WorkflowTriggerMode.Automatic when EditWebsiteTriggerEnabled && WebsitePatternChips.Count == 0:
                return Loc.Instance["Workflows.ValidationWebsite"];
            case WorkflowTriggerMode.Automatic when EditHotkeyTriggerEnabled && NormalizeHotkeyList(EditHotkeys).Count == 0:
                return Loc.Instance["Workflows.ValidationHotkey"];
        }

        if (EditTriggerMode == WorkflowTriggerMode.Automatic && EditHotkeyTriggerEnabled)
        {
            var hotkeyValidation = ValidateHotkeys();
            if (hotkeyValidation is not null)
                return hotkeyValidation;
        }

        if (EditTemplate == WorkflowTemplate.Translation && string.IsNullOrWhiteSpace(EditTranslationTargetLanguage))
            return Loc.Instance["Workflows.ValidationTranslation"];

        if (EditTemplate == WorkflowTemplate.Custom
            && string.IsNullOrWhiteSpace(EditCustomInstruction)
            && string.IsNullOrWhiteSpace(EditFineTuning))
            return Loc.Instance["Workflows.ValidationCustom"];

        return null;
    }

    private string BuildReviewText()
    {
        var name = ResolvedEditName();
        var templateName = WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Name;
        var trigger = BuildTriggerReviewText();

        return Loc.Instance.GetString("Workflows.ReviewFormat", name, templateName, trigger);
    }

    private string BuildTriggerReviewText()
    {
        if (EditTriggerMode == WorkflowTriggerMode.Global)
            return TriggerModeDisplayName(WorkflowTriggerMode.Global);

        if (EditTriggerMode == WorkflowTriggerMode.Manual)
            return TriggerModeDisplayName(WorkflowTriggerMode.Manual);

        var parts = new List<string>();

        if (EditAppTriggerEnabled)
            parts.Add(ProcessNameChips.Count == 0 ? TriggerDisplayName(WorkflowTriggerKind.App) : string.Join(", ", ProcessNameChips));

        if (EditWebsiteTriggerEnabled)
            parts.Add(WebsitePatternChips.Count == 0 ? TriggerDisplayName(WorkflowTriggerKind.Website) : string.Join(", ", WebsitePatternChips));

        if (EditHotkeyTriggerEnabled)
        {
            var hotkeys = NormalizeHotkeyList(EditHotkeys);
            var hotkeyText = hotkeys.Count == 0
                ? TriggerDisplayName(WorkflowTriggerKind.Hotkey)
                : string.Join(", ", hotkeys);
            parts.Add(EditHotkeyBehavior == WorkflowHotkeyBehavior.ProcessSelectedText
                ? Loc.Instance.GetString("Workflows.ReviewHotkeyProcessFormat", hotkeyText)
                : Loc.Instance.GetString("Workflows.ReviewHotkeyDictationFormat", hotkeyText));
        }

        return parts.Count == 0
            ? TriggerModeDisplayName(WorkflowTriggerMode.Automatic)
            : string.Join(" + ", parts);
    }

    private string ResolvedEditName()
    {
        var trimmedName = EditName.Trim();
        return string.IsNullOrWhiteSpace(trimmedName)
            ? WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Name
            : trimmedName;
    }

    private void RefreshWorkflows()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ReplaceCollection(Workflows, _workflows.Workflows);
            RefreshFilteredWorkflows();
            NotifyWorkflowStateChanged();
        });
    }

    private void RefreshFilteredWorkflows()
    {
        var query = SearchText.Trim();
        var source = string.IsNullOrWhiteSpace(query)
            ? Workflows
            : Workflows.Where(workflow =>
                workflow.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || workflow.Definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || WorkflowTriggerDetail(workflow).Contains(query, StringComparison.OrdinalIgnoreCase));

        ReplaceCollection(FilteredWorkflows, source);
        NotifyWorkflowStateChanged();
    }

    private void RefreshAppPickerOptions(bool forceRefresh = false)
    {
        if (!IsAppPickerOpen)
        {
            ReplaceCollection(AppPickerOptions, []);
            OnPropertyChanged(nameof(HasAppPickerOptions));
            return;
        }

        var query = AppPickerSearchText.Trim();
        var selected = new HashSet<string>(ProcessNameChips, StringComparer.OrdinalIgnoreCase);
        var apps = _appDiscovery.GetApps(forceRefresh)
            .Where(app => string.IsNullOrWhiteSpace(query)
                          || app.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || app.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(app => selected.Contains(app.ProcessName))
            .ThenBy(app => SourceRank(app.Source))
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(14)
            .Select(app => new WorkflowAppPickerOption(
                app.ProcessName,
                app.DisplayName,
                AppSourceLabel(app.Source),
                app.Icon,
                selected.Contains(app.ProcessName)));

        ReplaceCollection(AppPickerOptions, apps);
        OnPropertyChanged(nameof(HasAppPickerOptions));
    }

    private void RefreshCurrentWebsiteDomain()
    {
        CurrentWebsiteDomain = NormalizeWebsitePattern(_activeWindow.GetBrowserUrl() ?? "");
    }

    private void RefreshDomainSuggestions()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RefreshCurrentWebsiteDomain();

            var selected = new HashSet<string>(WebsitePatternChips, StringComparer.OrdinalIgnoreCase);
            var query = NormalizeWebsitePattern(WebsitePatternInput);
            var suggestions = BuildDomainSuggestionOptions(query, selected);
            ReplaceCollection(DomainSuggestions, suggestions);
            OnPropertyChanged(nameof(HasDomainSuggestions));
            OnPropertyChanged(nameof(HasCurrentWebsiteDomain));
        });
    }

    private IReadOnlyList<WorkflowDomainSuggestionOption> BuildDomainSuggestionOptions(
        string query,
        HashSet<string> selected)
    {
        var suggestions = new List<WorkflowDomainSuggestionOption>();
        if (!string.IsNullOrWhiteSpace(CurrentWebsiteDomain)
            && !selected.Contains(CurrentWebsiteDomain)
            && MatchesDomainQuery(CurrentWebsiteDomain, query))
        {
            suggestions.Add(new WorkflowDomainSuggestionOption(
                CurrentWebsiteDomain,
                Loc.Instance["Workflows.DomainCurrent"],
                true));
        }

        var historyDomains = _history.Records
            .Select(record => NormalizeWebsitePattern(record.AppUrl ?? ""))
            .Concat(_workflows.Workflows.SelectMany(workflow => workflow.Trigger.WebsitePatterns))
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(domain => !selected.Contains(domain) && MatchesDomainQuery(domain, query))
            .Where(domain => !suggestions.Any(option => string.Equals(option.Domain, domain, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(domain => new WorkflowDomainSuggestionOption(
                domain,
                Loc.Instance["Workflows.DomainRecent"],
                false));

        suggestions.AddRange(historyDomains);
        return suggestions;
    }

    private void BuildStaticOptions()
    {
        ReplaceCollection(TemplateOptions, WorkflowTemplateCatalog.All.Select(definition =>
            new WorkflowTemplateOption(
                definition.Template,
                definition.Name,
                definition.Description,
                TemplateIconGlyph(definition.Template))));
        ReplaceCollection(TriggerModeOptions,
        [
            new WorkflowTriggerModeOption(WorkflowTriggerMode.Automatic, TriggerModeDisplayName(WorkflowTriggerMode.Automatic), TriggerModeIconGlyph(WorkflowTriggerMode.Automatic)),
            new WorkflowTriggerModeOption(WorkflowTriggerMode.Global, TriggerModeDisplayName(WorkflowTriggerMode.Global), TriggerModeIconGlyph(WorkflowTriggerMode.Global)),
            new WorkflowTriggerModeOption(WorkflowTriggerMode.Manual, TriggerModeDisplayName(WorkflowTriggerMode.Manual), TriggerModeIconGlyph(WorkflowTriggerMode.Manual))
        ]);
        ReplaceCollection(HotkeyBehaviorOptions,
        [
            new WorkflowHotkeyBehaviorOption(
                WorkflowHotkeyBehavior.StartDictation,
                Loc.Instance["Workflows.HotkeyBehaviorStartDictation"]),
            new WorkflowHotkeyBehaviorOption(
                WorkflowHotkeyBehavior.ProcessSelectedText,
                Loc.Instance["Workflows.HotkeyBehaviorProcessSelectedText"])
        ]);
        ReplaceCollection(LanguageOptions,
        [
            new SettingOption(null, Loc.Instance.GetString("Workflows.LanguageGlobalFormat", LanguageDisplayName(_settings.Current.Language))),
            new SettingOption("auto", Loc.Instance["Workflows.LanguageAuto"]),
            new SettingOption("de", "Deutsch"),
            new SettingOption("en", "English"),
            new SettingOption("fr", "Francais"),
            new SettingOption("es", "Espanol")
        ]);
        ReplaceCollection(TaskOptions,
        [
            new SettingOption(null, Loc.Instance["Workflows.TaskGlobal"]),
            new SettingOption("transcribe", Loc.Instance["Workflows.TaskTranscribe"])
        ]);
        RebuildTaskOptions();
        ReplaceCollection(NumberNormalizationOptions,
        [
            new WorkflowNumberNormalizationOption(
                WorkflowNumberNormalizationMode.Inherit,
                Loc.Instance["Workflows.NumberFormattingGlobal"]),
            new WorkflowNumberNormalizationOption(
                WorkflowNumberNormalizationMode.Enabled,
                Loc.Instance["Workflows.NumberFormattingOn"]),
            new WorkflowNumberNormalizationOption(
                WorkflowNumberNormalizationMode.Disabled,
                Loc.Instance["Workflows.NumberFormattingOff"])
        ]);
        ReplaceCollection(TranslationTargetOptions, TranslationModelInfo.ProfileTargetOptions);
    }

    private void RebuildProviderOptions()
    {
        var explicitOptions = new List<ProviderOption>();
        foreach (var provider in _pluginManager.LlmProviders.Where(p => p.IsAvailable))
        {
            var plugin = _pluginManager.AllPlugins.FirstOrDefault(p => p.Instance == provider);
            if (plugin is null) continue;

            foreach (var model in provider.SupportedModels)
                explicitOptions.Add(new ProviderOption(
                    $"plugin:{plugin.Manifest.Id}:{model.Id}",
                    $"{provider.ProviderName} / {model.DisplayName}"));
        }

        _isRefreshingProviders = true;
        AvailableProviders.Clear();
        AvailableProviders.Add(new ProviderOption(null, GetDefaultProviderLabel(explicitOptions)));
        foreach (var option in explicitOptions)
            AvailableProviders.Add(option);
        _isRefreshingProviders = false;
        OnPropertyChanged(nameof(SelectedDefaultProvider));
        OnPropertyChanged(nameof(SelectedEditProvider));
    }

    private string GetDefaultProviderLabel(IReadOnlyList<ProviderOption> explicitOptions)
    {
        var configuredDefault = _settings.Current.DefaultLlmProvider;
        if (!string.IsNullOrWhiteSpace(configuredDefault))
        {
            var configuredOption = explicitOptions.FirstOrDefault(option =>
                string.Equals(option.Value, configuredDefault, StringComparison.Ordinal));
            if (configuredOption is not null)
                return Loc.Instance.GetString("Workflows.DefaultProviderLabelFormat", configuredOption.DisplayName);
        }

        var fallbackOption = explicitOptions.FirstOrDefault();
        return fallbackOption is null
            ? Loc.Instance["Workflows.DefaultProviderLabelNone"]
            : Loc.Instance.GetString("Workflows.DefaultProviderLabelAutoFormat", fallbackOption.DisplayName);
    }

    private void RebuildModelOptions()
    {
        var selected = EditTranscriptionModelOverride;
        AvailableModelOptions.Clear();
        AvailableModelOptions.Add(new ModelOption(null, Loc.Instance["Workflows.GlobalDefault"]));
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
                AvailableModelOptions.Add(new ModelOption(
                    ModelManagerService.GetPluginModelId(engine.PluginId, model.Id),
                    $"{engine.ProviderDisplayName}: {model.DisplayName}"));
        }
        EditTranscriptionModelOverride = selected;
        RebuildTaskOptions();
    }

    private void RebuildTaskOptions()
    {
        var selected = EditTask;
        var options = new List<SettingOption>
        {
            new(null, Loc.Instance["Workflows.TaskGlobal"]),
            new("transcribe", Loc.Instance["Workflows.TaskTranscribe"])
        };

        if (SelectedTranscriptionEngineSupportsTranslation())
            options.Add(new SettingOption("translate", Loc.Instance["Workflows.TaskTranslate"]));
        else if (string.Equals(selected, "translate", StringComparison.OrdinalIgnoreCase))
            selected = null;

        ReplaceCollection(TaskOptions, options);
        EditTask = selected;
        OnPropertyChanged(nameof(SupportsSelectedTranscriptionTranslation));
    }

    private void RebuildActionPluginOptions()
    {
        var selected = EditTargetActionPluginId;
        ActionPluginOptions.Clear();
        ActionPluginOptions.Add(new ActionPluginOption(null, Loc.Instance["Workflows.OutputDefault"]));
        foreach (var plugin in _pluginManager.ActionPlugins)
            ActionPluginOptions.Add(new ActionPluginOption(plugin.PluginId, plugin.ActionName));
        EditTargetActionPluginId = selected;
    }

    private static string? ParseModelOverride(string? pluginModelId)
    {
        var parts = pluginModelId?.Split(':', 3);
        return parts is { Length: 3 } && parts[0] == "plugin" ? parts[2] : null;
    }

    private static string? GetSetting(Workflow workflow, string key) =>
        workflow.Behavior.Settings.TryGetValue(key, out var value) ? value : null;

    private string? ValidateHotkeys()
    {
        var hotkeys = NormalizeHotkeyList(EditHotkeys);
        var duplicate = hotkeys
            .GroupBy(static hotkey => hotkey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (!string.IsNullOrWhiteSpace(duplicate))
            return Loc.Instance.GetString("Workflows.ValidationHotkeyDuplicateFormat", duplicate);

        foreach (var hotkey in hotkeys)
        {
            if (FindAppHotkeyConflict(hotkey) is { } appConflict)
                return Loc.Instance.GetString("Workflows.ValidationHotkeyAppConflictFormat", hotkey, appConflict);

            if (FindWorkflowHotkeyConflict(hotkey) is { } workflowName)
                return Loc.Instance.GetString("Workflows.ValidationHotkeyWorkflowConflictFormat", hotkey, workflowName);
        }

        return null;
    }

    private string? FindAppHotkeyConflict(string hotkey)
    {
        foreach (var (configuredHotkey, label) in AppHotkeyConflicts())
        {
            if (string.Equals(configuredHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        return null;
    }

    private IEnumerable<(string Hotkey, string Label)> AppHotkeyConflicts()
    {
        var settings = _settings.Current;
        var candidates = new (IEnumerable<string> Hotkeys, string Label)[]
        {
            (settings.GetMainDictationHotkeys(), Loc.Instance["Workflows.AppHotkeyMainDictation"]),
            (settings.GetToggleOnlyHotkeys(), Loc.Instance["Workflows.AppHotkeyToggleOnly"]),
            (settings.GetHoldOnlyHotkeys(), Loc.Instance["Workflows.AppHotkeyHoldOnly"]),
            (settings.GetRecentTranscriptionsHotkeys(), Loc.Instance["Workflows.AppHotkeyRecentTranscriptions"]),
            (settings.GetCopyLastTranscriptionHotkeys(), Loc.Instance["Workflows.AppHotkeyCopyLastTranscription"]),
            (settings.GetWorkflowPaletteHotkeys(), Loc.Instance["Workflows.AppHotkeyWorkflowPalette"])
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (configuredHotkeys, label) in candidates)
        {
            foreach (var configuredHotkey in configuredHotkeys)
            {
                var normalized = HotkeyParser.Normalize(configuredHotkey);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    continue;

                yield return (normalized, label);
            }
        }
    }

    private string? FindWorkflowHotkeyConflict(string hotkey)
    {
        foreach (var workflow in _workflows.Workflows)
        {
            if (string.Equals(workflow.Id, _editingWorkflowId, StringComparison.Ordinal))
                continue;

            if (workflow.Trigger.Hotkeys
                .Select(HotkeyParser.Normalize)
                .Any(existing => string.Equals(existing, hotkey, StringComparison.OrdinalIgnoreCase)))
            {
                return workflow.Name;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> NormalizeHotkeyList(IEnumerable<string> hotkeys) =>
        hotkeys
            .Select(HotkeyParser.Normalize)
            .Where(static hotkey => !string.IsNullOrWhiteSpace(hotkey))
            .ToList();

    private static int SourceRank(WindowsAppDiscoverySource source) => source switch
    {
        WindowsAppDiscoverySource.Running => 0,
        WindowsAppDiscoverySource.Installed => 1,
        WindowsAppDiscoverySource.History => 2,
        _ => 3
    };

    private static string AppSourceLabel(WindowsAppDiscoverySource source) => source switch
    {
        WindowsAppDiscoverySource.Running => Loc.Instance["Workflows.AppSourceRunning"],
        WindowsAppDiscoverySource.Installed => Loc.Instance["Workflows.AppSourceInstalled"],
        WindowsAppDiscoverySource.History => Loc.Instance["Workflows.AppSourceRecent"],
        _ => ""
    };

    private static bool MatchesDomainQuery(string domain, string query) =>
        string.IsNullOrWhiteSpace(query) || domain.Contains(query, StringComparison.OrdinalIgnoreCase);

    private bool SelectedTranscriptionEngineSupportsTranslation()
    {
        var modelId = string.IsNullOrWhiteSpace(EditTranscriptionModelOverride)
            ? _settings.Current.SelectedModelId
            : EditTranscriptionModelOverride;

        if (string.IsNullOrWhiteSpace(modelId) || !ModelManagerService.IsPluginModel(modelId))
            return false;

        try
        {
            var (pluginId, _) = ModelManagerService.ParsePluginModelId(modelId);
            return _modelManager.PluginManager.TranscriptionEngines
                .FirstOrDefault(engine => string.Equals(engine.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                ?.SupportsTranslation == true;
        }
        catch
        {
            return false;
        }
    }

    private static string LanguageDisplayName(string? language) => language?.ToLowerInvariant() switch
    {
        "auto" or null or "" => Loc.Instance["Workflows.LanguageAuto"],
        "de" => "Deutsch",
        "en" => "English",
        "fr" => "Francais",
        "es" => "Espanol",
        _ => language
    };

    private static string NormalizeWebsitePattern(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            trimmed = uri.Host;
        else if (trimmed.Contains('/'))
            trimmed = trimmed.Split('/')[0];

        return trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Gets the trigger mode display name.
    /// </summary>
    public static string TriggerModeDisplayName(WorkflowTriggerMode mode) => mode switch
    {
        WorkflowTriggerMode.Automatic => Loc.Instance["Workflows.TriggerAutomatic"],
        WorkflowTriggerMode.Global => Loc.Instance["Workflows.TriggerAlways"],
        WorkflowTriggerMode.Manual => Loc.Instance["Workflows.TriggerManual"],
        _ => ""
    };

    /// <summary>
    /// Gets the trigger display name.
    /// </summary>
    public static string TriggerDisplayName(WorkflowTriggerKind kind) => kind switch
    {
        WorkflowTriggerKind.App => Loc.Instance["Workflows.TriggerApp"],
        WorkflowTriggerKind.Website => Loc.Instance["Workflows.TriggerWebsite"],
        WorkflowTriggerKind.Hotkey => Loc.Instance["Workflows.TriggerHotkey"],
        WorkflowTriggerKind.Global => Loc.Instance["Workflows.TriggerAlways"],
        WorkflowTriggerKind.Manual => Loc.Instance["Workflows.TriggerManual"],
        _ => ""
    };

    /// <summary>
    /// Performs workflow trigger summary.
    /// </summary>
    public static string WorkflowTriggerSummary(Workflow workflow)
    {
        var trigger = workflow.Trigger;
        if (trigger.Kind == WorkflowTriggerKind.Global)
            return TriggerDisplayName(WorkflowTriggerKind.Global);

        if (trigger.Kind == WorkflowTriggerKind.Manual)
            return TriggerDisplayName(WorkflowTriggerKind.Manual);

        var parts = AutomaticTriggerComponents(trigger)
            .Select(TriggerDisplayName)
            .ToList();

        return parts.Count == 0
            ? TriggerModeDisplayName(WorkflowTriggerMode.Automatic)
            : string.Join(" + ", parts);
    }

    /// <summary>
    /// Performs workflow trigger detail.
    /// </summary>
    public static string WorkflowTriggerDetail(Workflow workflow)
    {
        var trigger = workflow.Trigger;
        if (trigger.Kind == WorkflowTriggerKind.Global)
            return Loc.Instance["Workflows.TriggerAlwaysDetail"];

        if (trigger.Kind == WorkflowTriggerKind.Manual)
            return Loc.Instance["Workflows.TriggerManualDetail"];

        var parts = new List<string>();
        if (trigger.HasAppBindings)
            parts.Add(string.Join(", ", trigger.ProcessNames));
        if (trigger.HasWebsiteBindings)
            parts.Add(string.Join(", ", trigger.WebsitePatterns));
        if (trigger.HasHotkeyBindings)
        {
            var hotkeys = string.Join(", ", trigger.Hotkeys);
            parts.Add(trigger.HotkeyBehavior == WorkflowHotkeyBehavior.ProcessSelectedText
                ? Loc.Instance.GetString("Workflows.TriggerHotkeyProcessDetailFormat", hotkeys)
                : Loc.Instance.GetString("Workflows.TriggerHotkeyDictationDetailFormat", hotkeys));
        }

        return string.Join(" · ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// Performs template icon glyph.
    /// </summary>
    public static string TemplateIconGlyph(WorkflowTemplate template) => template switch
    {
        WorkflowTemplate.CleanedText => "\uE8D2",
        WorkflowTemplate.Translation => "\uE774",
        WorkflowTemplate.EmailReply => "\uE715",
        WorkflowTemplate.MeetingNotes => "\uE8A5",
        WorkflowTemplate.Checklist => "\uE9D5",
        WorkflowTemplate.Json => "\uE8A5",
        WorkflowTemplate.Summary => "\uE8FD",
        WorkflowTemplate.Custom => "\uE713",
        _ => "\uE8D2"
    };

    /// <summary>
    /// Performs trigger icon glyph.
    /// </summary>
    public static string TriggerIconGlyph(WorkflowTriggerKind kind) => kind switch
    {
        WorkflowTriggerKind.App => "\uE71D",
        WorkflowTriggerKind.Website => "\uE774",
        WorkflowTriggerKind.Hotkey => "\uE765",
        WorkflowTriggerKind.Global => "\uE909",
        WorkflowTriggerKind.Manual => "\uE70F",
        _ => "\uE8F1"
    };

    /// <summary>
    /// Performs trigger mode icon glyph.
    /// </summary>
    public static string TriggerModeIconGlyph(WorkflowTriggerMode mode) => mode switch
    {
        WorkflowTriggerMode.Automatic => "\uE8B2",
        WorkflowTriggerMode.Global => TriggerIconGlyph(WorkflowTriggerKind.Global),
        WorkflowTriggerMode.Manual => TriggerIconGlyph(WorkflowTriggerKind.Manual),
        _ => "\uE8F1"
    };

    /// <summary>
    /// Performs hotkey behavior description.
    /// </summary>
    public static string HotkeyBehaviorDescription(WorkflowHotkeyBehavior behavior) => behavior switch
    {
        WorkflowHotkeyBehavior.StartDictation => Loc.Instance["Workflows.HotkeyBehaviorStartDictationHint"],
        WorkflowHotkeyBehavior.ProcessSelectedText => Loc.Instance["Workflows.HotkeyBehaviorProcessSelectedTextHint"],
        _ => ""
    };

    private string SelectedTriggerGlyph()
    {
        if (EditTriggerMode == WorkflowTriggerMode.Global)
            return TriggerModeIconGlyph(WorkflowTriggerMode.Global);

        if (EditTriggerMode == WorkflowTriggerMode.Manual)
            return TriggerModeIconGlyph(WorkflowTriggerMode.Manual);

        if (EditAppTriggerEnabled)
            return TriggerIconGlyph(WorkflowTriggerKind.App);

        if (EditWebsiteTriggerEnabled)
            return TriggerIconGlyph(WorkflowTriggerKind.Website);

        if (EditHotkeyTriggerEnabled)
            return TriggerIconGlyph(WorkflowTriggerKind.Hotkey);

        return TriggerModeIconGlyph(WorkflowTriggerMode.Automatic);
    }

    private string SelectedTriggerText()
    {
        if (EditTriggerMode != WorkflowTriggerMode.Automatic)
            return TriggerModeDisplayName(EditTriggerMode);

        var parts = new List<string>();
        if (EditAppTriggerEnabled)
            parts.Add(TriggerDisplayName(WorkflowTriggerKind.App));
        if (EditWebsiteTriggerEnabled)
            parts.Add(TriggerDisplayName(WorkflowTriggerKind.Website));
        if (EditHotkeyTriggerEnabled)
            parts.Add(TriggerDisplayName(WorkflowTriggerKind.Hotkey));

        return parts.Count == 0
            ? TriggerModeDisplayName(WorkflowTriggerMode.Automatic)
            : string.Join(" + ", parts);
    }

    private static IReadOnlyList<WorkflowTriggerKind> AutomaticTriggerComponents(WorkflowTrigger trigger)
    {
        var parts = new List<WorkflowTriggerKind>();
        if (trigger.HasAppBindings)
            parts.Add(WorkflowTriggerKind.App);
        if (trigger.HasWebsiteBindings)
            parts.Add(WorkflowTriggerKind.Website);
        if (trigger.HasHotkeyBindings)
            parts.Add(WorkflowTriggerKind.Hotkey);
        return parts;
    }

    partial void OnSearchTextChanged(string value) => RefreshFilteredWorkflows();
    partial void OnEditTemplateChanged(WorkflowTemplate value)
    {
        if (value == WorkflowTemplate.Translation && string.IsNullOrWhiteSpace(EditTranslationTargetLanguage))
            EditTranslationTargetLanguage = "English";
        if (value != WorkflowTemplate.Custom)
            EditCustomInstruction = "";
        OnPropertyChanged(nameof(SelectedTemplateName));
        OnPropertyChanged(nameof(SelectedTemplateDescription));
        OnPropertyChanged(nameof(SelectedTemplateIconGlyph));
        NotifyEditorStateChanged();
    }

    partial void OnEditTriggerModeChanged(WorkflowTriggerMode value)
    {
        OnPropertyChanged(nameof(SelectedTriggerIconGlyph));
        OnPropertyChanged(nameof(SelectedTriggerLabel));
        if (value == WorkflowTriggerMode.Automatic && EditAppTriggerEnabled)
            RefreshAppPickerOptions();
        if (value == WorkflowTriggerMode.Automatic && EditWebsiteTriggerEnabled)
            RefreshDomainSuggestions();
        NotifyEditorStateChanged();
    }
    partial void OnEditAppTriggerEnabledChanged(bool value)
    {
        if (value)
            RefreshAppPickerOptions();
        else
            IsAppPickerOpen = false;

        NotifyEditorStateChanged();
    }
    partial void OnEditWebsiteTriggerEnabledChanged(bool value)
    {
        if (value)
            RefreshDomainSuggestions();
        NotifyEditorStateChanged();
    }
    partial void OnEditHotkeyTriggerEnabledChanged(bool value) => NotifyEditorStateChanged();
    partial void OnEditHotkeyBehaviorChanged(WorkflowHotkeyBehavior value)
    {
        OnPropertyChanged(nameof(SelectedHotkeyBehaviorDescription));
        NotifyEditorStateChanged();
    }
    partial void OnAppPickerSearchTextChanged(string value) => RefreshAppPickerOptions();
    partial void OnWebsitePatternInputChanged(string value) => RefreshDomainSuggestions();
    partial void OnCurrentWebsiteDomainChanged(string? value) => OnPropertyChanged(nameof(HasCurrentWebsiteDomain));
    partial void OnEditNameChanged(string value) => NotifyEditorStateChanged();
    partial void OnEditTranscriptionModelOverrideChanged(string? value) => RebuildTaskOptions();
    partial void OnEditTranslationTargetLanguageChanged(string value) => NotifyEditorStateChanged();
    partial void OnEditCustomInstructionChanged(string value) => NotifyEditorStateChanged();
    partial void OnEditNumberNormalizationModeChanged(WorkflowNumberNormalizationMode value) => NotifyEditorStateChanged();
    partial void OnIsCreatingNewChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(CanChangeTemplate));
    }
    partial void OnEditorErrorChanged(string? value) => OnPropertyChanged(nameof(HasEditorError));
    partial void OnEditProviderOverrideChanged(string? value) => OnPropertyChanged(nameof(SelectedEditProvider));

    private void NotifyEditorStateChanged()
    {
        OnPropertyChanged(nameof(IsAutomaticTriggerModeSelected));
        OnPropertyChanged(nameof(IsGlobalTriggerModeSelected));
        OnPropertyChanged(nameof(IsManualTriggerModeSelected));
        OnPropertyChanged(nameof(ShowAppTriggerEditor));
        OnPropertyChanged(nameof(ShowWebsiteTriggerEditor));
        OnPropertyChanged(nameof(ShowHotkeyTriggerEditor));
        OnPropertyChanged(nameof(IsTranslationTemplate));
        OnPropertyChanged(nameof(IsCustomTemplate));
        OnPropertyChanged(nameof(SelectedTriggerIconGlyph));
        OnPropertyChanged(nameof(SelectedTriggerLabel));
        OnPropertyChanged(nameof(SelectedHotkeyBehaviorDescription));
        OnPropertyChanged(nameof(ReviewText));
    }

    private void NotifyWorkflowStateChanged()
    {
        OnPropertyChanged(nameof(WorkflowCount));
        OnPropertyChanged(nameof(EnabledWorkflowCount));
        OnPropertyChanged(nameof(WorkflowSummary));
        OnPropertyChanged(nameof(HasWorkflows));
        OnPropertyChanged(nameof(HasFilteredWorkflows));
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}

/// <summary>
/// Lists the supported workflow trigger mode values.
/// </summary>
public enum WorkflowTriggerMode
{
    /// <summary>
    /// Represents the automatic option.
    /// </summary>
    Automatic,
    /// <summary>
    /// Represents the global option.
    /// </summary>
    Global,
    /// <summary>
    /// Represents the manual option.
    /// </summary>
    Manual
}

/// <summary>
/// Represents workflow template option data.
/// </summary>
/// <param name="Template">Template supplied to the member.</param>
/// <param name="Name">Name supplied to the member.</param>
/// <param name="Description">Description supplied to the member.</param>
/// <param name="IconGlyph">Icon glyph supplied to the member.</param>
public sealed record WorkflowTemplateOption(WorkflowTemplate Template, string Name, string Description, string IconGlyph);
/// <summary>
/// Represents workflow trigger mode option data.
/// </summary>
/// <param name="Mode">Mode supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
/// <param name="IconGlyph">Icon glyph supplied to the member.</param>
public sealed record WorkflowTriggerModeOption(WorkflowTriggerMode Mode, string DisplayName, string IconGlyph);
/// <summary>
/// Represents workflow hotkey behavior option data.
/// </summary>
/// <param name="Behavior">Behavior supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record WorkflowHotkeyBehaviorOption(WorkflowHotkeyBehavior Behavior, string DisplayName);
/// <summary>
/// Represents workflow number normalization option data.
/// </summary>
/// <param name="Mode">Mode supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record WorkflowNumberNormalizationOption(WorkflowNumberNormalizationMode Mode, string DisplayName);
/// <summary>
/// Represents workflow app picker option data.
/// </summary>
/// <param name="ProcessName">Process name supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
/// <param name="Detail">Detail supplied to the member.</param>
/// <param name="Icon">Icon supplied to the member.</param>
/// <param name="IsSelected">Is selected supplied to the member.</param>
public sealed record WorkflowAppPickerOption(string ProcessName, string DisplayName, string Detail, ImageSource? Icon, bool IsSelected);
/// <summary>
/// Represents workflow domain suggestion option data.
/// </summary>
/// <param name="Domain">Domain supplied to the member.</param>
/// <param name="Detail">Detail supplied to the member.</param>
/// <param name="IsCurrent">Is current supplied to the member.</param>
public sealed record WorkflowDomainSuggestionOption(string Domain, string Detail, bool IsCurrent);
/// <summary>
/// Represents setting option data.
/// </summary>
/// <param name="Value">Value supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record SettingOption(string? Value, string DisplayName);
/// <summary>
/// Represents model option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record ModelOption(string? Id, string DisplayName);
/// <summary>
/// Represents provider option data.
/// </summary>
/// <param name="Value">Value supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record ProviderOption(string? Value, string DisplayName);
/// <summary>
/// Represents action plugin option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record ActionPluginOption(string? Id, string DisplayName);
