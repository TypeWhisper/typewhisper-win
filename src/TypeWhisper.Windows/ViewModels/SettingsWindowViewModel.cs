using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Aggregates all sub-view models and controls routed navigation inside the settings shell.
/// </summary>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private static SettingsRoute _lastOpenedRoute = SettingsRoute.Dashboard;

    public SettingsViewModel Settings { get; }
    public ModelManagerViewModel ModelManager { get; }
    public HistoryViewModel History { get; }
    public DictionaryViewModel Dictionary { get; }
    public SnippetsViewModel Snippets { get; }
    public WorkflowsViewModel Workflows { get; }
    public DashboardViewModel Dashboard { get; }
    public PluginsViewModel Plugins { get; }
    public CloudFolderSyncViewModel CloudFolderSync { get; }
    public AudioRecorderViewModel Recorder { get; }
    public FileTranscriptionViewModel FileTranscription { get; }

    private readonly UpdateService _updateService;
    private readonly ISettingsService _settingsService;
    private readonly IErrorLogService _errorLog;
    private readonly DispatcherTimer _indicatorPreviewTimer = new(DispatcherPriority.Background);
    private readonly DateTime _indicatorPreviewStartedAt = DateTime.UtcNow;
    private bool _isSyncingUpdateChannel;
    private double _indicatorPreviewPhase;

    [ObservableProperty] private UserControl? _currentSection;
    [ObservableProperty] private SettingsRoute _currentRoute = _lastOpenedRoute;
    [ObservableProperty] private string _currentPageTitle = "";
    [ObservableProperty] private string _currentPageSubtitle = "";
    [ObservableProperty] private SettingsPageMetadata _currentPageMetadata = new(SettingsPageKind.PreferencePage);
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private int _pendingFileImporterRequestId;
    [ObservableProperty] private ReleaseChannel _selectedUpdateChannel;
    [ObservableProperty] private float _audioLevel = 0.18f;
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private string _partialText = "";

    public string CurrentAppVersion => _updateService.CurrentVersion;
    public string CurrentAppVersionDisplay => BuildCurrentAppVersionDisplay();
    public double CurrentPageContentWidth => CurrentPageMetadata.ContentWidth;
    public bool CurrentPageShowsSummaryRow => CurrentPageMetadata.ShowsSummaryRow;
    public bool CurrentPageUsesStickyActions => CurrentPageMetadata.UsesStickyActions;
    public string SelectedUpdateChannelDescription =>
        UpdateChannelOptions.FirstOrDefault(option => option.Value == SelectedUpdateChannel)?.Description ?? string.Empty;
    public ObservableCollection<ErrorLogEntry> ErrorLogEntries { get; } = [];
    public ObservableCollection<ReleaseChannelOption> UpdateChannelOptions { get; } = [];
    public bool HasErrorLogEntries => ErrorLogEntries.Count > 0;
    public ObservableCollection<SettingsNavigationGroup> NavigationGroups { get; } = [];
    public IndicatorStyle IndicatorStyle => Settings.IndicatorStyle;
    public OverlayPosition OverlayPosition => Settings.OverlayPosition;
    public OverlayWidget LeftWidget => Settings.OverlayLeftWidget;
    public OverlayWidget RightWidget => Settings.OverlayRightWidget;
    public bool LiveTranscriptionEnabled => Settings.LiveTranscriptionEnabled;
    public double LiveTranscriptionFontSize => Settings.LiveTranscriptionFontSize;
    public DictationState State => DictationState.Recording;
    public string StatusText => Loc.Instance["Appearance.PreviewStatus"];
    public string? ActiveWorkflowName => Loc.Instance["Widget.Workflow"];
    public string? ActiveProcessName => "TypeWhisper";
    public HotkeyMode? CurrentHotkeyMode => HotkeyMode.Toggle;
    public bool ShowInlineFeedback => false;
    public string? FeedbackText => null;
    public bool FeedbackIsError => false;
    public bool ShowBuiltInPartialPreview =>
        AppearanceIndicatorPreviewPresentation.ShouldShowPartialText(
            LiveTranscriptionEnabled,
            IndicatorStyle);

    private readonly Dictionary<SettingsRoute, Func<UserControl>> _sectionFactories = [];
    private readonly Dictionary<SettingsRoute, UserControl> _sectionCache = [];
    private readonly Dictionary<SettingsRoute, SettingsNavigationItem> _navigationLookup = [];

    public SettingsWindowViewModel(
        SettingsViewModel settings,
        ModelManagerViewModel modelManager,
        HistoryViewModel history,
        DictionaryViewModel dictionary,
        SnippetsViewModel snippets,
        WorkflowsViewModel workflows,
        DashboardViewModel dashboard,
        PluginsViewModel plugins,
        CloudFolderSyncViewModel cloudFolderSync,
        AudioRecorderViewModel recorder,
        FileTranscriptionViewModel fileTranscription,
        UpdateService updateService,
        ISettingsService settingsService,
        IErrorLogService errorLog)
    {
        Settings = settings;
        ModelManager = modelManager;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Workflows = workflows;
        Dashboard = dashboard;
        Plugins = plugins;
        CloudFolderSync = cloudFolderSync;
        Recorder = recorder;
        FileTranscription = fileTranscription;
        _updateService = updateService;
        _settingsService = settingsService;
        _errorLog = errorLog;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                RefreshUpdateChannelOptions();
                RefreshIndicatorPreviewText();
                BuildNavigation();
                SyncRouteMetadata(CurrentRoute);
                SyncNavigationSelection();
                OnPropertyChanged(nameof(CurrentAppVersionDisplay));
                OnPropertyChanged(nameof(SelectedUpdateChannelDescription));
            });
        };

        RefreshUpdateChannelOptions();
        SyncSelectedUpdateChannel(_settingsService.Current);
        RefreshIndicatorPreviewText();
        BuildNavigation();
        RefreshErrorLog();
        _errorLog.EntriesChanged += RefreshErrorLog;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        _settingsService.SettingsChanged += SyncSelectedUpdateChannel;
        _indicatorPreviewTimer.Interval = TimeSpan.FromMilliseconds(140);
        _indicatorPreviewTimer.Tick += (_, _) => TickIndicatorPreview();
        _indicatorPreviewTimer.Start();
        SyncRouteMetadata(CurrentRoute);
        SyncNavigationSelection();
    }

    public string CurrentSectionName => CurrentRoute.ToString();

    [RelayCommand]
    private async Task NavigateToRoute(SettingsRoute route)
    {
        Open(route);
        if (route == SettingsRoute.History)
            await History.LoadAsync();
    }

    [RelayCommand]
    private Task NavigateToItem(SettingsNavigationItem? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return NavigateToRoute(item.Route);
    }

    [RelayCommand]
    private Task OpenHistory() => NavigateToRoute(SettingsRoute.History);

    [RelayCommand]
    private Task OpenIntegrations() => NavigateToRoute(SettingsRoute.Integrations);

    [RelayCommand]
    private Task OpenShortcuts() => NavigateToRoute(SettingsRoute.Shortcuts);

    [RelayCommand]
    private void OpenFileImporter()
    {
        Open(SettingsRoute.FileTranscription);
        PendingFileImporterRequestId++;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = Loc.Instance["Update.Checking"];

        await _updateService.CheckForUpdatesAsync();

        IsCheckingForUpdates = false;
        if (_updateService.IsUpdateAvailable)
        {
            IsUpdateAvailable = true;
            UpdateStatusText = Loc.Instance.GetString("Update.AvailableFormat", _updateService.AvailableVersion ?? "");
        }
        else
        {
            IsUpdateAvailable = false;
            UpdateStatusText = Loc.Instance["Update.UpToDate"];
        }
    }

    partial void OnSelectedUpdateChannelChanged(ReleaseChannel value)
    {
        OnPropertyChanged(nameof(SelectedUpdateChannelDescription));

        if (_isSyncingUpdateChannel)
            return;

        _settingsService.Save(_settingsService.Current with
        {
            UpdateChannel = UpdateService.ToSettingsValue(value)
        });
        _updateService.SwitchChannel(value);
        IsUpdateAvailable = false;
        UpdateStatusText = Loc.Instance.GetString("Update.ChannelChangedFormat", FormatReleaseChannelDisplayName(value));
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        UpdateStatusText = Loc.Instance["Update.Downloading"];
        await _updateService.DownloadAndApplyAsync();
        UpdateStatusText = Loc.Instance["Update.Failed"];
    }

    [RelayCommand]
    private void ClearErrorLog()
    {
        _errorLog.ClearAll();
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"typewhisper-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            var json = _errorLog.ExportDiagnostics();
            System.IO.File.WriteAllText(dialog.FileName, json);
        }
    }

    [RelayCommand]
    private void OpenSetupWizard()
    {
        var window = App.Services.GetRequiredService<WelcomeWindow>();
        window.Show();
    }

    public void RegisterSection(SettingsRoute route, Func<UserControl> factory)
    {
        _sectionFactories[route] = factory;
    }

    public void NavigateToDefault()
    {
        Open(_lastOpenedRoute);
    }

    public void Open(SettingsRoute route)
    {
        if (!_sectionFactories.ContainsKey(route))
            return;

        if (!_sectionCache.TryGetValue(route, out var section))
        {
            section = _sectionFactories[route]();
            _sectionCache[route] = section;
        }

        CurrentSection = section;
        CurrentRoute = route;
        _lastOpenedRoute = route;

        if (route is SettingsRoute.Dictation or SettingsRoute.Integrations)
            ModelManager.RefreshPluginAvailability();
    }

    internal bool FocusInstalledPlugin(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return false;

        Open(SettingsRoute.Integrations);
        return Plugins.FocusInstalledPlugin(pluginId);
    }

    public bool TryConsumePendingFileImporterRequest()
    {
        if (PendingFileImporterRequestId == 0)
            return false;

        PendingFileImporterRequestId = 0;
        return true;
    }

    partial void OnCurrentRouteChanged(SettingsRoute value)
    {
        SyncNavigationSelection();
        SyncRouteMetadata(value);
        OnPropertyChanged(nameof(CurrentSectionName));
    }

    private void RefreshErrorLog()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ErrorLogEntries.Clear();
            foreach (var entry in _errorLog.Entries)
                ErrorLogEntries.Add(entry);
            OnPropertyChanged(nameof(HasErrorLogEntries));
        });
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IndicatorStyle))
        {
            OnPropertyChanged(nameof(IndicatorStyle));
            OnPropertyChanged(nameof(ShowBuiltInPartialPreview));
        }

        if (e.PropertyName == nameof(SettingsViewModel.LiveTranscriptionEnabled))
        {
            OnPropertyChanged(nameof(LiveTranscriptionEnabled));
            OnPropertyChanged(nameof(ShowBuiltInPartialPreview));
        }

        if (e.PropertyName == nameof(SettingsViewModel.LiveTranscriptionFontSize))
            OnPropertyChanged(nameof(LiveTranscriptionFontSize));

        if (e.PropertyName == nameof(SettingsViewModel.OverlayPosition))
            OnPropertyChanged(nameof(OverlayPosition));

        if (e.PropertyName == nameof(SettingsViewModel.OverlayLeftWidget))
            OnPropertyChanged(nameof(LeftWidget));

        if (e.PropertyName == nameof(SettingsViewModel.OverlayRightWidget))
            OnPropertyChanged(nameof(RightWidget));
    }

    private void TickIndicatorPreview()
    {
        _indicatorPreviewPhase += 0.42;
        AudioLevel = 0.16f + (float)((Math.Sin(_indicatorPreviewPhase) + 1.0) * 0.18);
        RecordingSeconds = (DateTime.UtcNow - _indicatorPreviewStartedAt).TotalSeconds;
    }

    private void RefreshIndicatorPreviewText()
    {
        PartialText = Loc.Instance["Appearance.PreviewLiveSample"];
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActiveWorkflowName));
    }

    private void SyncSelectedUpdateChannel(AppSettings settings)
    {
        DispatchToUi(() =>
        {
            var resolvedChannel = UpdateService.ResolveReleaseChannel(settings.UpdateChannel, _updateService.CurrentVersion);
            _isSyncingUpdateChannel = true;
            SelectedUpdateChannel = resolvedChannel;
            _isSyncingUpdateChannel = false;
            OnPropertyChanged(nameof(SelectedUpdateChannelDescription));
        });
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private void RefreshUpdateChannelOptions()
    {
        UpdateChannelOptions.Clear();
        UpdateChannelOptions.Add(new ReleaseChannelOption(
            ReleaseChannel.Stable,
            Loc.Instance["Update.ChannelStable"],
            Loc.Instance["Update.ChannelStableDescription"]));
        UpdateChannelOptions.Add(new ReleaseChannelOption(
            ReleaseChannel.ReleaseCandidate,
            Loc.Instance["Update.ChannelReleaseCandidate"],
            Loc.Instance["Update.ChannelReleaseCandidateDescription"]));
        UpdateChannelOptions.Add(new ReleaseChannelOption(
            ReleaseChannel.Daily,
            Loc.Instance["Update.ChannelDaily"],
            Loc.Instance["Update.ChannelDailyDescription"]));
    }

    private string BuildCurrentAppVersionDisplay()
    {
        var installedChannel = UpdateService.InferReleaseChannel(CurrentAppVersion);
        if (installedChannel == ReleaseChannel.Stable)
            return Loc.Instance.GetString("Info.VersionFormat", CurrentAppVersion);

        return Loc.Instance.GetString(
            "Info.VersionWithChannelFormat",
            CurrentAppVersion,
            FormatReleaseChannelDisplayName(installedChannel));
    }

    private static string FormatReleaseChannelDisplayName(ReleaseChannel channel)
    {
        return channel switch
        {
            ReleaseChannel.ReleaseCandidate => Loc.Instance["Update.ChannelReleaseCandidate"],
            ReleaseChannel.Daily => Loc.Instance["Update.ChannelDaily"],
            _ => Loc.Instance["Update.ChannelStable"]
        };
    }

    private void BuildNavigation()
    {
        NavigationGroups.Clear();
        _navigationLookup.Clear();

        foreach (var group in SettingsNavigationCatalog.Build(key => Loc.Instance[key]))
        {
            NavigationGroups.Add(group);
            foreach (var item in group.Items)
                _navigationLookup[item.Route] = item;
        }
    }

    private void SyncNavigationSelection()
    {
        foreach (var item in _navigationLookup.Values)
            item.IsSelected = item.Route == CurrentRoute;
    }

    private void SyncRouteMetadata(SettingsRoute route)
    {
        CurrentPageMetadata = route switch
        {
            SettingsRoute.Workflows => new SettingsPageMetadata(SettingsPageKind.GuidedEditorPage, 1180, true, true),
            SettingsRoute.Snippets => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1040),
            SettingsRoute.Integrations => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1120),
            SettingsRoute.History => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1100),
            _ => new SettingsPageMetadata(SettingsPageKind.PreferencePage, 980)
        };

        CurrentPageTitle = route switch
        {
            SettingsRoute.Dashboard => Loc.Instance["Nav.Dashboard"],
            SettingsRoute.Dictation => Loc.Instance["Nav.Dictation"],
            SettingsRoute.Shortcuts => Loc.Instance["Nav.Shortcuts"],
            SettingsRoute.FileTranscription => Loc.Instance["Nav.FileTranscription"],
            SettingsRoute.Recorder => Loc.Instance["Nav.Recorder"],
            SettingsRoute.History => Loc.Instance["Nav.History"],
            SettingsRoute.Dictionary => Loc.Instance["Nav.Dictionary"],
            SettingsRoute.Snippets => Loc.Instance["Nav.Snippets"],
            SettingsRoute.Workflows => Loc.Instance["Nav.Workflows"],
            SettingsRoute.Integrations => Loc.Instance["Nav.Plugins"],
            SettingsRoute.General => Loc.Instance["Nav.General"],
            SettingsRoute.Appearance => Loc.Instance["Nav.Appearance"],
            SettingsRoute.Advanced => Loc.Instance["Nav.Advanced"],
            SettingsRoute.Premium => Loc.Instance["Nav.Premium"],
            SettingsRoute.License => Loc.Instance["Nav.License"],
            SettingsRoute.About => Loc.Instance["Nav.About"],
            _ => Loc.Instance["Settings.WindowTitle"]
        };

        CurrentPageSubtitle = route switch
        {
            SettingsRoute.Dashboard => Loc.Instance["Page.DashboardSubtitle"],
            SettingsRoute.Dictation => Loc.Instance["Page.DictationSubtitle"],
            SettingsRoute.Shortcuts => Loc.Instance["Page.ShortcutsSubtitle"],
            SettingsRoute.FileTranscription => Loc.Instance["Page.FileTranscriptionSubtitle"],
            SettingsRoute.Recorder => Loc.Instance["Page.RecorderSubtitle"],
            SettingsRoute.History => Loc.Instance["Page.HistorySubtitle"],
            SettingsRoute.Dictionary => Loc.Instance["Page.DictionarySubtitle"],
            SettingsRoute.Snippets => Loc.Instance["Page.SnippetsSubtitle"],
            SettingsRoute.Workflows => Loc.Instance["Page.WorkflowsSubtitle"],
            SettingsRoute.Integrations => Loc.Instance["Page.IntegrationsSubtitle"],
            SettingsRoute.General => Loc.Instance["Page.GeneralSubtitle"],
            SettingsRoute.Appearance => Loc.Instance["Page.AppearanceSubtitle"],
            SettingsRoute.Advanced => Loc.Instance["Page.AdvancedSubtitle"],
            SettingsRoute.Premium => Loc.Instance["Page.PremiumSubtitle"],
            SettingsRoute.License => Loc.Instance["Page.LicenseSubtitle"],
            SettingsRoute.About => Loc.Instance["Page.AboutSubtitle"],
            _ => string.Empty
        };
    }

    partial void OnCurrentPageMetadataChanged(SettingsPageMetadata value)
    {
        OnPropertyChanged(nameof(CurrentPageContentWidth));
        OnPropertyChanged(nameof(CurrentPageShowsSummaryRow));
        OnPropertyChanged(nameof(CurrentPageUsesStickyActions));
    }
}

public sealed record ReleaseChannelOption(ReleaseChannel Value, string DisplayName, string Description);
