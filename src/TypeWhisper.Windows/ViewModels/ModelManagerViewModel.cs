using System.Collections.ObjectModel;
using System.Threading;
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

/// <summary>
/// Provides model manager view model behavior.
/// </summary>
public partial class ModelManagerViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly LocalModelStorageService _modelStorage;
    private readonly IAppRestartService? _appRestart;
    private readonly IAppNotificationService? _notifications;
    private readonly SemaphoreSlim _accelerationApplyLock = new(1, 1);
    private bool _isSyncingSelection;
    private bool _isSyncingAccelerationSelection;
    private bool _accelerationRestartNotificationShown;
    private int _accelerationApplyVersion;
    private int _accelerationBusyVersion;

    [ObservableProperty] private string? _activeModelId;
    [ObservableProperty] private string? _selectedModelOptionId;
    private string _selectedAccelerationOptionValue = AppSettings.LocalModelAccelerationAuto;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyMessage;
    [ObservableProperty] private string _activeProviderDisplayName = "None";
    [ObservableProperty] private string _activeModelDisplayName = "No model selected";
    [ObservableProperty] private string _activeModelStatusText = "";
    private string _accelerationStatusText = "";
    [ObservableProperty] private bool _isActiveModelReady;
    [ObservableProperty] private bool _isActiveModelBusy;
    [ObservableProperty] private bool _isAccelerationSectionVisible;
    [ObservableProperty] private bool _isAccelerationRestartRequired;
    [ObservableProperty] private string _accelerationRestartMessage = "";
    [ObservableProperty] private string _modelStoragePath = "";
    [ObservableProperty] private string _resolvedModelStoragePath = "";
    [ObservableProperty] private string _modelStorageStatusText = "";
    [ObservableProperty] private bool _isModelStorageBusy;
    [ObservableProperty] private bool _hasModelStorageError;

    /// <summary>
    /// Gets the currently selected acceleration option.
    /// </summary>
    public string SelectedAccelerationOptionValue
    {
        get => _selectedAccelerationOptionValue;
        set
        {
            if (SetProperty(ref _selectedAccelerationOptionValue, value))
                OnSelectedAccelerationOptionValueChanged(value);
        }
    }

    /// <summary>
    /// Gets the acceleration status text.
    /// </summary>
    public string AccelerationStatusText
    {
        get => _accelerationStatusText;
        set => SetProperty(ref _accelerationStatusText, value);
    }

    /// <summary>
    /// Gets the providers.
    /// </summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];
    /// <summary>
    /// Gets the available model options.
    /// </summary>
    public ObservableCollection<ModelOptionViewModel> AvailableModelOptions { get; } = [];
    /// <summary>
    /// Gets the acceleration options.
    /// </summary>
    public ObservableCollection<AccelerationOptionViewModel> AccelerationOptions { get; } =
    [
        new(AppSettings.LocalModelAccelerationAuto, Loc.Instance["Models.AccelerationAuto"]),
        new(AppSettings.LocalModelAccelerationCpu, Loc.Instance["Models.AccelerationCpu"]),
        new(AppSettings.LocalModelAccelerationNvidiaCuda, Loc.Instance["Models.AccelerationNvidiaCuda"]),
        new(AppSettings.LocalModelAccelerationAmdVulkan, Loc.Instance["Models.AccelerationAmdVulkan"]),
        new(AppSettings.LocalModelAccelerationAmdRocm, Loc.Instance["Models.AccelerationAmdRocm"])
    ];

    /// <summary>
    /// Initializes a new instance of the ModelManagerViewModel class.
    /// </summary>
    public ModelManagerViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        IAppRestartService? appRestart = null,
        IAppNotificationService? notifications = null,
        LocalModelStorageService? modelStorage = null)
    {
        _modelManager = modelManager;
        _settings = settings;
        _modelStorage = modelStorage ?? new LocalModelStorageService(settings, _modelManager.UnloadModel);
        _appRestart = appRestart;
        _notifications = notifications;
        _activeModelId = _modelManager.ActiveModelId;
        _selectedAccelerationOptionValue = AppSettings.NormalizeLocalModelAcceleration(
            _settings.Current.LocalModelAcceleration);

        RebuildProviders();
        RefreshModelStorage();

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
            InvokeOnUiThread(RebuildProviders);

        _settings.SettingsChanged += _ => InvokeOnUiThread(() =>
        {
            SyncSelectedModelOption();
            SyncSelectedAccelerationOption();
            RefreshActiveModelDetails();
            RefreshModelStorage();
        });
    }

    private void RebuildProviders()
    {
        Providers.Clear();
        AvailableModelOptions.Clear();
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
        {
            var selectionId = engine.GetTranscriptionSelectionId();
            var isLlmProvider = _modelManager.PluginManager.LlmProviders
                .Any(l => string.Equals(l.GetLlmSelectionId(), selectionId, StringComparison.OrdinalIgnoreCase));
            var providerVm = new ProviderViewModel(
                selectionId, engine.ProviderDisplayName,
                engine.IsConfigured, isLlmProvider, engine.SupportsModelDownload);

            foreach (var model in engine.TranscriptionModels)
            {
                var fullId = ModelManagerService.GetPluginModelId(selectionId, model.Id);
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
                .FirstOrDefault(e => string.Equals(
                    e.GetTranscriptionSelectionId(),
                    providerVm.ProviderId,
                    StringComparison.OrdinalIgnoreCase));
            var isConfigured = engine?.IsConfigured ?? false;
            providerVm.IsConfigured = isConfigured;
            foreach (var m in providerVm.Models)
                m.IsAvailable = isConfigured;
        }

        RefreshAllModels();
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
                m.IsBusy = IsBusyStatus(status);
                m.StatusText = FormatStatus(status, m.IsDownloaded, m.IsAvailable, m.SupportsDownload);
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

    private void OnSelectedAccelerationOptionValueChanged(string value)
    {
        if (_isSyncingAccelerationSelection)
            return;

        var applyVersion = Interlocked.Increment(ref _accelerationApplyVersion);
        _ = ApplySelectedAccelerationOptionAsync(value, applyVersion);
    }

    private async Task ApplySelectedAccelerationOptionAsync(string value, int applyVersion)
    {
        var managesBusyState = false;
        await _accelerationApplyLock.WaitAsync();
        try
        {
            if (!IsCurrentAccelerationApply(applyVersion))
                return;

            var normalized = AppSettings.NormalizeLocalModelAcceleration(value);
            if (!string.Equals(value, normalized, StringComparison.Ordinal))
            {
                _isSyncingAccelerationSelection = true;
                SelectedAccelerationOptionValue = normalized;
                _isSyncingAccelerationSelection = false;
            }

            if (_settings.Current.LocalModelAcceleration != normalized)
                _settings.Save(_settings.Current with { LocalModelAcceleration = normalized });

            var plugin = GetDisplayTranscriptionPlugin();
            if (plugin is not null && ShouldShowAccelerationSection(plugin))
            {
                plugin.SetAccelerationPreference(
                    ModelManagerService.GetAccelerationPreference(normalized));
            }

            var displayModelId = GetDisplayModelId();
            var shouldReloadActiveModel = plugin is not null
                && ShouldShowAccelerationSection(plugin)
                && !string.IsNullOrWhiteSpace(displayModelId)
                && string.Equals(displayModelId, _modelManager.ActiveModelId, StringComparison.Ordinal);

            if (shouldReloadActiveModel)
            {
                IsBusy = true;
                BusyMessage = Loc.Instance["Models.LoadingModel"];
                managesBusyState = true;
                _accelerationBusyVersion = applyVersion;
            }

            if (shouldReloadActiveModel)
                await _modelManager.EnsureModelLoadedAsync(displayModelId);
        }
        catch (Exception ex)
        {
            if (IsCurrentAccelerationApply(applyVersion))
            {
                RefreshAccelerationStatus();
                if (IsAccelerationRestartRequired)
                    return;

                IsBusy = true;
                managesBusyState = true;
                _accelerationBusyVersion = applyVersion;

                BusyMessage = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
                await Task.Delay(2000);
            }
        }
        finally
        {
            if (IsCurrentAccelerationApply(applyVersion))
            {
                if (managesBusyState || _accelerationBusyVersion != 0)
                {
                    IsBusy = false;
                    BusyMessage = null;
                    _accelerationBusyVersion = 0;
                }

                RefreshAllModels();
            }

            _accelerationApplyLock.Release();
        }
    }

    private bool IsCurrentAccelerationApply(int applyVersion) =>
        applyVersion == Volatile.Read(ref _accelerationApplyVersion);

    internal static string FormatStatus(
        ModelStatus status,
        bool isDownloaded,
        bool isConfigured,
        bool supportsDownload) => status.Type switch
    {
        ModelStatusType.Downloading => $"Download {status.Progress:P0}",
        ModelStatusType.Loading => Loc.Instance["Models.StatusLoading"],
        ModelStatusType.Ready => Loc.Instance["Models.StatusReady"],
        ModelStatusType.Error => Loc.Instance.GetString("Models.StatusErrorFormat", status.ErrorMessage ?? ""),
        _ when !supportsDownload && !isConfigured => Loc.Instance["Models.StatusApiKeyRequired"],
        _ => isDownloaded ? Loc.Instance["Models.StatusDownloaded"] : ""
    };

    internal static bool IsBusyStatus(ModelStatus status) =>
        status.Type is ModelStatusType.Downloading or ModelStatusType.Loading;

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

    private void SyncSelectedAccelerationOption()
    {
        _isSyncingAccelerationSelection = true;
        SelectedAccelerationOptionValue = AppSettings.NormalizeLocalModelAcceleration(
            _settings.Current.LocalModelAcceleration);
        _isSyncingAccelerationSelection = false;
    }

    private static void InvokeOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher
            || dispatcher.HasShutdownStarted
            || dispatcher.HasShutdownFinished)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (TaskCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            action();
        }
    }

    private void RefreshActiveModelDetails()
    {
        var displayModelId = GetDisplayModelId();

        var activeModel = Providers
            .SelectMany(p => p.Models.Select(m => (Provider: p, Model: m)))
            .FirstOrDefault(x => x.Model.FullId == displayModelId);

        if (activeModel.Model is null)
        {
            ActiveProviderDisplayName = "None";
            ActiveModelDisplayName = "No model selected";
            ActiveModelStatusText = "";
            IsActiveModelReady = false;
            IsActiveModelBusy = false;
            RefreshAccelerationStatus();
            return;
        }

        ActiveProviderDisplayName = activeModel.Provider.DisplayName;
        ActiveModelDisplayName = activeModel.Model.DisplayName;
        ActiveModelStatusText = activeModel.Model.StatusText;
        IsActiveModelReady = activeModel.Model.IsReady;
        IsActiveModelBusy = activeModel.Model.IsBusy;
        RefreshAccelerationStatus();
    }

    private void RefreshAccelerationStatus()
    {
        var plugin = GetDisplayTranscriptionPlugin();
        IsAccelerationSectionVisible = ShouldShowAccelerationSection(plugin);
        if (!IsAccelerationSectionVisible)
        {
            AccelerationStatusText = "";
            SyncAccelerationRestartPrompt(null);
            return;
        }

        var status = plugin!.AccelerationStatus;
        AccelerationStatusText = FormatAccelerationStatus(status);
        SyncAccelerationRestartPrompt(status);
    }

    private void SyncAccelerationRestartPrompt(TranscriptionAccelerationStatus? status)
    {
        if (status?.RequiresRestart != true)
        {
            IsAccelerationRestartRequired = false;
            AccelerationRestartMessage = "";
            _accelerationRestartNotificationShown = false;
            return;
        }

        IsAccelerationRestartRequired = true;
        AccelerationRestartMessage = Loc.Instance["Models.AccelerationRestartRequiredMessage"];

        if (_accelerationRestartNotificationShown)
            return;

        _accelerationRestartNotificationShown = true;
        _notifications?.ShowBalloon(
            Loc.Instance["Models.AccelerationRestartBalloonTitle"],
            Loc.Instance["Models.AccelerationRestartBalloonMessage"],
            RestartForAcceleration);
    }

    private ITranscriptionEnginePlugin? GetDisplayTranscriptionPlugin()
    {
        var displayModelId = GetDisplayModelId();

        if (string.IsNullOrWhiteSpace(displayModelId) || !ModelManagerService.IsPluginModel(displayModelId))
            return null;

        var (pluginId, _) = ModelManagerService.ParsePluginModelId(displayModelId);
        return _modelManager.PluginManager.TranscriptionEngines
            .FirstOrDefault(e => string.Equals(
                e.GetTranscriptionSelectionId(),
                pluginId,
                StringComparison.OrdinalIgnoreCase));
    }

    private string? GetDisplayModelId() =>
        !string.IsNullOrWhiteSpace(SelectedModelOptionId)
            ? SelectedModelOptionId
            : ActiveModelId;

    internal static string FormatAccelerationStatus(TranscriptionAccelerationStatus status) =>
        string.IsNullOrWhiteSpace(status.Detail)
            ? status.DisplayText
            : $"{status.DisplayText}: {status.Detail}";

    internal static bool ShouldShowAccelerationSection(ITranscriptionEnginePlugin? plugin)
    {
        if (plugin is null)
            return false;

        return plugin.SupportsModelDownload
            || plugin.SupportedAccelerationBackends.Any(backend =>
                backend != TranscriptionAccelerationBackend.Cpu);
    }

    [RelayCommand]
    private void DeleteModel(string modelId)
    {
        _modelManager.DeleteModel(modelId);
    }

    [RelayCommand]
    private void RestartForAcceleration()
    {
        _appRestart?.RestartMinimized();
    }

    partial void OnIsModelStorageBusyChanged(bool value)
    {
        MoveModelStorageCommand.NotifyCanExecuteChanged();
        ResetModelStoragePathCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveModelStorage() => !IsModelStorageBusy;
    private bool CanResetModelStoragePath() => !IsModelStorageBusy;

    [RelayCommand(CanExecute = nameof(CanMoveModelStorage))]
    private async Task MoveModelStorage()
    {
        IsModelStorageBusy = true;
        HasModelStorageError = false;
        ModelStorageStatusText = Loc.Instance["Models.StorageMoving"];

        try
        {
            await _modelStorage.MoveDownloadsAndUsePathAsync(ModelStoragePath);
            RefreshModelStorage();
            RefreshAllModels();
            ModelStorageStatusText = Loc.Instance["Models.StorageMoved"];
        }
        catch (Exception ex)
        {
            HasModelStorageError = true;
            ModelStorageStatusText = Loc.Instance.GetString("Models.StorageErrorFormat", ex.Message);
        }
        finally
        {
            IsModelStorageBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanResetModelStoragePath))]
    private void ResetModelStoragePath()
    {
        if (IsModelStorageBusy)
            return;

        HasModelStorageError = false;
        _modelStorage.ResetToDefault();
        RefreshModelStorage();
        RefreshAllModels();
    }

    private void RefreshModelStorage()
    {
        ResolvedModelStoragePath = _modelStorage.ResolvedModelStoragePath;
        ModelStoragePath = ResolvedModelStoragePath;
        if (!IsModelStorageBusy && !HasModelStorageError)
            ModelStorageStatusText = Loc.Instance.GetString("Models.StorageCurrentFormat", ResolvedModelStoragePath);
    }
}

/// <summary>
/// Provides provider view model behavior.
/// </summary>
public partial class ProviderViewModel : ObservableObject
{
    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId { get; }
    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; }
    /// <summary>
    /// Gets whether has llm translation.
    /// </summary>
    public bool HasLlmTranslation { get; }
    /// <summary>
    /// Gets whether supports download.
    /// </summary>
    public bool SupportsDownload { get; }
    /// <summary>
    /// Gets the models.
    /// </summary>
    public ObservableCollection<ModelItemViewModel> Models { get; } = [];

    [ObservableProperty] private bool _isConfigured;

    /// <summary>
    /// Initializes a new instance of the ProviderViewModel class.
    /// </summary>
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

/// <summary>
/// Provides model item view model behavior.
/// </summary>
public partial class ModelItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the full id.
    /// </summary>
    public string FullId { get; }
    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; }
    /// <summary>
    /// Gets the size description.
    /// </summary>
    public string? SizeDescription { get; }
    /// <summary>
    /// Gets whether is recommended.
    /// </summary>
    public bool IsRecommended { get; }
    /// <summary>
    /// Gets the language count.
    /// </summary>
    public int LanguageCount { get; }
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation { get; }
    /// <summary>
    /// Gets whether supports download.
    /// </summary>
    public bool SupportsDownload { get; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    /// <summary>
    /// Initializes a new instance of the ModelItemViewModel class.
    /// </summary>
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
        _isBusy = ModelManagerViewModel.IsBusyStatus(status);
        _statusText = ModelManagerViewModel.FormatStatus(status, isDownloaded, isAvailable, supportsDownload);
    }
}

/// <summary>
/// Provides model option view model behavior.
/// </summary>
public sealed class ModelOptionViewModel
{
    /// <summary>
    /// Gets the full id.
    /// </summary>
    public string FullId { get; }
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName { get; }
    /// <summary>
    /// Gets the model display name.
    /// </summary>
    public string ModelDisplayName { get; }
    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Initializes a new instance of the ModelOptionViewModel class.
    /// </summary>
    public ModelOptionViewModel(string fullId, string providerDisplayName, string modelDisplayName, string displayName)
    {
        FullId = fullId;
        ProviderDisplayName = providerDisplayName;
        ModelDisplayName = modelDisplayName;
        DisplayName = displayName;
    }
}

/// <summary>
/// Provides acceleration option view model behavior.
/// </summary>
public sealed class AccelerationOptionViewModel
{
    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }
    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Initializes a new instance of the AccelerationOptionViewModel class.
    /// </summary>
    public AccelerationOptionViewModel(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }
}
