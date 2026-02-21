using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Aggregates all sub-ViewModels for the SettingsWindow with sidebar navigation.
/// </summary>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    public SettingsViewModel Settings { get; }
    public ModelManagerViewModel ModelManager { get; }
    public HistoryViewModel History { get; }
    public DictionaryViewModel Dictionary { get; }
    public SnippetsViewModel Snippets { get; }
    public ProfilesViewModel Profiles { get; }
    public DashboardViewModel Dashboard { get; }
    public PluginsViewModel Plugins { get; }

    private readonly UpdateService _updateService;

    [ObservableProperty] private UserControl? _currentSection;
    [ObservableProperty] private string _currentSectionName = "Dashboard";
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isUpdateAvailable;

    public string CurrentAppVersion => _updateService.CurrentVersion;

    private readonly Dictionary<string, Func<UserControl>> _sectionFactories = [];
    private readonly Dictionary<string, UserControl> _sectionCache = [];

    public SettingsWindowViewModel(
        SettingsViewModel settings,
        ModelManagerViewModel modelManager,
        HistoryViewModel history,
        DictionaryViewModel dictionary,
        SnippetsViewModel snippets,
        ProfilesViewModel profiles,
        DashboardViewModel dashboard,
        PluginsViewModel plugins,
        UpdateService updateService)
    {
        Settings = settings;
        ModelManager = modelManager;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Dashboard = dashboard;
        Plugins = plugins;
        _updateService = updateService;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = "Suche nach Updates\u2026";

        await _updateService.CheckForUpdatesAsync();

        IsCheckingForUpdates = false;
        if (_updateService.IsUpdateAvailable)
        {
            IsUpdateAvailable = true;
            UpdateStatusText = $"Version {_updateService.AvailableVersion} verfügbar!";
        }
        else
        {
            UpdateStatusText = "Sie verwenden die neueste Version.";
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        UpdateStatusText = "Update wird heruntergeladen\u2026";
        await _updateService.DownloadAndApplyAsync();
        UpdateStatusText = "Update fehlgeschlagen. Bitte erneut versuchen.";
    }

    public void RegisterSection(string name, Func<UserControl> factory)
    {
        _sectionFactories[name] = factory;
    }

    public void NavigateToDefault()
    {
        Navigate("Dashboard");
    }

    [RelayCommand]
    private void Navigate(string? sectionName)
    {
        if (string.IsNullOrEmpty(sectionName)) return;
        if (!_sectionFactories.ContainsKey(sectionName)) return;

        if (!_sectionCache.TryGetValue(sectionName, out var section))
        {
            section = _sectionFactories[sectionName]();
            _sectionCache[sectionName] = section;
        }

        CurrentSection = section;
        CurrentSectionName = sectionName;

        // Refresh plugin availability when navigating to Modelle
        // (API keys may have been changed in Erweiterungen)
        if (sectionName == "Modelle")
            ModelManager.RefreshPluginAvailability();
    }
}
