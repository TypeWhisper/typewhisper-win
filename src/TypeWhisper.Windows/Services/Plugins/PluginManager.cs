using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Central plugin registry and lifecycle manager. Discovers plugins, maintains
/// enabled/disabled state, and provides typed capability indices for LLM providers,
/// transcription engines, and post-processors.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly PluginLoader _loader;
    private readonly PluginEventBus _eventBus;
    private readonly IActiveWindowService _activeWindow;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;

    private readonly List<LoadedPlugin> _allPlugins = [];
    private readonly Dictionary<string, PluginHostServices> _hostServices = [];
    private readonly HashSet<string> _activatedPlugins = [];
    private readonly object _lock = new();

    private List<ILlmProviderPlugin> _llmProviders = [];
    private List<ITranscriptionEnginePlugin> _transcriptionEngines = [];
    private List<IPostProcessorPlugin> _postProcessors = [];

    public PluginManager(
        PluginLoader loader,
        PluginEventBus eventBus,
        IActiveWindowService activeWindow,
        IProfileService profiles,
        ISettingsService settings)
    {
        _loader = loader;
        _eventBus = eventBus;
        _activeWindow = activeWindow;
        _profiles = profiles;
        _settings = settings;
    }

    /// <summary>All discovered plugins (enabled and disabled).</summary>
    public IReadOnlyList<LoadedPlugin> AllPlugins
    {
        get { lock (_lock) return [.. _allPlugins]; }
    }

    /// <summary>Active LLM provider plugins.</summary>
    public IReadOnlyList<ILlmProviderPlugin> LlmProviders
    {
        get { lock (_lock) return [.. _llmProviders]; }
    }

    /// <summary>Active transcription engine plugins.</summary>
    public IReadOnlyList<ITranscriptionEnginePlugin> TranscriptionEngines
    {
        get { lock (_lock) return [.. _transcriptionEngines]; }
    }

    /// <summary>Active post-processor plugins, ordered by priority.</summary>
    public IReadOnlyList<IPostProcessorPlugin> PostProcessors
    {
        get { lock (_lock) return [.. _postProcessors]; }
    }

    /// <summary>The shared event bus for plugin communication.</summary>
    public PluginEventBus EventBus => _eventBus;

    /// <summary>
    /// Discovers plugins from built-in and user directories, restores enabled state
    /// from settings, and activates all enabled plugins.
    /// </summary>
    public async Task InitializeAsync()
    {
        var searchDirs = new string[]
        {
            Path.Combine(AppContext.BaseDirectory, "Plugins"),
            TypeWhisperEnvironment.PluginsPath
        };

        var discovered = _loader.DiscoverAndLoad(searchDirs);

        lock (_lock)
        {
            _allPlugins.Clear();
            _allPlugins.AddRange(discovered);
        }

        Debug.WriteLine($"[PluginManager] Discovered {discovered.Count} plugin(s)");

        var enabledState = _settings.Current.PluginEnabledState;

        foreach (var plugin in discovered)
        {
            var isEnabled = enabledState.TryGetValue(plugin.Manifest.Id, out var state)
                ? state
                : IsBuiltInPlugin(plugin);

            if (isEnabled)
            {
                await ActivatePluginAsync(plugin);
            }
        }

        RebuildCapabilityIndices();
        MigrateApiKeys();
    }

    /// <summary>Enables and activates a plugin by ID, persisting the state.</summary>
    public async Task EnablePluginAsync(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin is null)
        {
            Debug.WriteLine($"[PluginManager] Plugin not found: {pluginId}");
            return;
        }

        if (_activatedPlugins.Contains(pluginId))
            return;

        await ActivatePluginAsync(plugin);
        RebuildCapabilityIndices();
        PersistEnabledState(pluginId, true);
    }

    /// <summary>Disables and deactivates a plugin by ID, persisting the state.</summary>
    public async Task DisablePluginAsync(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin is null)
            return;

        if (!_activatedPlugins.Contains(pluginId))
        {
            PersistEnabledState(pluginId, false);
            return;
        }

        await DeactivatePluginAsync(plugin);
        RebuildCapabilityIndices();
        PersistEnabledState(pluginId, false);
    }

    /// <summary>Whether a plugin is currently enabled (activated).</summary>
    public bool IsEnabled(string pluginId)
    {
        lock (_lock)
        {
            return _activatedPlugins.Contains(pluginId);
        }
    }

    /// <summary>Finds a loaded plugin by ID, or null if not found.</summary>
    public LoadedPlugin? GetPlugin(string pluginId)
    {
        lock (_lock)
        {
            return _allPlugins.FirstOrDefault(p => p.Manifest.Id == pluginId);
        }
    }

    private async Task ActivatePluginAsync(LoadedPlugin plugin)
    {
        try
        {
            var hostServices = new PluginHostServices(
                plugin.Manifest.Id, _activeWindow, _eventBus, _profiles);

            await plugin.Instance.ActivateAsync(hostServices);

            lock (_lock)
            {
                _hostServices[plugin.Manifest.Id] = hostServices;
                _activatedPlugins.Add(plugin.Manifest.Id);
            }

            Debug.WriteLine($"[PluginManager] Activated plugin: {plugin.Manifest.Id}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginManager] Failed to activate plugin {plugin.Manifest.Id}: {ex.Message}");
        }
    }

    private async Task DeactivatePluginAsync(LoadedPlugin plugin)
    {
        try
        {
            await plugin.Instance.DeactivateAsync();

            lock (_lock)
            {
                _hostServices.Remove(plugin.Manifest.Id);
                _activatedPlugins.Remove(plugin.Manifest.Id);
            }

            Debug.WriteLine($"[PluginManager] Deactivated plugin: {plugin.Manifest.Id}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginManager] Failed to deactivate plugin {plugin.Manifest.Id}: {ex.Message}");
        }
    }

    private void RebuildCapabilityIndices()
    {
        lock (_lock)
        {
            var activePlugins = _allPlugins
                .Where(p => _activatedPlugins.Contains(p.Manifest.Id))
                .Select(p => p.Instance)
                .ToList();

            _llmProviders = activePlugins.OfType<ILlmProviderPlugin>().ToList();
            _transcriptionEngines = activePlugins.OfType<ITranscriptionEnginePlugin>().ToList();
            _postProcessors = activePlugins.OfType<IPostProcessorPlugin>()
                .OrderBy(p => p.Priority)
                .ToList();
        }
    }

    private static bool IsBuiltInPlugin(LoadedPlugin plugin)
    {
        var builtInDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
        return plugin.PluginDirectory.StartsWith(builtInDir, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistEnabledState(string pluginId, bool enabled)
    {
        try
        {
            var current = _settings.Current;
            var updatedState = new Dictionary<string, bool>(current.PluginEnabledState)
            {
                [pluginId] = enabled
            };

            _settings.Save(current with { PluginEnabledState = updatedState });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginManager] Failed to persist enabled state for {pluginId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrates legacy GroqApiKey/OpenAiApiKey from AppSettings to plugin secrets.
    /// Only runs if the old keys are present and the corresponding plugin is active.
    /// </summary>
    private void MigrateApiKeys()
    {
        var settings = _settings.Current;

        if (!string.IsNullOrEmpty(settings.GroqApiKey))
        {
            MigrateKeyToPlugin("com.typewhisper.groq", "api-key", settings.GroqApiKey);
        }

        if (!string.IsNullOrEmpty(settings.OpenAiApiKey))
        {
            MigrateKeyToPlugin("com.typewhisper.openai", "api-key", settings.OpenAiApiKey);
        }
    }

    private void MigrateKeyToPlugin(string pluginId, string secretKey, string encryptedValue)
    {
        lock (_lock)
        {
            if (_hostServices.TryGetValue(pluginId, out var hostServices))
            {
                try
                {
                    var decrypted = ApiKeyProtection.Decrypt(encryptedValue);
                    if (!string.IsNullOrEmpty(decrypted))
                    {
                        _ = hostServices.StoreSecretAsync(secretKey, decrypted);
                        Debug.WriteLine($"[PluginManager] Migrated API key to plugin: {pluginId}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginManager] Failed to migrate API key for {pluginId}: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        List<LoadedPlugin> plugins;
        lock (_lock)
        {
            plugins = [.. _allPlugins];
        }

        foreach (var plugin in plugins)
        {
            try
            {
                if (_activatedPlugins.Contains(plugin.Manifest.Id))
                {
                    plugin.Instance.DeactivateAsync().GetAwaiter().GetResult();
                }

                plugin.Instance.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginManager] Error disposing plugin {plugin.Manifest.Id}: {ex.Message}");
            }

            try
            {
                plugin.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginManager] Error unloading context for {plugin.Manifest.Id}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _allPlugins.Clear();
            _hostServices.Clear();
            _activatedPlugins.Clear();
            _llmProviders.Clear();
            _transcriptionEngines.Clear();
            _postProcessors.Clear();
        }
    }
}
