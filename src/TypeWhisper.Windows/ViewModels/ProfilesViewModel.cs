using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IActiveWindowService _activeWindow;
    private readonly DispatcherTimer _windowTimer;

    [ObservableProperty] private Profile? _selectedProfile;

    // Edit properties for detail panel
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string? _editLanguage;
    [ObservableProperty] private string? _editTask;
    [ObservableProperty] private string? _editTranslationTarget;
    [ObservableProperty] private bool? _editWhisperModeOverride;
    [ObservableProperty] private int _editPriority;
    [ObservableProperty] private bool _editIsEnabled = true;

    // Chip inputs
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _urlPatternInput = "";

    // Live window detection
    [ObservableProperty] private string _currentProcessName = "-";
    [ObservableProperty] private string _currentWindowTitle = "-";
    [ObservableProperty] private string _matchedProfileName = "Kein Profil";
    [ObservableProperty] private bool _hasMatchedProfile;

    public IReadOnlyList<TranslationTargetOption> TranslationTargetOptions { get; } = TranslationModelInfo.ProfileTargetOptions;
    public ObservableCollection<string> ProcessNameChips { get; } = [];
    public ObservableCollection<string> UrlPatternChips { get; } = [];
    public ObservableCollection<Profile> Profiles { get; } = [];

    public ProfilesViewModel(IProfileService profiles, IActiveWindowService activeWindow)
    {
        _profiles = profiles;
        _activeWindow = activeWindow;
        _profiles.ProfilesChanged += RefreshProfiles;
        RefreshProfiles();

        _windowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _windowTimer.Tick += (_, _) => UpdateCurrentWindow();
        _windowTimer.Start();
    }

    private void UpdateCurrentWindow()
    {
        var processName = _activeWindow.GetActiveWindowProcessName();
        var title = _activeWindow.GetActiveWindowTitle();
        var url = _activeWindow.GetBrowserUrl();

        CurrentProcessName = processName ?? "-";
        CurrentWindowTitle = title ?? "-";

        var matched = _profiles.MatchProfile(processName, url);
        HasMatchedProfile = matched is not null;
        MatchedProfileName = matched?.Name ?? "Kein Profil";
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        ProcessNameChips.Clear();
        UrlPatternChips.Clear();
        ProcessNameInput = "";
        UrlPatternInput = "";

        if (value is null)
        {
            EditName = "";
            EditLanguage = null;
            EditTask = null;
            EditTranslationTarget = null;
            EditWhisperModeOverride = null;
            EditPriority = 0;
            EditIsEnabled = true;
            return;
        }

        EditName = value.Name;
        EditLanguage = value.InputLanguage;
        EditTask = value.SelectedTask;
        EditTranslationTarget = value.TranslationTarget;
        EditWhisperModeOverride = value.WhisperModeOverride;
        EditPriority = value.Priority;
        EditIsEnabled = value.IsEnabled;

        foreach (var p in value.ProcessNames) ProcessNameChips.Add(p);
        foreach (var u in value.UrlPatterns) UrlPatternChips.Add(u);
    }

    [RelayCommand]
    private void AddProcessNameChip()
    {
        var name = ProcessNameInput.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!ProcessNameChips.Contains(name, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(name);
        ProcessNameInput = "";
    }

    [RelayCommand]
    private void RemoveProcessNameChip(string chip)
    {
        ProcessNameChips.Remove(chip);
    }

    [RelayCommand]
    private void AddUrlPatternChip()
    {
        var pattern = UrlPatternInput.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        if (!UrlPatternChips.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            UrlPatternChips.Add(pattern);
        UrlPatternInput = "";
    }

    [RelayCommand]
    private void RemoveUrlPatternChip(string chip)
    {
        UrlPatternChips.Remove(chip);
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Neues Profil",
            IsEnabled = true
        };
        _profiles.AddProfile(profile);

        foreach (var p in Profiles)
        {
            if (p.Id == profile.Id)
            {
                SelectedProfile = p;
                break;
            }
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedProfile is null) return;
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var updated = SelectedProfile with
        {
            Name = EditName.Trim(),
            ProcessNames = [.. ProcessNameChips],
            UrlPatterns = [.. UrlPatternChips],
            InputLanguage = string.IsNullOrWhiteSpace(EditLanguage) ? null : EditLanguage,
            SelectedTask = string.IsNullOrWhiteSpace(EditTask) ? null : EditTask,
            TranslationTarget = string.IsNullOrWhiteSpace(EditTranslationTarget) ? null : EditTranslationTarget,
            WhisperModeOverride = EditWhisperModeOverride,
            Priority = EditPriority,
            IsEnabled = EditIsEnabled,
            UpdatedAt = DateTime.UtcNow
        };

        var selectedId = SelectedProfile.Id;
        _profiles.UpdateProfile(updated);

        foreach (var p in Profiles)
        {
            if (p.Id == selectedId)
            {
                SelectedProfile = p;
                break;
            }
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        _profiles.DeleteProfile(SelectedProfile.Id);
        SelectedProfile = null;
    }

    [RelayCommand]
    private void ToggleProfileEnabled(Profile? profile)
    {
        if (profile is null) return;
        _profiles.UpdateProfile(profile with { IsEnabled = !profile.IsEnabled });
    }

    private void RefreshProfiles()
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profiles.Profiles)
            Profiles.Add(p);

        if (selectedId is not null)
        {
            foreach (var p in Profiles)
            {
                if (p.Id == selectedId)
                {
                    SelectedProfile = p;
                    return;
                }
            }
        }
        SelectedProfile = null;
    }
}
