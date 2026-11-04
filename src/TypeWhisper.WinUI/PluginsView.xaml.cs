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
    // The view lives as long as the app and hears every change, also while Settings is closed or shows another page.
    private readonly TypeWhisper.Presentation.DeferredPageRefresh _pageRefresh = new();
    // The latest inventory scan and the store revision it reads.
    private Task _scan = Task.CompletedTask;
    private long _scannedRevision = -1;

    internal void ConfigureRuntime(LocalDictationSession runtime)
    {
        _runtime = runtime;
        runtime.Packages.Updates.Changed += () => DispatcherQueue.TryEnqueue(RefreshWhenShown);
        Loaded += async (_, _) => await runtime.Packages.Updates.RefreshAsync();
        var root = runtime.Packages.Store.InventoryRoot;
        _management = new(root, runtime.GetPluginBinding,
            () => !runtime.IsRecording && runtime.OverlayState.Phase is not (DictationPhase.Processing or DictationPhase.Configuring or DictationPhase.LoadingModel),
            () => Task.Run(runtime.Packages.Store.Inventory));
        runtime.CtcVocabulary.Changed += () => DispatcherQueue.TryEnqueue(RefreshWhenShown);
        runtime.Models.Changed += () => DispatcherQueue.TryEnqueue(RefreshWhenShown);
        runtime.Changed += () => DispatcherQueue.TryEnqueue(RefreshWhenShown);
        Loaded += (_, _) => RefreshWhenShown();
        _ = RefreshRuntimeAsync();
    }

    // A change refreshes the page while it is shown; a hidden page catches up when it opens again. A changed
    // installation index refreshes a hidden page too, since its package list also fills the Settings sidebar.
    private void RefreshWhenShown()
    {
        if (_runtime is null) return;
        var listChanged = _runtime.Packages.Store.Revision != _scannedRevision;
        if (_pageRefresh.OnChange(ViewSubscriptions.IsShown(this), always: listChanged)) _ = RefreshRuntimeAsync(rescan: listChanged);
    }

    private async Task RefreshRuntimeAsync(bool rescan = true)
    {
        if (_runtime is null || _management is null) return;
        var generation = ++_refreshGeneration;
        // A refresh for a change reuses the last scan while the installation index is unchanged. Either way
        // it waits for the latest scan, so it never renders packages older than a scan still running.
        if (rescan)
        {
            _scannedRevision = _runtime.Packages.Store.Revision;
            _scan = _management.RefreshAsync();
        }
        await _scan;
        if (generation != _refreshGeneration) return;
        BuildPlugins();
        UpdateUpdateAction();
        RefreshSettingsPages();
    }

    // The page's plugins from the latest scan and the current runtime state.
    private void BuildPlugins()
    {
        var root = _runtime!.Packages.Store.InventoryRoot;
        _plugins.Clear();
        // Read once per refresh, not per plugin: each read checks the local engine's model files.
        IReadOnlyList<DictationProviderOption>? providers = null;
        // The CTC package is an internal dependency of NVIDIA Parakeet.
        foreach (var state in _management!.Snapshot().Where(state =>
            !string.Equals(state.Package.Directory, Path.Combine(root, LocalCtcVocabulary.PluginId), StringComparison.OrdinalIgnoreCase)))
        {
            var package = state.Package;
            var manifest = package.Manifest;
            var error = state.Error ?? (manifest is null ? null : _runtime.Packages.Store.UpdateWarning(manifest.Id));
            var enabled = state.Enabled;
            var provider = (providers ??= _runtime.DictationProviders).FirstOrDefault(item => item.PluginId == manifest?.Id);
            var setupRequired = enabled && provider is { Ready: false };
            // A package policy refuses cannot be enabled; the page names the reason instead of offering the switch.
            var locality = TypeWhisper.PluginHost.PluginLocalityMap.ToContentLocality(manifest?.ResolvedLocality ?? TypeWhisper.PluginSDK.Models.PluginLocality.Unknown);
            var refusal = manifest is null ? null
                : TypeWhisper.Core.Models.ManagedProviderPolicy.From(WinUIManagedPolicy.Current).PackageRefusal(manifest.Id, locality, manifest.ResolvedCategories);
            _plugins.Add(new(package.Directory, manifest?.Name ?? Path.GetFileName(package.Directory), manifest?.Description ?? Loc.T("An installed plugin package could not be read."),
                "plugin", "", "", manifest?.Version ?? "Unknown", manifest?.MinHostVersion ?? "0.0.0")
            {
                Enabled = enabled, RuntimeCanToggle = state.CanToggle && (enabled || refusal is null), Author = manifest?.Author, IsLocal = manifest?.ResolvedLocality == TypeWhisper.PluginSDK.Models.PluginLocality.Local,
                Refusal = refusal, Locality = locality,
                Status = RuntimeUpdateStatus(manifest?.Id) ?? (state.Busy ? Loc.T("Updating…") : refusal is not null && !enabled ? Loc.T("Not allowed") : error is not null ? Loc.T("Needs attention") : setupRequired ? Loc.T("Setup required") : enabled ? Loc.T("Ready") : Loc.T("Disabled"))
            });
        }
        _pageRefresh.OnRefreshed();
    }

    internal async Task OpenProviderSettingsAsync(string pluginId)
    {
        // A page shown again first catches up on what changed while it was hidden; the scan below follows.
        if (_pageRefresh.OnShown()) { BuildPlugins(); UpdateUpdateAction(); }
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
        // A policy change re-judges every package without waiting for a runtime event.
        WinUIManagedPolicy.Changed += _ => DispatcherQueue.TryEnqueue(RefreshWhenShown);
        PluginPageTitle.Text = Loc.T("Integrations");
        PluginSummary.Text = Loc.T("Loading installed plugins…");
        InstalledUpdateButton.Content = Loc.T("Update all");
        EntryActionMenu.Attach(this, () => EntryActionMenu.FromButtons(ContextActionsFooter));
    }
}
