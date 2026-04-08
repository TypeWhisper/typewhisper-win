using System.Collections.ObjectModel;
using System.Windows.Controls;
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
    public PromptsViewModel Prompts { get; }

    private readonly UpdateService _updateService;
    private readonly IErrorLogService _errorLog;

    [ObservableProperty] private UserControl? _currentSection;
    [ObservableProperty] private string _currentSectionName = "Dashboard";
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isUpdateAvailable;

    public string CurrentAppVersion => _updateService.CurrentVersion;

    public ObservableCollection<ErrorLogEntry> ErrorLogEntries { get; } = [];
    public bool HasErrorLogEntries => ErrorLogEntries.Count > 0;

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
        PromptsViewModel prompts,
        UpdateService updateService,
        IErrorLogService errorLog)
    {
        Settings = settings;
        ModelManager = modelManager;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Dashboard = dashboard;
        Plugins = plugins;
        Prompts = prompts;
        _updateService = updateService;
        _errorLog = errorLog;

        RefreshErrorLog();
        _errorLog.EntriesChanged += RefreshErrorLog;
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
            UpdateStatusText = Loc.Instance["Update.UpToDate"];
        }
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

    [RelayCommand]
    private void OpenSetupWizard()
    {
        var window = App.Services.GetRequiredService<WelcomeWindow>();
        window.Show();
    }

    public void RegisterSection(string name, Func<UserControl> factory)
    {
        _sectionFactories[name] = factory;
    }

    public void NavigateToDefault()
    {
        NavigateSync("Dashboard");
    }

    [RelayCommand]
    private async Task Navigate(string? sectionName)
    {
        NavigateSync(sectionName);

        if (sectionName == "History")
            await History.LoadAsync();
    }

    private void NavigateSync(string? sectionName)
    {
        if (string.IsNullOrEmpty(sectionName)) return;
        if (!_sectionFactories.ContainsKey(sectionName)) return;

        if (!_sectionCache.TryGetValue(sectionName, out var section))
        {
            section = _sectionFactories[sectionName]();
            _sectionCache[sectionName] = section;
        }

        // Stop preview when leaving Audio section
        if (CurrentSectionName == "Recording" && sectionName != "Recording")
            Settings.StopMicrophonePreview();

        CurrentSection = section;
        CurrentSectionName = sectionName;

        // Start preview when entering Audio section
        if (sectionName == "Recording")
            Settings.StartMicrophonePreview();

        // Refresh plugin availability when navigating to Models
        // (API keys may have been changed in Plugins)
        if (sectionName == "Models")
            ModelManager.RefreshPluginAvailability();
    }
}
