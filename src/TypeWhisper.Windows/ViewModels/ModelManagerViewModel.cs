using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

public partial class ModelManagerViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;

    [ObservableProperty] private string? _activeModelId;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyMessage;

    public ObservableCollection<ModelItemViewModel> LocalModels { get; } = [];
    public ObservableCollection<CloudProviderViewModel> CloudProviders { get; } = [];

    public ModelManagerViewModel(ModelManagerService modelManager, ISettingsService settings)
    {
        _modelManager = modelManager;
        _settings = settings;
        _activeModelId = _modelManager.ActiveModelId;

        // Build local model list from providers
        foreach (var provider in _modelManager.LocalProviders)
        {
            LocalModels.Add(new ModelItemViewModel(provider.Model, _modelManager));
        }

        // Build cloud provider view models from plugin transcription engines
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
        {
            var hasKey = engine.IsConfigured;
            var isLlmProvider = _modelManager.PluginManager.LlmProviders
                .Any(l => l.PluginId == engine.PluginId);
            var providerVm = new CloudProviderViewModel(
                engine.PluginId, engine.ProviderDisplayName, hasKey, isLlmProvider);
            foreach (var model in engine.TranscriptionModels)
            {
                var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                providerVm.Models.Add(new CloudModelItemViewModel(
                    fullId, model.DisplayName, hasKey,
                    _modelManager.ActiveModelId == fullId,
                    engine.SupportsTranslation));
            }
            CloudProviders.Add(providerVm);
        }

        _modelManager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ModelManagerService.ActiveModelId))
            {
                ActiveModelId = _modelManager.ActiveModelId;
                RefreshAllCloudModels();
            }

            foreach (var m in LocalModels)
                m.RefreshStatus();
        };
    }

    /// <summary>
    /// Refreshes cloud provider availability based on current plugin state.
    /// Called by plugin settings views after API key changes.
    /// </summary>
    public void RefreshPluginAvailability()
    {
        foreach (var providerVm in CloudProviders)
        {
            var engine = _modelManager.PluginManager.TranscriptionEngines
                .FirstOrDefault(e => e.PluginId == providerVm.ProviderId);
            var hasKey = engine?.IsConfigured ?? false;
            providerVm.HasApiKey = hasKey;
            foreach (var m in providerVm.Models)
                m.IsAvailable = hasKey;
        }
    }

    private void RefreshAllCloudModels()
    {
        foreach (var p in CloudProviders)
            foreach (var m in p.Models)
                m.IsActive = _modelManager.ActiveModelId == m.FullId;
    }

    [RelayCommand]
    private async Task DownloadAndLoadModel(string modelId)
    {
        IsBusy = true;
        BusyMessage = "Lade Modell...";
        try
        {
            await _modelManager.DownloadAndLoadModelAsync(modelId);
            ActiveModelId = modelId;
            _settings.Save(_settings.Current with { SelectedModelId = modelId });
        }
        catch (Exception ex)
        {
            BusyMessage = $"Fehler: {ex.Message}";
            await Task.Delay(2000);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    [RelayCommand]
    private async Task SelectCloudModel(string fullModelId)
    {
        IsBusy = true;
        BusyMessage = "Aktiviere Cloud-Modell...";
        try
        {
            await _modelManager.LoadModelAsync(fullModelId);
            ActiveModelId = fullModelId;
            _settings.Save(_settings.Current with { SelectedModelId = fullModelId });
        }
        catch (Exception ex)
        {
            BusyMessage = $"Fehler: {ex.Message}";
            await Task.Delay(2000);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    [RelayCommand]
    private void DeleteModel(string modelId)
    {
        _modelManager.DeleteModel(modelId);
    }
}

public partial class ModelItemViewModel : ObservableObject
{
    private readonly ModelInfo _model;
    private readonly ModelManagerService _manager;

    public string Id => _model.Id;
    public string Name => _model.DisplayName;
    public string Size => _model.SizeDescription;
    public bool IsRecommended => _model.IsRecommended;
    public int LanguageCount => _model.LanguageCount;
    public bool SupportsTranslation => _model.SupportsTranslation;

    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _statusText = "";

    public ModelItemViewModel(ModelInfo model, ModelManagerService manager)
    {
        _model = model;
        _manager = manager;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        IsDownloaded = _manager.IsDownloaded(Id);
        IsActive = _manager.ActiveModelId == Id;

        var status = _manager.GetStatus(Id);
        StatusText = status.Type switch
        {
            ModelStatusType.Downloading => $"Download {status.Progress:P0}",
            ModelStatusType.Loading => "Laden...",
            ModelStatusType.Ready => "Bereit",
            ModelStatusType.Error => $"Fehler: {status.ErrorMessage}",
            _ => IsDownloaded ? "Heruntergeladen" : ""
        };
    }
}

public partial class CloudProviderViewModel : ObservableObject
{
    public string ProviderId { get; }
    public string DisplayName { get; }
    public bool HasLlmTranslation { get; }
    public ObservableCollection<CloudModelItemViewModel> Models { get; } = [];

    [ObservableProperty] private bool _hasApiKey;

    public CloudProviderViewModel(string providerId, string displayName, bool hasApiKey, bool hasLlmTranslation)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        _hasApiKey = hasApiKey;
        HasLlmTranslation = hasLlmTranslation;
    }
}

public partial class CloudModelItemViewModel : ObservableObject
{
    public string FullId { get; }
    public string DisplayName { get; }
    public bool SupportsWhisperTranslation { get; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isAvailable;

    public CloudModelItemViewModel(string fullId, string displayName, bool isAvailable, bool isActive, bool supportsWhisperTranslation)
    {
        FullId = fullId;
        DisplayName = displayName;
        _isAvailable = isAvailable;
        _isActive = isActive;
        SupportsWhisperTranslation = supportsWhisperTranslation;
    }
}
