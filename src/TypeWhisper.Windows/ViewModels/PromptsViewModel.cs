using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

public partial class PromptsViewModel : ObservableObject
{
    private readonly IPromptActionService _promptActions;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;

    [ObservableProperty] private PromptAction? _selectedAction;

    // Editor state
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isCreatingNew;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editSystemPrompt = "";
    [ObservableProperty] private string _editIcon = "\u2728";
    [ObservableProperty] private string? _editProviderOverride;
    [ObservableProperty] private string? _editModelOverride;
    private string? _editingActionId;

    public ObservableCollection<PromptAction> Actions { get; } = [];
    public bool HasActions => Actions.Count > 0;
    public int ActionCount => Actions.Count;
    public int EnabledActionCount => Actions.Count(static action => action.IsEnabled);
    public string SummaryText => Loc.Instance.GetString("Prompts.SummaryFormat", ActionCount, EnabledActionCount);
    public string EditorTitle => IsCreatingNew
        ? Loc.Instance["Prompts.NewPromptTitle"]
        : Loc.Instance["Prompts.EditPromptTitle"];

    public bool HasLlmProviders => _pluginManager.LlmProviders.Any(p => p.IsAvailable);

    public string? DefaultLlmProvider
    {
        get => _settings.Current.DefaultLlmProvider;
        set
        {
            _settings.Save(_settings.Current with { DefaultLlmProvider = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultProviderSummary));
        }
    }

    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    public string DefaultProviderSummary => GetDefaultProviderLabel();

    public static IReadOnlyList<string> IconOptions { get; } =
    [
        "\u2728", "\U0001F30D", "\u2709\uFE0F", "\U0001F4CB", "\u2705", "\U0001F4AC",
        "\U0001F4DD", "\U0001F4A1", "\U0001F50D", "\U0001F4DA", "\U0001F3AF",
        "\u2699\uFE0F", "\U0001F916", "\U0001F4CA", "\U0001F680"
    ];

    public PromptsViewModel(
        IPromptActionService promptActions,
        PluginManager pluginManager,
        ISettingsService settings)
    {
        _promptActions = promptActions;
        _pluginManager = pluginManager;
        _settings = settings;

        _promptActions.ActionsChanged += RefreshActions;
        RefreshActions();
        RefreshProviders();
    }

    [RelayCommand]
    private void StartCreate()
    {
        _editingActionId = null;
        IsCreatingNew = true;
        EditName = "";
        EditSystemPrompt = "";
        EditIcon = "\u2728";
        EditProviderOverride = null;
        EditModelOverride = null;
        IsEditorOpen = true;
        ShowEditorDialog();
    }

    [RelayCommand]
    private void StartEdit(PromptAction? action)
    {
        if (action is null) return;
        SelectedAction = action;
        _editingActionId = action.Id;
        IsCreatingNew = false;
        EditName = action.Name;
        EditSystemPrompt = action.SystemPrompt;
        EditIcon = action.Icon;
        EditProviderOverride = action.ProviderOverride;
        EditModelOverride = action.ModelOverride;
        IsEditorOpen = true;
        ShowEditorDialog();
    }

    [RelayCommand]
    private void SaveEditor()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditSystemPrompt)) return;

        if (IsCreatingNew)
        {
            _promptActions.AddAction(new PromptAction
            {
                Id = Guid.NewGuid().ToString(),
                Name = EditName.Trim(),
                SystemPrompt = EditSystemPrompt.Trim(),
                Icon = EditIcon,
                ProviderOverride = EditProviderOverride,
                ModelOverride = EditModelOverride,
                SortOrder = _promptActions.Actions.Count
            });
        }
        else if (_editingActionId is not null)
        {
            var existing = _promptActions.Actions.FirstOrDefault(a => a.Id == _editingActionId);
            if (existing is not null)
            {
                _promptActions.UpdateAction(existing with
                {
                    Name = EditName.Trim(),
                    SystemPrompt = EditSystemPrompt.Trim(),
                    Icon = EditIcon,
                    ProviderOverride = EditProviderOverride,
                    ModelOverride = EditModelOverride
                });
            }
        }

        IsEditorOpen = false;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private void DeleteAction(PromptAction? action)
    {
        if (action is null) return;

        var result = MessageBox.Show(
            Loc.Instance.GetString("Prompts.DeleteConfirm", action.Name),
            Loc.Instance["Prompts.DeleteTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _promptActions.DeleteAction(action.Id);
        if (SelectedAction?.Id == action.Id)
            SelectedAction = null;
    }

    [RelayCommand]
    private void ToggleEnabled(PromptAction? action)
    {
        if (action is null) return;
        _promptActions.UpdateAction(action with { IsEnabled = !action.IsEnabled });
    }

    [RelayCommand]
    private void MoveUp(PromptAction? action)
    {
        if (action is null) return;
        var orderedIds = _promptActions.Actions.OrderBy(a => a.SortOrder).Select(a => a.Id).ToList();
        var idx = orderedIds.IndexOf(action.Id);
        if (idx <= 0) return;
        (orderedIds[idx], orderedIds[idx - 1]) = (orderedIds[idx - 1], orderedIds[idx]);
        _promptActions.Reorder(orderedIds);
    }

    [RelayCommand]
    private void MoveDown(PromptAction? action)
    {
        if (action is null) return;
        var orderedIds = _promptActions.Actions.OrderBy(a => a.SortOrder).Select(a => a.Id).ToList();
        var idx = orderedIds.IndexOf(action.Id);
        if (idx < 0 || idx >= orderedIds.Count - 1) return;
        (orderedIds[idx], orderedIds[idx + 1]) = (orderedIds[idx + 1], orderedIds[idx]);
        _promptActions.Reorder(orderedIds);
    }

    private void RefreshActions()
    {
        Actions.Clear();
        foreach (var action in _promptActions.Actions.OrderBy(a => a.SortOrder))
            Actions.Add(action);
        NotifyStateChanged();
    }

    public void RefreshProviders()
    {
        AvailableProviders.Clear();
        AvailableProviders.Add(new ProviderOption(null, GetDefaultProviderLabel()));
        foreach (var provider in _pluginManager.LlmProviders)
        {
            if (!provider.IsAvailable) continue;
            var plugin = _pluginManager.AllPlugins
                .FirstOrDefault(p => p.Instance == provider);
            if (plugin is null) continue;

            foreach (var model in provider.SupportedModels)
            {
                var modelId = $"plugin:{plugin.Manifest.Id}:{model.Id}";
                AvailableProviders.Add(new ProviderOption(modelId, $"{provider.ProviderName} / {model.DisplayName}"));
            }
        }
        OnPropertyChanged(nameof(HasLlmProviders));
        OnPropertyChanged(nameof(DefaultProviderSummary));
    }

    private string GetDefaultProviderLabel()
    {
        var firstAvailable = _pluginManager.LlmProviders.FirstOrDefault(p => p.IsAvailable);
        if (firstAvailable is null)
            return Loc.Instance["Prompts.DefaultProviderLabelNone"];

        var firstModel = firstAvailable.SupportedModels.FirstOrDefault();
        if (firstModel is not null)
            return Loc.Instance.GetString("Prompts.DefaultProviderLabelFormat",
                $"{firstAvailable.ProviderName} / {firstModel.DisplayName}");

        return Loc.Instance["Prompts.DefaultProviderLabelNone"];
    }

    partial void OnIsCreatingNewChanged(bool value) => OnPropertyChanged(nameof(EditorTitle));

    private void ShowEditorDialog()
    {
        var owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        var editor = new PromptEditorWindow(this);
        if (owner is not null)
            editor.Owner = owner;
        editor.ShowDialog();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(ActionCount));
        OnPropertyChanged(nameof(EnabledActionCount));
        OnPropertyChanged(nameof(SummaryText));
    }
}

public sealed record ProviderOption(string? Value, string DisplayName);
