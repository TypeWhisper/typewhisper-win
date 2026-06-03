using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services.Sync;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public sealed partial class CloudFolderSyncViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUserDataSyncStore _store;
    private readonly Func<bool> _canUseSync;
    private readonly TimeSpan _autoSyncDelay;
    private readonly Guid _localChangeObserverId;
    private readonly LicenseService? _license;
    private CloudFolderSyncState _state;
    private CancellationTokenSource? _scheduledSync;

    [ObservableProperty] private string? _selectedFolderPath;
    [ObservableProperty] private DateTime? _lastSyncDate;
    [ObservableProperty] private int _pendingChanges;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    public CloudFolderSyncViewModel(
        ISettingsService settings,
        IUserDataSyncStore store,
        LicenseService license)
        : this(settings, store, () => license.HasCommercialLicense, TimeSpan.FromSeconds(2))
    {
        _license = license;
        license.StatusChanged += RefreshEntitlementProperties;
    }

    public CloudFolderSyncViewModel(
        ISettingsService settings,
        IUserDataSyncStore store,
        Func<bool> canUseSync,
        TimeSpan autoSyncDelay)
    {
        _settings = settings;
        _store = store;
        _canUseSync = canUseSync;
        _autoSyncDelay = autoSyncDelay;
        _state = settings.Current.CloudFolderSyncState ?? new CloudFolderSyncState();
        _selectedFolderPath = settings.Current.CloudFolderSyncFolderPath;
        _lastSyncDate = _state.LastSyncAt;
        _localChangeObserverId = _store.ObserveLocalChanges(ScheduleSyncAfterLocalChange);

        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync, CanRunSyncNow);
        ClearFolderCommand = new RelayCommand(ClearFolder, () => HasSelectedFolder && !IsSyncing);
    }

    public IAsyncRelayCommand SyncNowCommand { get; }
    public IRelayCommand ClearFolderCommand { get; }

    public bool CanUseSync => _canUseSync();
    public bool ShowLockedState => !CanUseSync;
    public bool ShowSyncControls => CanUseSync;
    public bool HasSelectedFolder => !string.IsNullOrWhiteSpace(SelectedFolderPath);
    public string SelectedFolderDisplayName => HasSelectedFolder
        ? SelectedFolderPath!
        : Text("Premium.NoFolderSelected", "No folder selected");
    public string ProviderDisplayName => ProviderText(CloudFolderSyncProviderDetector.Detect(SelectedFolderPath));
    public string LastSyncDisplay => LastSyncDate is { } date
        ? date.ToLocalTime().ToString("g")
        : Text("Premium.Never", "Never");

    public void SetFolderPath(string folderPath)
    {
        if (IsSyncing || string.IsNullOrWhiteSpace(folderPath))
            return;

        var pathChanged = !string.Equals(SelectedFolderPath, folderPath, StringComparison.OrdinalIgnoreCase);
        if (pathChanged)
        {
            _scheduledSync?.Cancel();
            _state = new CloudFolderSyncState();
            PendingChanges = 0;
            LastSyncDate = null;
            StatusMessage = null;
            ErrorMessage = null;
        }

        SelectedFolderPath = folderPath;
        _settings.Save(_settings.Current with
        {
            CloudFolderSyncFolderPath = folderPath,
            CloudFolderSyncState = pathChanged ? null : _state
        });
        RefreshComputedProperties();
    }

    public void ClearFolder()
    {
        if (IsSyncing)
            return;

        _scheduledSync?.Cancel();
        SelectedFolderPath = null;
        _state = new CloudFolderSyncState();
        PendingChanges = 0;
        LastSyncDate = null;
        StatusMessage = null;
        ErrorMessage = null;
        _settings.Save(_settings.Current with
        {
            CloudFolderSyncFolderPath = null,
            CloudFolderSyncState = null
        });
        RefreshComputedProperties();
    }

    public async Task SyncNowAsync()
    {
        var folderPath = SelectedFolderPath;
        var state = _state;

        if (!HasSelectedFolder)
        {
            ErrorMessage = Text("Premium.ErrorChooseFolder", "Choose a sync folder first.");
            return;
        }

        if (!CanUseSync)
        {
            ErrorMessage = Text("Premium.ErrorCommercialRequired", "Cloud Folder Sync requires an active Commercial license.");
            return;
        }

        if (IsSyncing)
            return;

        ErrorMessage = null;
        IsSyncing = true;
        NotifyCommandStates();

        try
        {
            var result = await CloudFolderSyncEngine.SyncAsync(
                folderPath!,
                _store,
                state,
                new PaidEntitlements(CanUseCloudFolderSync: true));

            if (!string.Equals(SelectedFolderPath, folderPath, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(_state, state))
            {
                return;
            }

            LastSyncDate = result.SyncedAt;
            PendingChanges = 0;
            StatusMessage = Format(
                "Premium.StatusSyncedFormat",
                "Synced {0} changes.",
                result.OperationsWritten + result.MutationsApplied);
            _settings.Save(_settings.Current with
            {
                CloudFolderSyncFolderPath = folderPath,
                CloudFolderSyncState = state
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSyncing = false;
            NotifyCommandStates();
        }
    }

    public void Dispose()
    {
        _scheduledSync?.Cancel();
        if (_license is not null)
            _license.StatusChanged -= RefreshEntitlementProperties;
        _store.RemoveLocalChangeObserver(_localChangeObserverId);
    }

    partial void OnSelectedFolderPathChanged(string? value) => RefreshComputedProperties();
    partial void OnLastSyncDateChanged(DateTime? value) => OnPropertyChanged(nameof(LastSyncDisplay));
    partial void OnIsSyncingChanged(bool value) => NotifyCommandStates();

    private bool CanRunSyncNow() => CanUseSync && HasSelectedFolder && !IsSyncing;

    private void ScheduleSyncAfterLocalChange()
    {
        if (!HasSelectedFolder || !CanUseSync)
            return;

        PendingChanges++;
        _scheduledSync?.Cancel();
        var sync = new CancellationTokenSource();
        _scheduledSync = sync;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_autoSyncDelay, sync.Token);
                await SyncNowAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RefreshEntitlementProperties()
    {
        RefreshComputedProperties();
        NotifyCommandStates();
    }

    private void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(CanUseSync));
        OnPropertyChanged(nameof(ShowLockedState));
        OnPropertyChanged(nameof(ShowSyncControls));
        OnPropertyChanged(nameof(HasSelectedFolder));
        OnPropertyChanged(nameof(SelectedFolderDisplayName));
        OnPropertyChanged(nameof(ProviderDisplayName));
        ClearFolderCommand.NotifyCanExecuteChanged();
        SyncNowCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandStates()
    {
        SyncNowCommand.NotifyCanExecuteChanged();
        ClearFolderCommand.NotifyCanExecuteChanged();
    }

    private static string ProviderText(CloudFolderSyncProvider provider) =>
        provider switch
        {
            CloudFolderSyncProvider.ICloudDrive => Text("Premium.ProviderICloudDrive", "iCloud Drive"),
            CloudFolderSyncProvider.OneDrive => Text("Premium.ProviderOneDrive", "OneDrive"),
            CloudFolderSyncProvider.Dropbox => Text("Premium.ProviderDropbox", "Dropbox"),
            _ => Text("Premium.ProviderCustom", "Custom Folder")
        };

    private static string Text(string key, string fallback)
    {
        var value = Loc.Instance.GetString(key);
        return value == key ? fallback : value;
    }

    private static string Format(string key, string fallback, params object[] args)
    {
        var template = Text(key, fallback);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
