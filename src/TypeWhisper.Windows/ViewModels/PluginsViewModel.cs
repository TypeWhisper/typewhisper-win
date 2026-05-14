using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

public partial class PluginsViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _registryService;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<RegistryPluginItemViewModel> RegistryPlugins { get; } = [];
    public ObservableCollection<RegistryPluginCategoryGroupViewModel> MarketplaceGroups { get; } = [];
    public ObservableCollection<MarketplaceCapabilityFilterViewModel> MarketplaceCapabilityFilters { get; } = [];
    public ObservableCollection<RegistryPluginItemViewModel> FilteredMarketplacePlugins { get; } = [];
    public int InstalledPluginCount => Plugins.Count;
    public int EnabledPluginCount => Plugins.Count(static plugin => plugin.IsEnabled);
    public int MarketplacePluginCount => RegistryPlugins.Count;
    public string InstalledSummaryText => Loc.Instance.GetString("Plugins.InstalledSummaryFormat", InstalledPluginCount, EnabledPluginCount);
    public string MarketplaceSummaryText => Loc.Instance.GetString("Plugins.MarketplaceSummaryFormat", MarketplacePluginCount);
    public string SelectedMarketplaceCapabilityFilterName => string.Join(
        ", ",
        MarketplaceCapabilityFilters
            .Where(filter => filter.IsSelected)
            .OrderBy(filter => filter.SortOrder)
            .Select(filter => filter.DisplayName));
    public string MarketplaceCategorySummaryText => string.IsNullOrWhiteSpace(SelectedMarketplaceCapabilityFilterName)
        ? MarketplaceSummaryText
        : Loc.Instance.GetString("Plugins.MarketplaceCategorySummaryFormat", FilteredMarketplacePlugins.Count, SelectedMarketplaceCapabilityFilterName);
    public bool HasMarketplaceCategories => MarketplaceCapabilityFilters.Count > 0;
    public bool HasActiveMarketplaceCapabilityFilters => _selectedMarketplaceCapabilityKeys.Count > 0;

    [ObservableProperty] private bool _isLoadingRegistry;
    [ObservableProperty] private bool _isMarketplaceSelected;

    private readonly HashSet<string> _selectedMarketplaceCapabilityKeys = new(StringComparer.OrdinalIgnoreCase);

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
        // Preserve expanded state across refresh
        var expandedIds = Plugins.Where(p => p.IsExpanded).Select(p => p.Id).ToHashSet();

        Plugins.Clear();
        foreach (var plugin in _pluginManager.AllPlugins)
        {
            var isEnabled = _pluginManager.IsEnabled(plugin.Manifest.Id);
            var vm = new PluginItemViewModel(plugin, isEnabled, _pluginManager, _registryService);
            if (expandedIds.Contains(vm.Id))
                vm.IsExpanded = true;
            Plugins.Add(vm);
        }

        NotifyStateChanged();
    }

    private void RefreshMarketplaceInstallStates()
    {
        foreach (var plugin in RegistryPlugins)
            plugin.RefreshInstallState();
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

            RegistryPlugins.Clear();
            MarketplaceGroups.Clear();
            MarketplaceCapabilityFilters.Clear();

            foreach (var plugin in registryItems)
            {
                RegistryPlugins.Add(plugin);
            }

            RebuildMarketplaceFilters();
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
        OnPropertyChanged(nameof(InstalledSummaryText));
        OnPropertyChanged(nameof(MarketplaceSummaryText));
        OnPropertyChanged(nameof(SelectedMarketplaceCapabilityFilterName));
        OnPropertyChanged(nameof(MarketplaceCategorySummaryText));
        OnPropertyChanged(nameof(HasMarketplaceCategories));
        OnPropertyChanged(nameof(HasActiveMarketplaceCapabilityFilters));
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
            NotifyStateChanged();
        });
    }

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

public partial class RegistryPluginCategoryGroupViewModel : ObservableObject
{
    public string DisplayName { get; }
    public ObservableCollection<RegistryPluginItemViewModel> Plugins { get; }
    public int Count => Plugins.Count;

    public RegistryPluginCategoryGroupViewModel(string displayName, IEnumerable<RegistryPluginItemViewModel> plugins)
    {
        DisplayName = displayName;
        Plugins = [.. plugins];
    }
}

public partial class MarketplaceCapabilityFilterViewModel : ObservableObject
{
    public string Key { get; }
    public string DisplayName { get; }
    public int SortOrder { get; }
    public int Count { get; }

    [ObservableProperty] private bool _isSelected;

    public MarketplaceCapabilityFilterViewModel(string key, string displayName, int sortOrder, int count, bool isSelected)
    {
        Key = key;
        DisplayName = displayName;
        SortOrder = sortOrder;
        Count = count;
        _isSelected = isSelected;
    }
}

public partial class PluginItemViewModel : ObservableObject
{
    private readonly LoadedPlugin _plugin;
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _registryService;

    public string Id => _plugin.Manifest.Id;
    public string Name => _plugin.Manifest.Name;
    public string Version => _plugin.Manifest.Version;
    public string? Author => _plugin.Manifest.Author;
    public string? Description => _plugin.Manifest.Description;
    public string? LogoPath => PluginIconHelper.GetLogoPath(Id);
    public bool HasLogo => LogoPath is not null;
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);
    public string StatusLabel => IsEnabled ? Loc.Instance["Plugins.Enabled"] : Loc.Instance["Plugins.Disabled"];
    public IReadOnlyList<PluginMarketplaceCategoryDescriptor> CategoryDescriptors =>
        _categoryDescriptors ??= PluginMarketplaceCategories.ResolveAll(
            _plugin.Manifest.Category ?? DetectPrimaryCategory(),
            ResolveDeclaredAndDetectedCategories());

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private UserControl? _settingsView;
    [ObservableProperty] private bool _isExpanded;

    private IReadOnlyList<PluginMarketplaceCategoryDescriptor>? _categoryDescriptors;

    // Capability badges
    public bool IsTranscriptionProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITranscriptionEnginePlugin;
    public bool IsLlmProvider => _plugin.Instance is TypeWhisper.PluginSDK.ILlmProviderPlugin;
    public bool IsTtsProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITtsProviderPlugin;
    public bool IsPostProcessor => _plugin.Instance is TypeWhisper.PluginSDK.IPostProcessorPlugin;
    public bool IsActionProvider => _plugin.Instance is TypeWhisper.PluginSDK.IActionPlugin;
    public bool IsMemoryStorage => _plugin.Instance is TypeWhisper.PluginSDK.IMemoryStoragePlugin;

    public string Category => CategoryDescriptors[0].DisplayName;

    public bool IsLocal => _plugin.Manifest.IsLocal;
    public string LocationBadge => IsLocal ? Loc.Instance["Plugins.Local"] : Loc.Instance["Plugins.Cloud"];

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

    partial void OnIsExpandedChanged(bool value)
    {
        // Lazy-load settings view when first expanded
        if (value && SettingsView is null && IsEnabled)
            SettingsView = _plugin.Instance.CreateSettingsView();
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
    }
}
