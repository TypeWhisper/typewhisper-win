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

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<ModelItemViewModel> ParakeetModels { get; } = [];
    public ObservableCollection<ModelItemViewModel> WhisperModels { get; } = [];

    public ModelManagerViewModel(ModelManagerService modelManager, ISettingsService settings)
    {
        _modelManager = modelManager;
        _settings = settings;
        _activeModelId = _modelManager.ActiveModelId;

        foreach (var model in ModelInfo.AvailableModels)
        {
            var item = new ModelItemViewModel(model, _modelManager);
            Models.Add(item);

            if (model.Engine == EngineType.Parakeet)
                ParakeetModels.Add(item);
            else
                WhisperModels.Add(item);
        }

        _modelManager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ModelManagerService.ActiveModelId))
                ActiveModelId = _modelManager.ActiveModelId;

            foreach (var m in Models)
                m.RefreshStatus();
        };
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

            // Persist selection so the model auto-loads on next startup
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
    public EngineType EngineType => _model.Engine;
    public string EngineLabel => _model.Engine == EngineType.Parakeet ? "Parakeet" : "Whisper";

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
