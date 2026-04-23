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
    [ObservableProperty] private WorkflowTriggerKind _editTriggerKind = WorkflowTriggerKind.Hotkey;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _websitePatternInput = "";
    [ObservableProperty] private string? _editHotkey;
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
    [ObservableProperty] private bool _isAppPickerOpen;
    [ObservableProperty] private string _appPickerSearchText = "";
    [ObservableProperty] private string? _currentWebsiteDomain;

    public ObservableCollection<Workflow> Workflows { get; } = [];
    public ObservableCollection<Workflow> FilteredWorkflows { get; } = [];
    public ObservableCollection<string> ProcessNameChips { get; } = [];
    public ObservableCollection<string> WebsitePatternChips { get; } = [];
    public ObservableCollection<WorkflowAppPickerOption> AppPickerOptions { get; } = [];
    public ObservableCollection<WorkflowDomainSuggestionOption> DomainSuggestions { get; } = [];
    public ObservableCollection<WorkflowTemplateOption> TemplateOptions { get; } = [];
    public ObservableCollection<WorkflowTriggerKindOption> TriggerKindOptions { get; } = [];
    public ObservableCollection<SettingOption> LanguageOptions { get; } = [];
    public ObservableCollection<SettingOption> TaskOptions { get; } = [];
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    public ObservableCollection<ModelOption> AvailableModelOptions { get; } = [];
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    public ObservableCollection<ActionPluginOption> ActionPluginOptions { get; } = [];

    public int WorkflowCount => Workflows.Count;
    public int EnabledWorkflowCount => Workflows.Count(static workflow => workflow.IsEnabled);
    public string WorkflowSummary => Loc.Instance.GetString("Workflows.SummaryFormat", WorkflowCount, EnabledWorkflowCount);
    public string EditorTitle => IsCreatingNew
        ? Loc.Instance["Workflows.NewTitle"]
        : Loc.Instance["Workflows.EditTitle"];
    public bool HasWorkflows => Workflows.Count > 0;
    public bool HasFilteredWorkflows => FilteredWorkflows.Count > 0;
    public bool IsAppTriggerSelected => EditTriggerKind == WorkflowTriggerKind.App;
    public bool IsWebsiteTriggerSelected => EditTriggerKind == WorkflowTriggerKind.Website;
    public bool IsHotkeyTriggerSelected => EditTriggerKind == WorkflowTriggerKind.Hotkey;
    public bool IsTranslationTemplate => EditTemplate == WorkflowTemplate.Translation;
    public bool IsCustomTemplate => EditTemplate == WorkflowTemplate.Custom;
    public bool CanChangeTemplate => IsCreatingNew;
    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorError);
    public bool HasAppPickerOptions => AppPickerOptions.Count > 0;
    public bool HasDomainSuggestions => DomainSuggestions.Count > 0;
    public bool HasCurrentWebsiteDomain => !string.IsNullOrWhiteSpace(CurrentWebsiteDomain);
    public bool SupportsSelectedTranscriptionTranslation => SelectedTranscriptionEngineSupportsTranslation();
    public string SelectedTemplateName => WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Name;
    public string SelectedTemplateDescription => WorkflowTemplateCatalog.DefinitionFor(EditTemplate).Description;
    public string SelectedTemplateIconGlyph => TemplateIconGlyph(EditTemplate);
    public string SelectedTriggerIconGlyph => TriggerIconGlyph(EditTriggerKind);
    public string ReviewText => BuildReviewText();

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
    private void SelectTriggerKind(WorkflowTriggerKindOption? option)
    {
        if (option is null)
            return;

        EditTriggerKind = option.Kind;
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
        EditTriggerKind = workflow.Trigger.Kind;
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
        EditHotkey = workflow.Trigger.Hotkeys.FirstOrDefault();

        ReplaceCollection(ProcessNameChips, workflow.Trigger.ProcessNames);
        ReplaceCollection(WebsitePatternChips, workflow.Trigger.WebsitePatterns);
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
                TargetActionPluginId = string.IsNullOrWhiteSpace(EditTargetActionPluginId) ? null : EditTargetActionPluginId
            },
            CreatedAt = IsCreatingNew ? DateTime.UtcNow : _editingCreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private WorkflowTrigger BuildTrigger() => EditTriggerKind switch
    {
        WorkflowTriggerKind.App => WorkflowTrigger.App([.. ProcessNameChips]),
        WorkflowTriggerKind.Website => WorkflowTrigger.Website([.. WebsitePatternChips]),
        WorkflowTriggerKind.Hotkey => WorkflowTrigger.Hotkey(HotkeyParser.Normalize(EditHotkey)),
        _ => WorkflowTrigger.Hotkey()
    };

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
        switch (EditTriggerKind)
        {
            case WorkflowTriggerKind.App when ProcessNameChips.Count == 0:
                return Loc.Instance["Workflows.ValidationApp"];
            case WorkflowTriggerKind.Website when WebsitePatternChips.Count == 0:
                return Loc.Instance["Workflows.ValidationWebsite"];
            case WorkflowTriggerKind.Hotkey when string.IsNullOrWhiteSpace(HotkeyParser.Normalize(EditHotkey)):
                return Loc.Instance["Workflows.ValidationHotkey"];
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
        var trigger = EditTriggerKind switch
        {
            WorkflowTriggerKind.App => ProcessNameChips.Count == 0 ? Loc.Instance["Workflows.TriggerApp"] : string.Join(", ", ProcessNameChips),
            WorkflowTriggerKind.Website => WebsitePatternChips.Count == 0 ? Loc.Instance["Workflows.TriggerWebsite"] : string.Join(", ", WebsitePatternChips),
            WorkflowTriggerKind.Hotkey => string.IsNullOrWhiteSpace(EditHotkey) ? Loc.Instance["Workflows.TriggerHotkey"] : EditHotkey,
            _ => ""
        };

        return Loc.Instance.GetString("Workflows.ReviewFormat", name, templateName, trigger);
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
        ReplaceCollection(TriggerKindOptions,
        [
            new WorkflowTriggerKindOption(WorkflowTriggerKind.App, Loc.Instance["Workflows.TriggerApp"], TriggerIconGlyph(WorkflowTriggerKind.App)),
            new WorkflowTriggerKindOption(WorkflowTriggerKind.Website, Loc.Instance["Workflows.TriggerWebsite"], TriggerIconGlyph(WorkflowTriggerKind.Website)),
            new WorkflowTriggerKindOption(WorkflowTriggerKind.Hotkey, Loc.Instance["Workflows.TriggerHotkey"], TriggerIconGlyph(WorkflowTriggerKind.Hotkey))
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
        if (string.IsNullOrWhiteSpace(configuredDefault))
            return Loc.Instance["Workflows.DefaultProviderLabelNone"];

        var configuredOption = explicitOptions.FirstOrDefault(option =>
            string.Equals(option.Value, configuredDefault, StringComparison.Ordinal));
        return configuredOption is null
            ? Loc.Instance["Workflows.DefaultProviderLabelNone"]
            : Loc.Instance.GetString("Workflows.DefaultProviderLabelFormat", configuredOption.DisplayName);
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

    public static string WorkflowTriggerSummary(Workflow workflow) => workflow.Trigger.Kind switch
    {
        WorkflowTriggerKind.App => Loc.Instance["Workflows.TriggerApp"],
        WorkflowTriggerKind.Website => Loc.Instance["Workflows.TriggerWebsite"],
        WorkflowTriggerKind.Hotkey => Loc.Instance["Workflows.TriggerHotkey"],
        _ => ""
    };

    public static string WorkflowTriggerDetail(Workflow workflow) => workflow.Trigger.Kind switch
    {
        WorkflowTriggerKind.App => string.Join(", ", workflow.Trigger.ProcessNames),
        WorkflowTriggerKind.Website => string.Join(", ", workflow.Trigger.WebsitePatterns),
        WorkflowTriggerKind.Hotkey => string.Join(", ", workflow.Trigger.Hotkeys),
        _ => ""
    };

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

    public static string TriggerIconGlyph(WorkflowTriggerKind kind) => kind switch
    {
        WorkflowTriggerKind.App => "\uE71D",
        WorkflowTriggerKind.Website => "\uE774",
        WorkflowTriggerKind.Hotkey => "\uE765",
        _ => "\uE8F1"
    };

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

    partial void OnEditTriggerKindChanged(WorkflowTriggerKind value)
    {
        OnPropertyChanged(nameof(SelectedTriggerIconGlyph));
        if (value == WorkflowTriggerKind.App)
            RefreshAppPickerOptions();
        if (value == WorkflowTriggerKind.Website)
            RefreshDomainSuggestions();
        NotifyEditorStateChanged();
    }
    partial void OnAppPickerSearchTextChanged(string value) => RefreshAppPickerOptions();
    partial void OnWebsitePatternInputChanged(string value) => RefreshDomainSuggestions();
    partial void OnCurrentWebsiteDomainChanged(string? value) => OnPropertyChanged(nameof(HasCurrentWebsiteDomain));
    partial void OnEditNameChanged(string value) => NotifyEditorStateChanged();
    partial void OnEditHotkeyChanged(string? value) => NotifyEditorStateChanged();
    partial void OnEditTranscriptionModelOverrideChanged(string? value) => RebuildTaskOptions();
    partial void OnEditTranslationTargetLanguageChanged(string value) => NotifyEditorStateChanged();
    partial void OnEditCustomInstructionChanged(string value) => NotifyEditorStateChanged();
    partial void OnIsCreatingNewChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(CanChangeTemplate));
    }
    partial void OnEditorErrorChanged(string? value) => OnPropertyChanged(nameof(HasEditorError));
    partial void OnEditProviderOverrideChanged(string? value) => OnPropertyChanged(nameof(SelectedEditProvider));

    private void NotifyEditorStateChanged()
    {
        OnPropertyChanged(nameof(IsAppTriggerSelected));
        OnPropertyChanged(nameof(IsWebsiteTriggerSelected));
        OnPropertyChanged(nameof(IsHotkeyTriggerSelected));
        OnPropertyChanged(nameof(IsTranslationTemplate));
        OnPropertyChanged(nameof(IsCustomTemplate));
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

public sealed record WorkflowTemplateOption(WorkflowTemplate Template, string Name, string Description, string IconGlyph);
public sealed record WorkflowTriggerKindOption(WorkflowTriggerKind Kind, string DisplayName, string IconGlyph);
public sealed record WorkflowAppPickerOption(string ProcessName, string DisplayName, string Detail, ImageSource? Icon, bool IsSelected);
public sealed record WorkflowDomainSuggestionOption(string Domain, string Detail, bool IsCurrent);
public sealed record SettingOption(string? Value, string DisplayName);
public sealed record ModelOption(string? Id, string DisplayName);
public sealed record ProviderOption(string? Value, string DisplayName);
public sealed record ActionPluginOption(string? Id, string DisplayName);
