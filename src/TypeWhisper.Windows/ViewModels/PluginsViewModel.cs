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

    [ObservableProperty] private bool _isLoadingRegistry;

    public PluginsViewModel(PluginManager pluginManager, PluginRegistryService registryService)
    {
        _pluginManager = pluginManager;
        _registryService = registryService;
        _pluginManager.PluginStateChanged += (_, _) =>
            Application.Current.Dispatcher.Invoke(RefreshPlugins);
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
    }

    [RelayCommand]
    private async Task RefreshRegistryAsync()
    {
        IsLoadingRegistry = true;

        try
        {
            var registry = await _registryService.FetchRegistryAsync();

            RegistryPlugins.Clear();
            foreach (var plugin in registry)
            {
                RegistryPlugins.Add(new RegistryPluginItemViewModel(plugin, _registryService));
            }
        }
        finally
        {
            IsLoadingRegistry = false;
        }
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
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private UserControl? _settingsView;
    [ObservableProperty] private bool _isExpanded;

    // Capability badges
    public bool IsTranscriptionProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITranscriptionEnginePlugin;
    public bool IsLlmProvider => _plugin.Instance is TypeWhisper.PluginSDK.ILlmProviderPlugin;
    public bool IsPostProcessor => _plugin.Instance is TypeWhisper.PluginSDK.IPostProcessorPlugin;
    public bool IsActionProvider => _plugin.Instance is TypeWhisper.PluginSDK.IActionPlugin;
    public bool IsMemoryStorage => _plugin.Instance is TypeWhisper.PluginSDK.IMemoryStoragePlugin;

    public string Category => (_plugin.Manifest.Category?.ToLowerInvariant()) switch
    {
        "transcription" => "Transcription Engines",
        "llm" => "LLM Providers",
        "memory" => "Memory",
        "postprocessing" or "post-processing" => "Post-Processing",
        "action" => "Actions",
        _ => _plugin.Instance switch
        {
            TypeWhisper.PluginSDK.ITranscriptionEnginePlugin => "Transcription Engines",
            TypeWhisper.PluginSDK.ILlmProviderPlugin => "LLM Providers",
            TypeWhisper.PluginSDK.IMemoryStoragePlugin => "Memory",
            TypeWhisper.PluginSDK.IPostProcessorPlugin => "Post-Processing",
            TypeWhisper.PluginSDK.IActionPlugin => "Actions",
            _ => "Utilities"
        }
    };

    public bool IsLocal => _plugin.Manifest.IsLocal;
    public string LocationBadge => IsLocal ? "Local" : "Cloud";

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
}
