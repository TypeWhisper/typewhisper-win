using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

public partial class PluginsViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public PluginsViewModel(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        RefreshPlugins();
    }

    private void RefreshPlugins()
    {
        Plugins.Clear();
        foreach (var plugin in _pluginManager.AllPlugins)
        {
            var isEnabled = _pluginManager.IsEnabled(plugin.Manifest.Id);
            Plugins.Add(new PluginItemViewModel(plugin, isEnabled, _pluginManager));
        }
    }
}

public partial class PluginItemViewModel : ObservableObject
{
    private readonly LoadedPlugin _plugin;
    private readonly PluginManager _pluginManager;

    public string Id => _plugin.Manifest.Id;
    public string Name => _plugin.Manifest.Name;
    public string Version => _plugin.Manifest.Version;
    public string? Author => _plugin.Manifest.Author;
    public string? Description => _plugin.Manifest.Description;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private UserControl? _settingsView;
    [ObservableProperty] private bool _isExpanded;

    public string ExpandButtonText => IsExpanded ? "Einstellungen ausblenden" : "Einstellungen anzeigen";

    // Capability badges
    public bool IsTranscriptionProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITranscriptionEnginePlugin;
    public bool IsLlmProvider => _plugin.Instance is TypeWhisper.PluginSDK.ILlmProviderPlugin;
    public bool IsPostProcessor => _plugin.Instance is TypeWhisper.PluginSDK.IPostProcessorPlugin;

    public PluginItemViewModel(LoadedPlugin plugin, bool isEnabled, PluginManager pluginManager)
    {
        _plugin = plugin;
        _pluginManager = pluginManager;
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

        OnPropertyChanged(nameof(ExpandButtonText));
    }
}
