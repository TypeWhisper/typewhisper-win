using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    [ObservableProperty] private UserControl? _currentSection;
    [ObservableProperty] private string _currentSectionName = "Dashboard";

    private readonly Dictionary<string, Func<UserControl>> _sectionFactories = [];
    private readonly Dictionary<string, UserControl> _sectionCache = [];

    public SettingsWindowViewModel(
        SettingsViewModel settings,
        ModelManagerViewModel modelManager,
        HistoryViewModel history,
        DictionaryViewModel dictionary,
        SnippetsViewModel snippets,
        ProfilesViewModel profiles,
        DashboardViewModel dashboard)
    {
        Settings = settings;
        ModelManager = modelManager;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Dashboard = dashboard;
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
    }
}
