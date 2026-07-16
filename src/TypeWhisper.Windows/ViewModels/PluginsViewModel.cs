using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides plugins view model behavior.
/// </summary>
public partial class PluginsViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _registryService;

    /// <summary>
    /// Gets the loaded plugin view models.
    /// </summary>
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    /// <summary>
    /// Gets the registry plugins.
    /// </summary>
    public ObservableCollection<RegistryPluginItemViewModel> RegistryPlugins { get; } = [];
    /// <summary>
    /// Gets the marketplace groups.
    /// </summary>
    public ObservableCollection<RegistryPluginCategoryGroupViewModel> MarketplaceGroups { get; } = [];
    /// <summary>
    /// Gets the marketplace capability filters.
    /// </summary>
    public ObservableCollection<MarketplaceCapabilityFilterViewModel> MarketplaceCapabilityFilters { get; } = [];
    /// <summary>
    /// Gets the filtered marketplace plugins.
    /// </summary>
    public ObservableCollection<RegistryPluginItemViewModel> FilteredMarketplacePlugins { get; } = [];
    /// <summary>
    /// Gets the installed plugin count.
    /// </summary>
    public int InstalledPluginCount => Plugins.Count;
    /// <summary>
    /// Performs enabled plugin count.
    /// </summary>
    public int EnabledPluginCount => Plugins.Count(static plugin => plugin.IsEnabled);
    /// <summary>
    /// Gets the marketplace plugin count.
    /// </summary>
    public int MarketplacePluginCount => RegistryPlugins.Count;
    /// <summary>
    /// Gets the marketplace plugin update count.
    /// </summary>
    public int AvailablePluginUpdateCount => RegistryPlugins.Count(plugin => plugin.InstallState == PluginInstallState.UpdateAvailable);
    /// <summary>
    /// Gets whether marketplace plugin updates are available.
    /// </summary>
    public bool HasAvailablePluginUpdates => AvailablePluginUpdateCount > 0;
    /// <summary>
    /// Gets the sidebar navigation badge text for plugin updates.
    /// </summary>
    public string? PluginUpdateNavigationBadgeText => HasAvailablePluginUpdates ? AvailablePluginUpdateCount.ToString() : null;
    /// <summary>
    /// Gets the marketplace plugin update summary text.
    /// </summary>
    public string PluginUpdateSummaryText => Loc.Instance.GetString("Plugins.PluginUpdateSummaryFormat", AvailablePluginUpdateCount);
    /// <summary>
    /// Performs installed summary text.
    /// </summary>
    public string InstalledSummaryText => Loc.Instance.GetString("Plugins.InstalledSummaryFormat", InstalledPluginCount, EnabledPluginCount);
    /// <summary>
    /// Performs marketplace summary text.
    /// </summary>
    public string MarketplaceSummaryText => Loc.Instance.GetString("Plugins.MarketplaceSummaryFormat", MarketplacePluginCount);
    /// <summary>
    /// Gets the selected marketplace capability filter name.
    /// </summary>
    public string SelectedMarketplaceCapabilityFilterName => string.Join(
        ", ",
        MarketplaceCapabilityFilters
            .Where(filter => filter.IsSelected)
            .OrderBy(filter => filter.SortOrder)
            .Select(filter => filter.DisplayName));
    /// <summary>
    /// Performs marketplace category summary text.
    /// </summary>
    public string MarketplaceCategorySummaryText => string.IsNullOrWhiteSpace(SelectedMarketplaceCapabilityFilterName)
        ? MarketplaceSummaryText
        : Loc.Instance.GetString("Plugins.MarketplaceCategorySummaryFormat", FilteredMarketplacePlugins.Count, SelectedMarketplaceCapabilityFilterName);
    /// <summary>
    /// Gets whether has marketplace categories.
    /// </summary>
    public bool HasMarketplaceCategories => MarketplaceCapabilityFilters.Count > 0;
    /// <summary>
    /// Gets whether has active marketplace capability filters.
    /// </summary>
    public bool HasActiveMarketplaceCapabilityFilters => _selectedMarketplaceCapabilityKeys.Count > 0;

    [ObservableProperty] private bool _isLoadingRegistry;
    [ObservableProperty] private bool _isMarketplaceSelected;

    private readonly HashSet<string> _selectedMarketplaceCapabilityKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the PluginsViewModel class.
    /// </summary>
    public PluginsViewModel(PluginManager pluginManager, PluginRegistryService registryService)
    {
        _pluginManager = pluginManager;
        _registryService = registryService;
        _pluginManager.PluginStateChanged += (_, _) =>
            Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshPlugins();
                RefreshMarketplaceInstallStates();
            });
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RefreshPlugins();
        _ = RefreshRegistryAsync();
    }

    private void RefreshPlugins()
    {
        Plugins.Clear();
        foreach (var plugin in _pluginManager.AllPlugins)
        {
            var isEnabled = _pluginManager.IsEnabled(plugin.Manifest.Id);
            var vm = new PluginItemViewModel(plugin, isEnabled, _pluginManager, _registryService);
            vm.RegistryPlugin = FindRegistryPlugin(vm.Id);
            Plugins.Add(vm);
        }

        SyncInstalledPluginRegistryItems();
        NotifyStateChanged();
    }

    private void RefreshMarketplaceInstallStates()
    {
        foreach (var plugin in RegistryPlugins)
            plugin.RefreshInstallState();

        SyncInstalledPluginRegistryItems();
        NotifyStateChanged();
    }

    [RelayCommand]
    private async Task RefreshRegistryAsync()
    {
        IsLoadingRegistry = true;

        try
        {
            var registry = await _registryService.FetchRegistryAsync();
            var registryItems = registry
                .Select(plugin => new RegistryPluginItemViewModel(plugin, _registryService))
                .OrderBy(plugin => plugin.CategorySortOrder)
                .ThenBy(plugin => plugin.Name)
                .ToList();

            foreach (var plugin in RegistryPlugins)
                plugin.PropertyChanged -= OnRegistryPluginPropertyChanged;

            RegistryPlugins.Clear();
            MarketplaceGroups.Clear();
            MarketplaceCapabilityFilters.Clear();

            foreach (var plugin in registryItems)
            {
                plugin.PropertyChanged += OnRegistryPluginPropertyChanged;
                RegistryPlugins.Add(plugin);
            }

            RebuildMarketplaceFilters();
            SyncInstalledPluginRegistryItems();
            NotifyStateChanged();
        }
        finally
        {
            IsLoadingRegistry = false;
        }
    }

    [RelayCommand]
    private void ToggleMarketplaceCapabilityFilter(string? categoryKey)
    {
        if (string.IsNullOrWhiteSpace(categoryKey))
            return;

        var normalizedKey = PluginMarketplaceCategories.Resolve(categoryKey).Key;
        if (_selectedMarketplaceCapabilityKeys.Contains(normalizedKey))
            _selectedMarketplaceCapabilityKeys.Remove(normalizedKey);
        else
            _selectedMarketplaceCapabilityKeys.Add(normalizedKey);

        RebuildMarketplaceFilters();
        NotifyStateChanged();
    }

    [RelayCommand]
    private void ClearMarketplaceCapabilityFilters()
    {
        if (_selectedMarketplaceCapabilityKeys.Count == 0)
            return;

        _selectedMarketplaceCapabilityKeys.Clear();
        RebuildMarketplaceFilters();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(InstalledPluginCount));
        OnPropertyChanged(nameof(EnabledPluginCount));
        OnPropertyChanged(nameof(MarketplacePluginCount));
        OnPropertyChanged(nameof(AvailablePluginUpdateCount));
        OnPropertyChanged(nameof(HasAvailablePluginUpdates));
        OnPropertyChanged(nameof(PluginUpdateNavigationBadgeText));
        OnPropertyChanged(nameof(PluginUpdateSummaryText));
        OnPropertyChanged(nameof(InstalledSummaryText));
        OnPropertyChanged(nameof(MarketplaceSummaryText));
        OnPropertyChanged(nameof(SelectedMarketplaceCapabilityFilterName));
        OnPropertyChanged(nameof(MarketplaceCategorySummaryText));
        OnPropertyChanged(nameof(HasMarketplaceCategories));
        OnPropertyChanged(nameof(HasActiveMarketplaceCapabilityFilters));
    }

    private void OnRegistryPluginPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegistryPluginItemViewModel.InstallState))
        {
            SyncInstalledPluginRegistryItems();
            NotifyStateChanged();
        }
    }

    internal bool FocusInstalledPlugin(string pluginId)
    {
        var selected = Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            return false;

        IsMarketplaceSelected = false;
        return true;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var plugin in Plugins)
                plugin.NotifyLocalizationChanged();

            foreach (var plugin in RegistryPlugins)
                plugin.NotifyLocalizationChanged();

            RebuildMarketplaceFilters();
            SyncInstalledPluginRegistryItems();
            NotifyStateChanged();
        });
    }

    private void SyncInstalledPluginRegistryItems()
    {
        foreach (var plugin in Plugins)
            plugin.RegistryPlugin = FindRegistryPlugin(plugin.Id);
    }

    private RegistryPluginItemViewModel? FindRegistryPlugin(string pluginId) =>
        RegistryPlugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    private void RebuildMarketplaceFilters()
    {
        MarketplaceGroups.Clear();
        MarketplaceCapabilityFilters.Clear();

        foreach (var group in RegistryPlugins
                     .GroupBy(plugin => plugin.CategoryKey)
                     .OrderBy(group => group.First().CategorySortOrder))
        {
            var first = group.First();
            MarketplaceGroups.Add(new RegistryPluginCategoryGroupViewModel(first.CategoryLabel, group));
        }

        var categories = RegistryPlugins
            .SelectMany(plugin => plugin.CategoryDescriptors)
            .GroupBy(category => category.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Descriptor = group.First(),
                Count = RegistryPlugins.Count(plugin => plugin.CategoryKeys.Contains(group.Key, StringComparer.OrdinalIgnoreCase))
            })
            .OrderBy(item => item.Descriptor.SortOrder)
            .ThenBy(item => item.Descriptor.DisplayName)
            .ToList();

        var availableKeys = categories.Select(item => item.Descriptor.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedMarketplaceCapabilityKeys.IntersectWith(availableKeys);

        foreach (var category in categories)
        {
            MarketplaceCapabilityFilters.Add(new MarketplaceCapabilityFilterViewModel(
                category.Descriptor.Key,
                category.Descriptor.DisplayName,
                category.Descriptor.SortOrder,
                category.Count,
                _selectedMarketplaceCapabilityKeys.Contains(category.Descriptor.Key)));
        }

        RefreshFilteredMarketplacePlugins();
    }

    private void RefreshFilteredMarketplacePlugins()
    {
        FilteredMarketplacePlugins.Clear();

        var plugins = _selectedMarketplaceCapabilityKeys.Count == 0
            ? RegistryPlugins
            : RegistryPlugins.Where(plugin =>
                plugin.CategoryKeys.Any(key => _selectedMarketplaceCapabilityKeys.Contains(key)));

        foreach (var plugin in plugins)
            FilteredMarketplacePlugins.Add(plugin);
    }
}

/// <summary>
/// Provides registry plugin category group view model behavior.
/// </summary>
public partial class RegistryPluginCategoryGroupViewModel : ObservableObject
{
    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; }
    /// <summary>
    /// Gets the loaded plugin view models.
    /// </summary>
    public ObservableCollection<RegistryPluginItemViewModel> Plugins { get; }
    /// <summary>
    /// Gets the count.
    /// </summary>
    public int Count => Plugins.Count;

    /// <summary>
    /// Initializes a new instance of the RegistryPluginCategoryGroupViewModel class.
    /// </summary>
    public RegistryPluginCategoryGroupViewModel(string displayName, IEnumerable<RegistryPluginItemViewModel> plugins)
    {
        DisplayName = displayName;
        Plugins = [.. plugins];
    }
}

/// <summary>
/// Provides marketplace capability filter view model behavior.
/// </summary>
public partial class MarketplaceCapabilityFilterViewModel : ObservableObject
{
    /// <summary>
    /// Gets the key.
    /// </summary>
    public string Key { get; }
    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; }
    /// <summary>
    /// Gets the sort order.
    /// </summary>
    public int SortOrder { get; }
    /// <summary>
    /// Gets the count.
    /// </summary>
    public int Count { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Initializes a new instance of the MarketplaceCapabilityFilterViewModel class.
    /// </summary>
    public MarketplaceCapabilityFilterViewModel(string key, string displayName, int sortOrder, int count, bool isSelected)
    {
        Key = key;
        DisplayName = displayName;
        SortOrder = sortOrder;
        Count = count;
        _isSelected = isSelected;
    }
}

/// <summary>
/// Provides plugin item view model behavior.
/// </summary>
public partial class PluginItemViewModel : ObservableObject
{
    private readonly LoadedPlugin _plugin;
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _registryService;
    private RegistryPluginItemViewModel? _registryPlugin;

    /// <summary>
    /// Gets the id.
    /// </summary>
    public string Id => _plugin.Manifest.Id;
    /// <summary>
    /// Gets the display or storage name.
    /// </summary>
    public string Name => _plugin.Manifest.Name;
    /// <summary>
    /// Gets the version.
    /// </summary>
    public string Version => _plugin.Manifest.Version;
    /// <summary>
    /// Gets the author.
    /// </summary>
    public string? Author => _plugin.Manifest.Author;
    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description => _plugin.Manifest.Description;
    /// <summary>
    /// Gets the logo path.
    /// </summary>
    public string? LogoPath => PluginIconHelper.GetLogoPath(Id);
    /// <summary>
    /// Gets whether has logo.
    /// </summary>
    public bool HasLogo => LogoPath is not null;
    /// <summary>
    /// Performs icon emoji.
    /// </summary>
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    /// <summary>
    /// Performs icon gradient start.
    /// </summary>
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    /// <summary>
    /// Performs icon gradient end.
    /// </summary>
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);
    /// <summary>
    /// Gets the status label.
    /// </summary>
    public string StatusLabel => IsEnabled ? Loc.Instance["Plugins.Enabled"] : Loc.Instance["Plugins.Disabled"];
    /// <summary>
    /// Gets the category descriptors.
    /// </summary>
    public IReadOnlyList<PluginMarketplaceCategoryDescriptor> CategoryDescriptors =>
        _categoryDescriptors ??= PluginMarketplaceCategories.ResolveAll(
            _plugin.Manifest.Category ?? DetectPrimaryCategory(),
            ResolveDeclaredAndDetectedCategories());

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private UserControl? _settingsView;

    /// <summary>
    /// Gets whether the plugin exposes settings.
    /// </summary>
    public bool HasSettings => SettingsView is not null;

    /// <summary>
    /// Gets the accessible label for the plugin settings action.
    /// </summary>
    public string SettingsAutomationName => $"{Loc.Instance["Settings.WindowTitle"]}: {Name}";

    private IReadOnlyList<PluginMarketplaceCategoryDescriptor>? _categoryDescriptors;

    // Capability badges
    /// <summary>
    /// Gets whether is transcription provider.
    /// </summary>
    public bool IsTranscriptionProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITranscriptionEnginePlugin;
    /// <summary>
    /// Gets whether is llm provider.
    /// </summary>
    public bool IsLlmProvider => _plugin.Instance is TypeWhisper.PluginSDK.ILlmProviderPlugin;
    /// <summary>
    /// Gets whether is tts provider.
    /// </summary>
    public bool IsTtsProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITtsProviderPlugin;
    /// <summary>
    /// Gets whether is post processor.
    /// </summary>
    public bool IsPostProcessor => _plugin.Instance is TypeWhisper.PluginSDK.IPostProcessorPlugin;
    /// <summary>
    /// Gets whether is action provider.
    /// </summary>
    public bool IsActionProvider => _plugin.Instance is TypeWhisper.PluginSDK.IActionPlugin;
    /// <summary>
    /// Gets whether is memory storage.
    /// </summary>
    public bool IsMemoryStorage => _plugin.Instance is TypeWhisper.PluginSDK.IMemoryStoragePlugin;

    /// <summary>
    /// Gets the category.
    /// </summary>
    public string Category => CategoryDescriptors[0].DisplayName;

    /// <summary>
    /// Gets whether is local.
    /// </summary>
    public bool IsLocal => _plugin.Manifest.IsLocal;
    /// <summary>
    /// Gets the location badge.
    /// </summary>
    public string LocationBadge => IsLocal ? Loc.Instance["Plugins.Local"] : Loc.Instance["Plugins.Cloud"];
    /// <summary>
    /// Gets the matching marketplace registry plugin.
    /// </summary>
    public RegistryPluginItemViewModel? RegistryPlugin
    {
        get => _registryPlugin;
        internal set
        {
            if (ReferenceEquals(_registryPlugin, value))
                return;

            if (_registryPlugin is not null)
                _registryPlugin.PropertyChanged -= OnRegistryPluginPropertyChanged;

            _registryPlugin = value;

            if (_registryPlugin is not null)
                _registryPlugin.PropertyChanged += OnRegistryPluginPropertyChanged;

            NotifyUpdateStateChanged();
        }
    }
    /// <summary>
    /// Gets whether an update is available for this installed plugin.
    /// </summary>
    public bool HasUpdateAvailable => RegistryPlugin?.InstallState == PluginInstallState.UpdateAvailable;
    /// <summary>
    /// Gets whether an update is staged and requires restart.
    /// </summary>
    public bool IsUpdatePendingRestart => RegistryPlugin?.InstallState == PluginInstallState.PendingRestart;
    /// <summary>
    /// Gets the available update version.
    /// </summary>
    public string? AvailableUpdateVersion => HasUpdateAvailable ? RegistryPlugin?.Version : null;

    /// <summary>
    /// Initializes a new instance of the PluginItemViewModel class.
    /// </summary>
    public PluginItemViewModel(LoadedPlugin plugin, bool isEnabled, PluginManager pluginManager, PluginRegistryService registryService)
    {
        _plugin = plugin;
        _pluginManager = pluginManager;
        _registryService = registryService;
        _isEnabled = isEnabled;

        if (isEnabled)
            _settingsView = plugin.Instance.CreateSettingsView();
    }

    async partial void OnIsEnabledChanged(bool value)
    {
        if (value)
        {
            await _pluginManager.EnablePluginAsync(Id);
            SettingsView = _plugin.Instance.CreateSettingsView();
        }
        else
        {
            await _pluginManager.DisablePluginAsync(Id);
            SettingsView = null;
        }

        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnSettingsViewChanged(UserControl? value)
    {
        OnPropertyChanged(nameof(HasSettings));
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var result = MessageBox.Show(
            Loc.Instance.GetString("Plugins.UninstallConfirm", Name),
            Loc.Instance["Plugins.UninstallTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        await _registryService.UninstallPluginAsync(Id);
        RegistryPlugin?.RefreshInstallState();
        NotifyUpdateStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUpdateRegistryPlugin))]
    private async Task UpdateRegistryPluginAsync()
    {
        if (RegistryPlugin is null)
            return;

        await RegistryPlugin.UpdateCommand.ExecuteAsync(null);
        NotifyUpdateStateChanged();
    }

    private bool CanUpdateRegistryPlugin() =>
        HasUpdateAvailable && RegistryPlugin?.UpdateCommand.CanExecute(null) == true;

    private void OnRegistryPluginPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RegistryPluginItemViewModel.InstallState)
            or nameof(RegistryPluginItemViewModel.IsWorking)
            or nameof(RegistryPluginItemViewModel.Version))
        {
            NotifyUpdateStateChanged();
        }
    }

    private void NotifyUpdateStateChanged()
    {
        OnPropertyChanged(nameof(RegistryPlugin));
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(IsUpdatePendingRestart));
        OnPropertyChanged(nameof(AvailableUpdateVersion));
        UpdateRegistryPluginCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<string> ResolveDeclaredAndDetectedCategories()
    {
        foreach (var category in _plugin.Manifest.Categories ?? [])
            yield return category;

        foreach (var category in DetectCategories())
            yield return category;
    }

    private string DetectPrimaryCategory() => DetectCategories().FirstOrDefault() ?? "utility";

    private IEnumerable<string> DetectCategories()
    {
        if (_plugin.Instance is TypeWhisper.PluginSDK.ITranscriptionEnginePlugin)
            yield return "transcription";
        if (_plugin.Instance is TypeWhisper.PluginSDK.ILlmProviderPlugin)
            yield return "llm";
        if (_plugin.Instance is TypeWhisper.PluginSDK.ITtsProviderPlugin)
            yield return "tts";
        if (_plugin.Instance is TypeWhisper.PluginSDK.IMemoryStoragePlugin)
            yield return "memory";
        if (_plugin.Instance is TypeWhisper.PluginSDK.IPostProcessorPlugin)
            yield return "post-processing";
        if (_plugin.Instance is TypeWhisper.PluginSDK.IActionPlugin)
            yield return "action";
    }

    internal void NotifyLocalizationChanged()
    {
        _categoryDescriptors = null;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CategoryDescriptors));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(LocationBadge));
        OnPropertyChanged(nameof(SettingsAutomationName));
        NotifyUpdateStateChanged();
    }
}
