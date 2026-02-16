using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Cloud;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

public partial class ModelManagerViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;

    [ObservableProperty] private string? _activeModelId;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyMessage;

    // Cloud provider API keys
    [ObservableProperty] private string _groqApiKey = "";
    [ObservableProperty] private string _openAiApiKey = "";

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

        // Load API keys from settings (decrypt)
        _groqApiKey = ApiKeyProtection.Decrypt(_settings.Current.GroqApiKey ?? "");
        _openAiApiKey = ApiKeyProtection.Decrypt(_settings.Current.OpenAiApiKey ?? "");

        // Build cloud provider view models from providers
        foreach (var provider in _modelManager.CloudProviders)
        {
            var hasKey = provider.IsConfigured;
            var providerVm = new CloudProviderViewModel(
                provider.Id, provider.DisplayName, hasKey,
                provider.TranslationModel is not null);
            foreach (var model in provider.TranscriptionModels)
            {
                var fullId = CloudProvider.GetFullModelId(provider.Id, model.Id);
                providerVm.Models.Add(new CloudModelItemViewModel(
                    fullId, model.DisplayName, hasKey,
                    _modelManager.ActiveModelId == fullId,
                    model.SupportsTranslation));
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

    partial void OnGroqApiKeyChanged(string value)
    {
        var encrypted = string.IsNullOrWhiteSpace(value) ? null : ApiKeyProtection.Encrypt(value);
        _settings.Save(_settings.Current with { GroqApiKey = encrypted });
        RefreshProviderAvailability("groq", !string.IsNullOrWhiteSpace(value));
    }

    partial void OnOpenAiApiKeyChanged(string value)
    {
        var encrypted = string.IsNullOrWhiteSpace(value) ? null : ApiKeyProtection.Encrypt(value);
        _settings.Save(_settings.Current with { OpenAiApiKey = encrypted });
        RefreshProviderAvailability("openai", !string.IsNullOrWhiteSpace(value));
    }

    private void RefreshProviderAvailability(string providerId, bool hasKey)
    {
        foreach (var p in CloudProviders)
        {
            if (p.ProviderId == providerId)
            {
                p.HasApiKey = hasKey;
                foreach (var m in p.Models)
                    m.IsAvailable = hasKey;
            }
        }
    }

    private void RefreshAllCloudModels()
    {
        foreach (var p in CloudProviders)
            foreach (var m in p.Models)
                m.IsActive = _modelManager.ActiveModelId == m.FullId;
    }

    private string? GetApiKeyForProvider(string providerId) => providerId switch
    {
        "groq" => string.IsNullOrWhiteSpace(GroqApiKey) ? null : GroqApiKey,
        "openai" => string.IsNullOrWhiteSpace(OpenAiApiKey) ? null : OpenAiApiKey,
        _ => null
    };

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
    private async Task TestApiKey(string providerId)
    {
        var provider = _modelManager.CloudProviders.FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return;

        var apiKey = GetApiKeyForProvider(providerId);
        if (string.IsNullOrEmpty(apiKey))
        {
            BusyMessage = "Bitte zuerst einen API-Key eingeben";
            await Task.Delay(2000);
            BusyMessage = null;
            return;
        }

        IsBusy = true;
        BusyMessage = $"{provider.DisplayName} API-Key wird getestet...";
        try
        {
            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{provider.BaseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
                BusyMessage = $"{provider.DisplayName}: API-Key gültig!";
            else
                BusyMessage = $"{provider.DisplayName}: Ungültiger API-Key (HTTP {(int)response.StatusCode})";
        }
        catch (Exception ex)
        {
            BusyMessage = $"Verbindungsfehler: {ex.Message}";
        }

        await Task.Delay(2500);
        IsBusy = false;
        BusyMessage = null;
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
