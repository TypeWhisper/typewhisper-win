using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models.Backup;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Exports plugin identities and restores them exclusively through the trusted plugin registry.
/// Plugin packages, settings, and credentials are never embedded in a backup.
/// </summary>
public sealed class BackupPluginHandler : IBackupPluginHandler
{
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _pluginRegistry;
    private readonly ISettingsService _settings;

    /// <summary>
    /// Initializes a new plugin backup bridge.
    /// </summary>
    public BackupPluginHandler(
        PluginManager pluginManager,
        PluginRegistryService pluginRegistry,
        ISettingsService settings)
    {
        _pluginManager = pluginManager;
        _pluginRegistry = pluginRegistry;
        _settings = settings;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupPlugin>> ExportAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<BackupPlugin> plugins = _pluginManager.AllPlugins
            .OrderBy(plugin => plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Select(plugin => new BackupPlugin
            {
                Id = plugin.Manifest.Id,
                Name = plugin.Manifest.Name,
                Version = plugin.Manifest.Version,
                WasEnabled = _pluginManager.IsEnabled(plugin.Manifest.Id)
            })
            .ToList();

        return Task.FromResult(plugins);
    }

    /// <inheritdoc />
    public async Task<BackupPluginImportResult> ImportAsync(
        IReadOnlyList<BackupPlugin> plugins,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var imported = 0;
        var skipped = 0;
        var conflicts = 0;
        var restartRequired = false;

        var requested = new Dictionary<string, BackupPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(plugin.Id))
            {
                skipped++;
                warnings.Add("A plugin entry with an empty ID was skipped.");
                continue;
            }

            var normalizedId = plugin.Id.Trim();
            if (!requested.TryAdd(normalizedId, plugin with { Id = normalizedId }))
            {
                skipped++;
                warnings.Add($"Duplicate plugin entry '{normalizedId}' was skipped.");
            }
        }

        var missing = new List<BackupPlugin>();
        foreach (var plugin in requested.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installed = _pluginManager.GetPlugin(plugin.Id);
            if (installed is null)
            {
                missing.Add(plugin);
                continue;
            }

            var isEnabled = _pluginManager.IsEnabled(plugin.Id);
            if (isEnabled == plugin.WasEnabled)
            {
                skipped++;
                continue;
            }

            try
            {
                if (plugin.WasEnabled)
                    await _pluginManager.EnablePluginAsync(plugin.Id);
                else
                    await _pluginManager.DisablePluginAsync(plugin.Id);
                imported++;
            }
            catch (Exception ex)
            {
                conflicts++;
                warnings.Add($"Plugin '{plugin.Id}' is installed, but its enabled state could not be restored: {ex.Message}");
            }
        }

        if (missing.Count > 0)
        {
            IReadOnlyDictionary<string, RegistryPlugin>? registryById = null;
            try
            {
                registryById = (await _pluginRegistry.FetchRegistryAsync(cancellationToken))
                    .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"The plugin registry could not be loaded: {ex.Message}");
            }

            foreach (var plugin in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (registryById is null || !registryById.TryGetValue(plugin.Id, out var registryPlugin))
                {
                    skipped++;
                    warnings.Add($"Plugin '{plugin.Id}' is not available in a compatible registry and was skipped.");
                    continue;
                }

                try
                {
                    var installResult = await _pluginRegistry.InstallPluginAsync(
                        registryPlugin,
                        progress: null,
                        cancellationToken);

                    restartRequired |= installResult == PluginInstallResult.PendingRestart;
                    if (installResult == PluginInstallResult.PendingRestart)
                    {
                        PersistEnabledState(plugin.Id, plugin.WasEnabled);
                    }
                    else if (plugin.WasEnabled)
                    {
                        await _pluginManager.EnablePluginAsync(plugin.Id);
                    }
                    else
                    {
                        await _pluginManager.DisablePluginAsync(plugin.Id);
                    }

                    imported++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    conflicts++;
                    warnings.Add($"Plugin '{plugin.Id}' could not be restored: {ex.Message}");
                }
            }
        }

        return new BackupPluginImportResult
        {
            CategoryResult = new BackupCategoryImportResult
            {
                Imported = imported,
                Skipped = skipped,
                Conflicts = conflicts
            },
            Warnings = warnings,
            RestartRequired = restartRequired
        };
    }

    private void PersistEnabledState(string pluginId, bool isEnabled)
    {
        var current = _settings.Current;
        var states = new Dictionary<string, bool>(
            current.PluginEnabledState,
            StringComparer.OrdinalIgnoreCase)
        {
            [pluginId] = isEnabled
        };
        _settings.Save(current with { PluginEnabledState = states });
    }
}
