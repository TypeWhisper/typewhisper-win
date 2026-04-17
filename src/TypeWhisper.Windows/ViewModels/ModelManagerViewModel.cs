using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class ModelManagerViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private bool _isSyncingSelection;

    [ObservableProperty] private string? _activeModelId;
    [ObservableProperty] private string? _selectedModelOptionId;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyMessage;
    [ObservableProperty] private string _activeProviderDisplayName = "None";
    [ObservableProperty] private string _activeModelDisplayName = "No model selected";
    [ObservableProperty] private string _activeModelStatusText = "";
    [ObservableProperty] private bool _isActiveModelReady;

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];
    public ObservableCollection<ModelOptionViewModel> AvailableModelOptions { get; } = [];

    public ModelManagerViewModel(ModelManagerService modelManager, ISettingsService settings)
    {
        _modelManager = modelManager;
        _settings = settings;
        _activeModelId = _modelManager.ActiveModelId;

        RebuildProviders();

        _modelManager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ModelManagerService.ActiveModelId))
            {
                ActiveModelId = _modelManager.ActiveModelId;
                RefreshAllModels();
            }

            if (args.PropertyName == nameof(ModelManagerService.GetStatus))
                RefreshAllModels();
        };

        _modelManager.PluginManager.PluginStateChanged += (_, _) =>
            Application.Current.Dispatcher.Invoke(RebuildProviders);

        _settings.SettingsChanged += _ => InvokeOnUiThread(() =>
        {
            SyncSelectedModelOption();
            RefreshActiveModelDetails();
        });
    }

    private void RebuildProviders()
    {
        Providers.Clear();
        AvailableModelOptions.Clear();
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
        {
            var isLlmProvider = _modelManager.PluginManager.LlmProviders
                .Any(l => l.PluginId == engine.PluginId);
            var providerVm = new ProviderViewModel(
                engine.PluginId, engine.ProviderDisplayName,
                engine.IsConfigured, isLlmProvider, engine.SupportsModelDownload);

            foreach (var model in engine.TranscriptionModels)
            {
                var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var status = _modelManager.GetStatus(fullId);
                providerVm.Models.Add(new ModelItemViewModel(
                    fullId, model, engine.IsConfigured,
                    _modelManager.ActiveModelId == fullId,
                    engine.SupportsTranslation,
                    engine.SupportsModelDownload,
                    _modelManager.IsDownloaded(fullId),
                    status));

                AvailableModelOptions.Add(new ModelOptionViewModel(
                    fullId,
                    engine.ProviderDisplayName,
                    model.DisplayName,
                    $"{engine.ProviderDisplayName} / {model.DisplayName}"));
            }
            Providers.Add(providerVm);
        }

        SyncSelectedModelOption();
        RefreshActiveModelDetails();
    }

    /// <summary>
    /// Refreshes provider availability based on current plugin state.
    /// Called by plugin settings views after API key changes.
    /// </summary>
    public void RefreshPluginAvailability()
    {
        foreach (var providerVm in Providers)
        {
            var engine = _modelManager.PluginManager.TranscriptionEngines
                .FirstOrDefault(e => e.PluginId == providerVm.ProviderId);
            var isConfigured = engine?.IsConfigured ?? false;
            providerVm.IsConfigured = isConfigured;
            foreach (var m in providerVm.Models)
                m.IsAvailable = isConfigured;
        }
    }

    private void RefreshAllModels()
    {
        foreach (var p in Providers)
            foreach (var m in p.Models)
            {
                m.IsActive = _modelManager.ActiveModelId == m.FullId;
                m.IsDownloaded = _modelManager.IsDownloaded(m.FullId);
                var status = _modelManager.GetStatus(m.FullId);
                m.IsReady = status.Type == ModelStatusType.Ready;
                m.StatusText = FormatStatus(status, m.IsDownloaded);
            }

        SyncSelectedModelOption();
        RefreshActiveModelDetails();
    }

    partial void OnSelectedModelOptionIdChanged(string? value)
    {
        if (_isSyncingSelection || string.IsNullOrWhiteSpace(value) || value == ActiveModelId)
            return;

        RefreshActiveModelDetails();

        if (ActivateModelCommand.CanExecute(value))
            ActivateModelCommand.Execute(value);
    }

    internal static string FormatStatus(ModelStatus status, bool isDownloaded) => status.Type switch
    {
        ModelStatusType.Downloading => $"Download {status.Progress:P0}",
        ModelStatusType.Loading => Loc.Instance["Models.StatusLoading"],
        ModelStatusType.Ready => Loc.Instance["Models.StatusReady"],
        ModelStatusType.Error => Loc.Instance.GetString("Models.StatusErrorFormat", status.ErrorMessage ?? ""),
        _ => isDownloaded ? Loc.Instance["Models.StatusDownloaded"] : ""
    };

    [RelayCommand]
    private async Task ActivateModel(string fullModelId)
    {
        IsBusy = true;
        BusyMessage = Loc.Instance["Models.LoadingModel"];
        try
        {
            await _modelManager.DownloadAndLoadModelAsync(fullModelId);
            ActiveModelId = fullModelId;
            _settings.Save(_settings.Current with { SelectedModelId = fullModelId });
        }
        catch (Exception ex)
        {
            BusyMessage = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
            await Task.Delay(2000);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    private void SyncSelectedModelOption()
    {
        if (IsBusy && !string.IsNullOrWhiteSpace(SelectedModelOptionId))
            return;

        _isSyncingSelection = true;
        SelectedModelOptionId = _settings.Current.SelectedModelId;
        _isSyncingSelection = false;
    }

    private static void InvokeOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
            return;
        }

        action();
    }

    private void RefreshActiveModelDetails()
    {
        var displayModelId = !string.IsNullOrWhiteSpace(SelectedModelOptionId)
            ? SelectedModelOptionId
            : ActiveModelId;

        var activeModel = Providers
            .SelectMany(p => p.Models.Select(m => (Provider: p, Model: m)))
            .FirstOrDefault(x => x.Model.FullId == displayModelId);

        if (activeModel.Model is null)
        {
            ActiveProviderDisplayName = "None";
            ActiveModelDisplayName = "No model selected";
            ActiveModelStatusText = "";
            IsActiveModelReady = false;
            return;
        }

        ActiveProviderDisplayName = activeModel.Provider.DisplayName;
        ActiveModelDisplayName = activeModel.Model.DisplayName;
        ActiveModelStatusText = activeModel.Model.StatusText;
        IsActiveModelReady = activeModel.Model.IsReady;
    }

    [RelayCommand]
    private void DeleteModel(string modelId)
    {
        _modelManager.DeleteModel(modelId);
    }
}

public partial class ProviderViewModel : ObservableObject
{
    public string ProviderId { get; }
    public string DisplayName { get; }
    public bool HasLlmTranslation { get; }
    public bool SupportsDownload { get; }
    public ObservableCollection<ModelItemViewModel> Models { get; } = [];

    [ObservableProperty] private bool _isConfigured;

    public ProviderViewModel(string providerId, string displayName, bool isConfigured,
        bool hasLlmTranslation, bool supportsDownload)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        _isConfigured = isConfigured;
        HasLlmTranslation = hasLlmTranslation;
        SupportsDownload = supportsDownload;
    }
}

public partial class ModelItemViewModel : ObservableObject
{
    public string FullId { get; }
    public string DisplayName { get; }
    public string? SizeDescription { get; }
    public bool IsRecommended { get; }
    public int LanguageCount { get; }
    public bool SupportsTranslation { get; }
    public bool SupportsDownload { get; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _statusText = "";

    public ModelItemViewModel(string fullId, PluginModelInfo model, bool isAvailable,
        bool isActive, bool supportsTranslation, bool supportsDownload,
        bool isDownloaded, ModelStatus status)
    {
        FullId = fullId;
        DisplayName = model.DisplayName;
        SizeDescription = model.SizeDescription;
        IsRecommended = model.IsRecommended;
        LanguageCount = model.LanguageCount;
        SupportsTranslation = supportsTranslation;
        SupportsDownload = supportsDownload;
        _isAvailable = isAvailable;
        _isActive = isActive;
        _isDownloaded = isDownloaded;
        _isReady = status.Type == ModelStatusType.Ready;
        _statusText = ModelManagerViewModel.FormatStatus(status, isDownloaded);
    }
}

public sealed class ModelOptionViewModel
{
    public string FullId { get; }
    public string ProviderDisplayName { get; }
    public string ModelDisplayName { get; }
    public string DisplayName { get; }

    public ModelOptionViewModel(string fullId, string providerDisplayName, string modelDisplayName, string displayName)
    {
        FullId = fullId;
        ProviderDisplayName = providerDisplayName;
        ModelDisplayName = modelDisplayName;
        DisplayName = displayName;
    }
}
