using System.Collections.ObjectModel;
using System.Windows.Threading;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Translation;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IActiveWindowService _activeWindow;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _windowTimer;
    private ProfilesContextWindow? _contextWindow;
    private readonly string _hostProcessName = Process.GetCurrentProcess().ProcessName;
    private string _lastExternalProcessName = "-";
    private string _lastExternalWindowTitle = "-";
    private string _lastExternalUrl = "-";

    [ObservableProperty] private Profile? _selectedProfile;

    // Edit properties for detail panel
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string? _editLanguage;
    [ObservableProperty] private string? _editTask;
    [ObservableProperty] private string? _editTranslationTarget;
    [ObservableProperty] private bool? _editWhisperModeOverride;
    [ObservableProperty] private string? _editTranscriptionModelOverride;
    [ObservableProperty] private int _editPriority;
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string? _editHotkey;

    // Chip inputs
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _urlPatternInput = "";

    // Live window detection
    [ObservableProperty] private string _currentProcessName = "-";
    [ObservableProperty] private string _currentWindowTitle = "-";
    [ObservableProperty] private string _currentUrl = "-";
    [ObservableProperty] private string _matchedProfileName = Loc.Instance["Profiles.NoProfile"];
    [ObservableProperty] private bool _hasMatchedProfile;

    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    public ObservableCollection<SettingOption> LanguageOptions { get; } = [];
    public ObservableCollection<SettingOption> TaskOptions { get; } = [];

    private static IReadOnlyList<TranslationTargetOption> LocalizeTranslationOptions(IReadOnlyList<TranslationTargetOption> options) =>
        options.Select(o => o.DisplayName switch
        {
            "Keine Übersetzung" => o with { DisplayName = Loc.Instance["Translation.None"] },
            "Globale Einstellung" => o with { DisplayName = Loc.Instance["Translation.GlobalSetting"] },
            _ => o
        }).ToList();
    public ObservableCollection<string> ProcessNameChips { get; } = [];
    public ObservableCollection<string> UrlPatternChips { get; } = [];
    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<string> RunningApps { get; } = [];
    public ObservableCollection<ModelOption> AvailableModelOptions { get; } = [];
    public int ProfileCount => Profiles.Count;
    public int EnabledProfileCount => Profiles.Count(static profile => profile.IsEnabled);
    public bool HasSelectedProfile => SelectedProfile is not null;
    public string ProfileSummary => Loc.Instance.GetString("Profiles.SummaryFormat", ProfileCount, EnabledProfileCount);
    public string SelectedProfileSummary => SelectedProfile is null
        ? Loc.Instance["Profiles.SelectProfileHint"]
        : Loc.Instance.GetString("Profiles.SelectedSummaryFormat", ProcessNameChips.Count, UrlPatternChips.Count);
    public string SelectedProfileDisplayName => SelectedProfile?.Name ?? Loc.Instance["Profiles.NoProfile"];
    public string MatchStatusText => HasMatchedProfile
        ? Loc.Instance.GetString("Profiles.MatchFoundFormat", MatchedProfileName)
        : Loc.Instance["Profiles.MatchNone"];
    public bool HasCurrentProcess => !string.IsNullOrWhiteSpace(CurrentProcessName) && CurrentProcessName != "-";
    public bool HasCurrentUrl => !string.IsNullOrWhiteSpace(CurrentUrl) && CurrentUrl != "-";
    public bool HasCurrentWindowTitle => !string.IsNullOrWhiteSpace(CurrentWindowTitle) && CurrentWindowTitle != "-";
    public string CurrentUrlPattern => TryExtractUrlPattern(CurrentUrl);

    private readonly ModelManagerService _modelManager;

    public ProfilesViewModel(IProfileService profiles, IActiveWindowService activeWindow, ISettingsService settings, ModelManagerService modelManager)
    {
        _profiles = profiles;
        _activeWindow = activeWindow;
        _settings = settings;
        _modelManager = modelManager;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        RefreshLocalizedCollections();
        RebuildModelOptions();
        modelManager.PluginManager.PluginStateChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(RebuildModelOptions);
        _profiles.ProfilesChanged += RefreshProfiles;
        RefreshProfiles();

        _windowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _windowTimer.Tick += (_, _) => UpdateCurrentWindow();
        _windowTimer.Start();
    }

    private void RebuildModelOptions()
    {
        var selected = EditTranscriptionModelOverride;
        AvailableModelOptions.Clear();
        AvailableModelOptions.Add(new(null, Loc.Instance["Profiles.GlobalDefault"]));
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
            foreach (var model in engine.TranscriptionModels)
                AvailableModelOptions.Add(new(ModelManagerService.GetPluginModelId(engine.PluginId, model.Id),
                    $"{engine.ProviderDisplayName}: {model.DisplayName}"));
        EditTranscriptionModelOverride = selected;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshLocalizedCollections();
            RebuildModelOptions();
            if (!HasMatchedProfile)
                MatchedProfileName = Loc.Instance["Profiles.NoProfile"];
            NotifyStateChanged();
        });
    }

    private void RefreshLocalizedCollections()
    {
        ReplaceCollection(TranslationTargetOptions, LocalizeTranslationOptions(TranslationModelInfo.ProfileTargetOptions));
        ReplaceCollection(LanguageOptions,
        [
            new SettingOption(null, Loc.Instance["Profiles.GlobalSetting"]),
            new SettingOption("auto", Loc.Instance["Profiles.Auto"]),
            new SettingOption("de", "Deutsch"),
            new SettingOption("en", "English"),
            new SettingOption("fr", "Français"),
            new SettingOption("es", "Español")
        ]);
        ReplaceCollection(TaskOptions,
        [
            new SettingOption(null, Loc.Instance["Profiles.TaskGlobal"]),
            new SettingOption("transcribe", Loc.Instance["Profiles.TaskTranscribe"]),
            new SettingOption("translate", Loc.Instance["Profiles.TaskTranslate"])
        ]);
    }

    private void UpdateCurrentWindow()
    {
        var processName = _activeWindow.GetActiveWindowProcessName();
        var title = _activeWindow.GetActiveWindowTitle();
        var url = _activeWindow.GetBrowserUrl();

        if (string.IsNullOrWhiteSpace(processName)
            || string.Equals(processName, _hostProcessName, StringComparison.OrdinalIgnoreCase))
        {
            processName = _lastExternalProcessName;
            title = _lastExternalWindowTitle;
            url = _lastExternalUrl;
        }
        else
        {
            _lastExternalProcessName = processName ?? "-";
            _lastExternalWindowTitle = title ?? "-";
            _lastExternalUrl = url ?? "-";
        }

        CurrentProcessName = processName ?? "-";
        CurrentWindowTitle = title ?? "-";
        CurrentUrl = url ?? "-";

        var matched = _profiles.MatchProfile(processName, url);
        HasMatchedProfile = matched is not null;
        MatchedProfileName = matched?.Name ?? Loc.Instance["Profiles.NoProfile"];

        RefreshRunningApps();
        NotifyStateChanged();
    }

    [RelayCommand]
    private void AddCurrentProcessRule()
    {
        if (!HasCurrentProcess) return;
        if (!ProcessNameChips.Contains(CurrentProcessName, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(CurrentProcessName);
        RefreshRunningApps();
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddCurrentUrlRule()
    {
        var pattern = CurrentUrlPattern;
        if (string.IsNullOrWhiteSpace(pattern)) return;
        if (!UrlPatternChips.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            UrlPatternChips.Add(pattern);
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void OpenLiveContextWindow()
    {
        if (_contextWindow is { IsLoaded: true })
        {
            _contextWindow.Activate();
            return;
        }

        var owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault();

        _contextWindow = new ProfilesContextWindow(this);
        if (owner is not null)
            _contextWindow.Owner = owner;

        _contextWindow.Closed += (_, _) => _contextWindow = null;
        _contextWindow.Show();
    }

    private void RefreshRunningApps()
    {
        var apps = _activeWindow.GetRunningAppProcessNames();
        var current = new HashSet<string>(RunningApps, StringComparer.OrdinalIgnoreCase);
        var incoming = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);

        if (current.SetEquals(incoming)) return;

        RunningApps.Clear();
        foreach (var app in apps)
        {
            if (!ProcessNameChips.Contains(app, StringComparer.OrdinalIgnoreCase))
                RunningApps.Add(app);
        }
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
            EditTranscriptionModelOverride = null;
            EditPriority = 0;
            EditIsEnabled = true;
            EditHotkey = null;
            NotifyStateChanged();
            return;
        }

        EditName = value.Name;
        EditLanguage = value.InputLanguage;
        EditTask = value.SelectedTask;
        EditTranslationTarget = value.TranslationTarget;
        EditWhisperModeOverride = value.WhisperModeOverride;
        EditTranscriptionModelOverride = value.TranscriptionModelOverride;
        EditPriority = value.Priority;
        EditIsEnabled = value.IsEnabled;
        EditHotkey = value.HotkeyData;

        foreach (var p in value.ProcessNames) ProcessNameChips.Add(p);
        foreach (var u in value.UrlPatterns) UrlPatternChips.Add(u);
        NotifyStateChanged();
    }

    [RelayCommand]
    private void AddProcessNameChip()
    {
        var name = ProcessNameInput.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!ProcessNameChips.Contains(name, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(name);
        ProcessNameInput = "";
        RefreshRunningApps();
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddRunningApp(string processName)
    {
        if (!ProcessNameChips.Contains(processName, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(processName);
        RunningApps.Remove(processName);
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void RemoveProcessNameChip(string chip)
    {
        ProcessNameChips.Remove(chip);
        RefreshRunningApps();
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddUrlPatternChip()
    {
        var pattern = UrlPatternInput.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        if (!UrlPatternChips.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            UrlPatternChips.Add(pattern);
        UrlPatternInput = "";
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void RemoveUrlPatternChip(string chip)
    {
        UrlPatternChips.Remove(chip);
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = Loc.Instance["Profiles.NewProfileName"],
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
            TranscriptionModelOverride = string.IsNullOrWhiteSpace(EditTranscriptionModelOverride) ? null : EditTranscriptionModelOverride,
            Priority = EditPriority,
            IsEnabled = EditIsEnabled,
            HotkeyData = string.IsNullOrWhiteSpace(EditHotkey) ? null : EditHotkey,
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
        NotifyStateChanged();
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null)
            return;

        var duplicate = SelectedProfile with
        {
            Id = Guid.NewGuid().ToString(),
            Name = Loc.Instance.GetString("Profiles.DuplicateNameFormat", SelectedProfile.Name),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _profiles.AddProfile(duplicate);
        foreach (var profile in Profiles)
        {
            if (profile.Id == duplicate.Id)
            {
                SelectedProfile = profile;
                break;
            }
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(ProfileCount));
        OnPropertyChanged(nameof(EnabledProfileCount));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(SelectedProfileDisplayName));
        OnPropertyChanged(nameof(SelectedProfileSummary));
        OnPropertyChanged(nameof(MatchStatusText));
        OnPropertyChanged(nameof(HasCurrentProcess));
        OnPropertyChanged(nameof(HasCurrentUrl));
        OnPropertyChanged(nameof(HasCurrentWindowTitle));
        OnPropertyChanged(nameof(CurrentUrlPattern));
    }

    private static string TryExtractUrlPattern(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl == "-")
            return string.Empty;

        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return rawUrl;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}

public sealed record ModelOption(string? Id, string DisplayName);
public sealed record SettingOption(string? Value, string DisplayName);
