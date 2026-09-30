using Microsoft.UI.Xaml.Controls;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

public sealed partial class PluginsView : UserControl
{
    private readonly List<Plugin> _plugins = [];
    private LocalDictationSession? _runtime;
    private PluginManagementController? _management;
    private int _refreshGeneration;
    private bool _changingPlugin;

    internal void ConfigureRuntime(LocalDictationSession runtime)
    {
        _runtime = runtime;
        runtime.Packages.Updates.Changed += () => DispatcherQueue.TryEnqueue(() => _ = RefreshRuntimeAsync());
        Loaded += async (_, _) => await runtime.Packages.Updates.RefreshAsync();
        var root = runtime.Packages.Store.InventoryRoot;
        _management = new(root, runtime.GetPluginBinding,
            () => !runtime.IsRecording && runtime.OverlayState.Phase is not (DictationPhase.Processing or DictationPhase.Configuring or DictationPhase.LoadingModel),
            () => Task.Run(runtime.Packages.Store.Inventory));
        runtime.CtcVocabulary.Changed += () => DispatcherQueue.TryEnqueue(() => _ = RefreshRuntimeAsync());
        runtime.Models.Changed += () => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) _ = RefreshRuntimeAsync(); });
        runtime.Changed += () => DispatcherQueue.TryEnqueue(() => { UpdateUpdateAction(); if (IsLoaded) _ = RefreshRuntimeAsync(); });
        Loaded += (_, _) => _ = RefreshRuntimeAsync();
        _ = RefreshRuntimeAsync();
    }

    private async Task RefreshRuntimeAsync()
    {
        if (_runtime is null || _management is null) return;
        var generation = ++_refreshGeneration;
        var root = _runtime.Packages.Store.InventoryRoot;
        await _management.RefreshAsync();
        if (generation != _refreshGeneration) return;
        _plugins.Clear();
        // The CTC package is an internal dependency of NVIDIA Parakeet.
        foreach (var state in _management.Snapshot().Where(state =>
            !string.Equals(state.Package.Directory, Path.Combine(root, LocalCtcVocabulary.PluginId), StringComparison.OrdinalIgnoreCase)))
        {
            var package = state.Package;
            var manifest = package.Manifest;
            var error = state.Error ?? (manifest is null ? null : _runtime.Packages.Store.UpdateWarning(manifest.Id));
            var enabled = state.Enabled;
            var provider = _runtime.DictationProviders.FirstOrDefault(item => item.PluginId == manifest?.Id);
            var setupRequired = enabled && provider is { Ready: false };
            _plugins.Add(new(package.Directory, manifest?.Name ?? Path.GetFileName(package.Directory), manifest?.Description ?? "An installed plugin package could not be read.",
                "plugin", "", "", manifest?.Version ?? "Unknown", manifest?.MinHostVersion ?? "0.0.0")
            {
                Enabled = enabled, RuntimeCanToggle = state.CanToggle,
                Status = RuntimeUpdateStatus(manifest?.Id) ?? (state.Busy ? "Updating…" : error is not null ? "Needs attention" : setupRequired ? "Setup required" : enabled ? "Ready" : "Disabled")
            });
        }
        UpdateUpdateAction();
        RefreshSettingsPages();
    }

    internal async Task OpenProviderSettingsAsync(string pluginId)
    {
        // Record navigation before awaiting: every refresh renders the latest selection.
        // A plugin installed a moment ago is listed only after the refresh; rendering it
        // earlier would report it unavailable and drop the selection.
        var listed = _plugins.Any(plugin => Path.GetFileName(plugin.Id) == pluginId);
        if (listed) SelectSettingsPlugin(pluginId);
        else _selectedSettingsPlugin = Path.GetFileName(pluginId);
        await RefreshRuntimeAsync();
        if (!listed) PluginContentScroll.ChangeView(null, 0, null, true);
    }

    public PluginsView()
    {
        InitializeComponent();
        EntryActionMenu.Attach(this, () => EntryActionMenu.FromButtons(ContextActionsFooter));
    }
}
