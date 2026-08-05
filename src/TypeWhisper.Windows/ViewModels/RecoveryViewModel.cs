using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Manages durable dictation recordings and recovery settings.
/// </summary>
public sealed partial class RecoveryViewModel : ObservableObject
{
    private readonly DictationRecoveryAudioStore _store;
    private readonly ISettingsService _settings;
    private readonly ManualAudioRecoveryService _manualRecovery;
    private readonly ModelManagerService _modelManager;
    private readonly LicenseService _license;
    private bool _loadingSettings;
    private CancellationTokenSource? _manualRecoveryCts;

    [ObservableProperty] private RecoveryRecordingItemViewModel? _selectedRecording;
    [ObservableProperty] private int _retentionDays = 30;
    [ObservableProperty] private string? _engineId;
    [ObservableProperty] private string? _modelId;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private string _task = "transcribe";
    [ObservableProperty] private bool _automaticFallbackEnabled;
    [ObservableProperty] private bool _workflowRequestRecoveryEnabled = true;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";

    /// <summary>
    /// Gets durable recordings ordered newest first.
    /// </summary>
    public ObservableCollection<RecoveryRecordingItemViewModel> Recordings { get; } = [];
    /// <summary>
    /// Gets allowed retention values.
    /// </summary>
    public ObservableCollection<RecoveryOption<int>> RetentionOptions { get; } = [];
    /// <summary>
    /// Gets available recovery engines.
    /// </summary>
    public ObservableCollection<RecoveryOption<string?>> EngineOptions { get; } = [];
    /// <summary>
    /// Gets models belonging to the selected engine.
    /// </summary>
    public ObservableCollection<RecoveryOption<string?>> ModelOptions { get; } = [];
    /// <summary>
    /// Gets recovery language options.
    /// </summary>
    public ObservableCollection<RecoveryOption<string>> LanguageOptions { get; } = [];
    /// <summary>
    /// Gets recovery task options.
    /// </summary>
    public ObservableCollection<RecoveryOption<string>> TaskOptions { get; } = [];

    /// <summary>
    /// Gets the private recovery storage location.
    /// </summary>
    public string StoragePath => TypeWhisperEnvironment.DictationRecoveryPath;
    /// <summary>
    /// Gets whether recordings exist.
    /// </summary>
    public bool HasRecordings => Recordings.Count > 0;
    /// <summary>
    /// Gets whether the selected recording can be recovered.
    /// </summary>
    public bool CanTranscribe =>
        SelectedRecording is not null
        && !IsTranscribing
        && !string.IsNullOrWhiteSpace(EngineId)
        && !string.IsNullOrWhiteSpace(ModelId);
    /// <summary>
    /// Gets whether automatic fallback is licensed.
    /// </summary>
    public bool HasFallbackLicense => _license.HasCommercialLicense || _license.IsSupporter;
    /// <summary>
    /// Gets the localized fallback license state.
    /// </summary>
    public string FallbackLicenseStatus => HasFallbackLicense
        ? Loc.Instance["Recovery.FallbackLicensed"]
        : Loc.Instance["Recovery.FallbackRequiresLicense"];

    /// <summary>
    /// Creates a recovery page view model.
    /// </summary>
    public RecoveryViewModel(
        DictationRecoveryAudioStore store,
        ISettingsService settings,
        ManualAudioRecoveryService manualRecovery,
        ModelManagerService modelManager,
        LicenseService license)
    {
        _store = store;
        _settings = settings;
        _manualRecovery = manualRecovery;
        _modelManager = modelManager;
        _license = license;

        Recordings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecordings));
            DeleteAllCommand.NotifyCanExecuteChanged();
        };
        _store.Changed += () => InvokeOnUiThread(RefreshSnapshot);
        _settings.SettingsChanged += updated => InvokeOnUiThread(() => LoadSettings(updated));
        _modelManager.PluginManager.PluginStateChanged += (_, _) => InvokeOnUiThread(RefreshEngineOptions);
        _license.PropertyChanged += OnLicensePropertyChanged;
        Loc.Instance.LanguageChanged += (_, _) => InvokeOnUiThread(RefreshOptionLabels);

        RefreshOptionLabels();
        LoadSettings(_settings.Current);
        RefreshSnapshot();
    }

    /// <summary>
    /// Selects a durable recording by store id, or the newest recording when no id is supplied.
    /// </summary>
    public void SelectRecording(string? id = null)
    {
        SelectedRecording = string.IsNullOrWhiteSpace(id)
            ? Recordings.FirstOrDefault()
            : Recordings.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        ErrorText = "";
        try
        {
            await _store.RefreshAsync();
            RefreshSnapshot();
        }
        catch (Exception ex)
        {
            ErrorText = HistoryWorkflowRetryService.SanitizeFailure(ex);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private bool CanDeleteSelected() => SelectedRecording is not null && !IsTranscribing;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelected()
    {
        if (SelectedRecording is null)
            return;
        await _store.DeleteAsync(SelectedRecording.Id);
        RefreshSnapshot();
    }

    private bool CanDeleteAll() => HasRecordings && !IsTranscribing;

    [RelayCommand(CanExecute = nameof(CanDeleteAll))]
    private async Task DeleteAll()
    {
        var confirmed = MessageBox.Show(
            Loc.Instance["Recovery.DeleteAllConfirm"],
            Loc.Instance["Recovery.DeleteAllTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
            return;

        await _store.DeleteAllAsync();
        RefreshSnapshot();
    }

    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeSelected()
    {
        if (SelectedRecording is null)
            return;
        var selectedId = SelectedRecording.Id;
        _manualRecoveryCts?.Dispose();
        _manualRecoveryCts = new CancellationTokenSource();
        IsTranscribing = true;
        ErrorText = "";
        StatusText = Loc.Instance["Recovery.Transcribing"];
        TranscribeSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DeleteAllCommand.NotifyCanExecuteChanged();
        try
        {
            var transcriptionTask = string.Equals(Task, "translate", StringComparison.OrdinalIgnoreCase)
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;
            _ = await _manualRecovery.RecoverAsync(
                selectedId,
                new FileTranscriptionProcessOptions(
                    EngineId,
                    ModelId,
                    Language,
                    transcriptionTask,
                    string.Equals(Language, "auto", StringComparison.OrdinalIgnoreCase) ? [] : [Language]),
                progress => InvokeOnUiThread(() => StatusText = progress.StatusText),
                _manualRecoveryCts.Token);
            StatusText = Loc.Instance["Recovery.SavedToHistory"];
            RefreshSnapshot();
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex)
        {
            ErrorText = HistoryWorkflowRetryService.SanitizeFailure(ex);
            StatusText = "";
        }
        finally
        {
            IsTranscribing = false;
            _manualRecoveryCts.Dispose();
            _manualRecoveryCts = null;
            TranscribeSelectedCommand.NotifyCanExecuteChanged();
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            DeleteAllCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelTranscription() => _manualRecoveryCts?.Cancel();

    partial void OnSelectedRecordingChanged(RecoveryRecordingItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanTranscribe));
        TranscribeSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnRetentionDaysChanged(int value)
    {
        if (_loadingSettings) return;
        SaveSettings(settings => settings with { DictationRecoveryRetentionDays = value });
        _ = _store.SetRetentionAsync(value);
    }

    partial void OnEngineIdChanged(string? value)
    {
        RebuildModelOptions();
        if (ModelId is not null && !ModelOptions.Any(option => option.Value == ModelId))
            ModelId = null;
        OnPropertyChanged(nameof(CanTranscribe));
        TranscribeSelectedCommand.NotifyCanExecuteChanged();
        if (!_loadingSettings)
            SaveSettings(settings => settings with { DictationRecoveryEngineId = value });
    }

    partial void OnModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(CanTranscribe));
        TranscribeSelectedCommand.NotifyCanExecuteChanged();
        if (!_loadingSettings)
            SaveSettings(settings => settings with { DictationRecoveryModelId = value });
    }

    partial void OnLanguageChanged(string value)
    {
        if (!_loadingSettings)
            SaveSettings(settings => settings with { DictationRecoveryLanguage = value });
    }

    partial void OnTaskChanged(string value)
    {
        if (!_loadingSettings)
            SaveSettings(settings => settings with { DictationRecoveryTask = value });
    }

    partial void OnAutomaticFallbackEnabledChanged(bool value)
    {
        if (!_loadingSettings)
            SaveSettings(settings => settings with { DictationRecoveryAutomaticFallbackEnabled = value });
    }

    partial void OnWorkflowRequestRecoveryEnabledChanged(bool value)
    {
        if (!_loadingSettings)
            SaveSettings(settings => settings with { WorkflowRequestRecoveryEnabled = value });
    }

    private void LoadSettings(AppSettings settings)
    {
        _loadingSettings = true;
        try
        {
            RetentionDays = settings.DictationRecoveryRetentionDays;
            EngineId = settings.DictationRecoveryEngineId;
            ModelId = settings.DictationRecoveryModelId;
            Language = settings.DictationRecoveryLanguage;
            Task = settings.DictationRecoveryTask;
            AutomaticFallbackEnabled = settings.DictationRecoveryAutomaticFallbackEnabled;
            WorkflowRequestRecoveryEnabled = settings.WorkflowRequestRecoveryEnabled;
            RefreshEngineOptions();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveSettings(Func<AppSettings, AppSettings> update) => _settings.Save(update(_settings.Current));

    private void RefreshSnapshot()
    {
        var selectedId = SelectedRecording?.Id;
        Recordings.Clear();
        foreach (var descriptor in _store.Recordings)
            Recordings.Add(new RecoveryRecordingItemViewModel(descriptor));
        SelectedRecording = Recordings.FirstOrDefault(item => item.Id == selectedId)
                            ?? Recordings.FirstOrDefault();
    }

    private void RefreshOptionLabels()
    {
        ReplaceCollection(RetentionOptions,
        [
            new(-1, Loc.Instance["Recovery.RetentionImmediately"]),
            new(1, Loc.Instance.GetString("Recovery.RetentionDaysFormat", 1)),
            new(7, Loc.Instance.GetString("Recovery.RetentionDaysFormat", 7)),
            new(30, Loc.Instance.GetString("Recovery.RetentionDaysFormat", 30)),
            new(60, Loc.Instance.GetString("Recovery.RetentionDaysFormat", 60)),
            new(90, Loc.Instance.GetString("Recovery.RetentionDaysFormat", 90)),
            new(180, Loc.Instance.GetString("Recovery.RetentionDaysFormat", 180)),
            new(0, Loc.Instance["Recovery.RetentionNever"])
        ]);
        ReplaceCollection(LanguageOptions,
        [
            new("auto", Loc.Instance["Profiles.Auto"]),
            new("de", "Deutsch"), new("en", "English"), new("fr", "Francais"),
            new("es", "Espanol"), new("ja", "日本語"), new("ru", "Русский"), new("zh", "中文")
        ]);
        ReplaceCollection(TaskOptions,
        [
            new("transcribe", Loc.Instance["Recovery.TaskTranscribe"]),
            new("translate", Loc.Instance["Recovery.TaskTranslate"])
        ]);
        RefreshEngineOptions();
        OnPropertyChanged(nameof(FallbackLicenseStatus));
    }

    private void RefreshEngineOptions()
    {
        var options = new List<RecoveryOption<string?>>
        {
            new(null, Loc.Instance["Recovery.SelectEngine"])
        };
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines
                     .DistinctBy(candidate => candidate.GetTranscriptionSelectionId(), StringComparer.OrdinalIgnoreCase))
        {
            var name = engine.IsConfigured
                ? engine.ProviderDisplayName
                : Loc.Instance.GetString("WatchFolder.EngineNotReadyFormat", engine.ProviderDisplayName);
            options.Add(new(engine.GetTranscriptionSelectionId(), name));
        }
        ReplaceCollection(EngineOptions, options);
        RebuildModelOptions();
    }

    private void RebuildModelOptions()
    {
        var options = new List<RecoveryOption<string?>>
        {
            new(null, Loc.Instance["Recovery.SelectModel"])
        };
        var engine = _modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
            string.Equals(candidate.GetTranscriptionSelectionId(), EngineId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.ProviderId, EngineId, StringComparison.OrdinalIgnoreCase));
        if (engine is not null)
        {
            options.AddRange(engine.TranscriptionModels
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new RecoveryOption<string?>(model.Id, model.DisplayName)));
        }
        ReplaceCollection(ModelOptions, options);
    }

    private void OnLicensePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LicenseService.HasCommercialLicense) or nameof(LicenseService.IsSupporter))
        {
            InvokeOnUiThread(() =>
            {
                OnPropertyChanged(nameof(HasFallbackLicense));
                OnPropertyChanged(nameof(FallbackLicenseStatus));
            });
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private static void InvokeOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }
}

/// <summary>
/// Represents a localized selectable recovery setting.
/// </summary>
public sealed record RecoveryOption<T>(T Value, string DisplayName);

/// <summary>
/// Presents one durable recovery recording.
/// </summary>
public sealed class RecoveryRecordingItemViewModel
{
    /// <summary>Creates an item from a safe store descriptor.</summary>
    public RecoveryRecordingItemViewModel(RecoveryRecordingDescriptor descriptor) => Descriptor = descriptor;
    /// <summary>Gets the safe store descriptor.</summary>
    public RecoveryRecordingDescriptor Descriptor { get; }
    /// <summary>Gets the internal store id.</summary>
    public string Id => Descriptor.Id;
    /// <summary>Gets the creation timestamp.</summary>
    public DateTimeOffset CreatedAt => Descriptor.CreatedAt.ToLocalTime();
    /// <summary>Gets the audio duration.</summary>
    public double DurationSeconds => Descriptor.DurationSeconds;
    /// <summary>Gets a duration display value.</summary>
    public string DurationLabel => $"{Descriptor.DurationSeconds:F1} s";
    /// <summary>Gets a file size display value.</summary>
    public string SizeLabel => Descriptor.FileSizeBytes >= 1024 * 1024
        ? $"{Descriptor.FileSizeBytes / 1024d / 1024d:F1} MB"
        : $"{Math.Max(1, Descriptor.FileSizeBytes / 1024d):F0} KB";
}
