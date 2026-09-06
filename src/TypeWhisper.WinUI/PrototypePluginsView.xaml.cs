using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypePluginsView : UserControl
{
    private enum Page { List, Detail, Settings }
    private Page _page;
    private readonly List<PrototypePlugin> _plugins = [];
    private LocalDictationSession? _runtime;
    private PluginManagementController? _management;
    private int _refreshGeneration;
    private bool _changingPlugin;
    private string? _uninstallMessage;
    private string? _pendingDetailFocus;
    private string Summary => $"{FilteredPlugins.Count} installed plugins";

    internal void ConfigureRuntime(LocalDictationSession runtime)
    {
        _runtime = runtime;
        var root = runtime.Packages.Store.InventoryRoot;
        _management = new(root,
        [
            new(LocalTranscriptionPlugin.PluginId, () => runtime.Models.Enabled, () => runtime.Models.Busy || runtime.CtcVocabulary.Busy,
                () => runtime.LocalPluginError ?? runtime.CtcVocabulary.Error, runtime.SetLocalPluginEnabledAsync),
            new(CloudTranscriptionPlugin.PluginId, () => runtime.Groq.Enabled, () => runtime.Groq.Busy,
                () => runtime.Groq.Error, runtime.SetGroqEnabledAsync)
        ], () => !runtime.IsRecording && runtime.OverlayState.Phase is not (DictationPhase.Processing or DictationPhase.Configuring),
            () => Task.Run(runtime.Packages.Store.Inventory));
        runtime.CtcVocabulary.Changed += () => DispatcherQueue.TryEnqueue(() => _ = RefreshRuntimeAsync());
        runtime.Groq.Changed += () => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) _ = RefreshRuntimeAsync(); });
        runtime.Models.Changed += () => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) _ = RefreshRuntimeAsync(); });
        runtime.Changed += () => DispatcherQueue.TryEnqueue(() => { UpdateRuntimeAction(); if (IsLoaded) _ = RefreshRuntimeAsync(); });
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
            var connected = manifest?.Id == LocalCtcVocabulary.PluginId &&
                string.Equals(package.Directory, Path.Combine(root, LocalCtcVocabulary.PluginId), StringComparison.OrdinalIgnoreCase);
            var transcription = manifest?.Id == LocalTranscriptionPlugin.PluginId &&
                string.Equals(package.Directory, Path.Combine(root, LocalTranscriptionPlugin.PluginId), StringComparison.OrdinalIgnoreCase);
            var cloud = manifest?.Id == CloudTranscriptionPlugin.PluginId;
            var error = state.Error;
            var enabled = state.Enabled;
            var provider = _runtime.DictationProviders.FirstOrDefault(item => item.PluginId == manifest?.Id);
            var setupRequired = enabled && provider is { Ready: false };
            _plugins.Add(new(package.Directory, manifest?.Name ?? Path.GetFileName(package.Directory), manifest?.Description ?? "An installed plugin package could not be read.",
                "plugin", manifest?.Category ?? "Package", connected
                    ? "Uses recorded audio, token timings and dictionary terms locally to refine the final transcript."
                    : cloud ? "Sends recorded audio to Groq over HTTPS after recording when a Groq model is selected. Stores your API key encrypted for your Windows user."
                    : transcription ? "Transcribes microphone audio locally. Includes automatic dictionary boosting for Parakeet."
                    : "Access requirements are not declared by this package's manifest.", manifest?.Version ?? "Unknown", manifest?.MinHostVersion ?? "0.0.0")
            {
                Enabled = enabled, RuntimeCanToggle = state.CanToggle,
                RuntimeNeedsAttention = error is not null || setupRequired,
                RuntimeStatus = state.Busy ? "Updating…" : error is not null ? "Needs attention" : setupRequired ? "Setup required" : enabled ? "Ready" : "Disabled",
                RuntimeExplanation = error ?? (cloud ? enabled
                    ? _runtime.Groq.Ready ? "Choose a Groq model in Settings to use cloud transcription." : "Open Settings to add your Groq API key."
                    : "Enable Groq and add your API key to use cloud transcription."
                    : transcription
                    ? enabled ? "NVIDIA Parakeet powers local dictation, live preview and automatic dictionary boosting. Open Settings to manage models."
                        : "Enable for local dictation and model downloads. Your downloaded models are kept."
                    : enabled
                    ? "Loaded and connected to dictation. Vocabulary preferences are set per term in Dictionary."
                    : "Disabled. Enable to load the local CTC model and refine future dictations. Your saved vocabulary preferences are kept.")
            });
        }
        if (_opened is not null)
        {
            _opened = _plugins.FirstOrDefault(plugin => plugin.Id == _opened.Id);
            if (_opened is null) ShowPage(Page.List);
            else if (_page == Page.Detail) ShowPage(Page.Detail);
        }
        if (!IsDetail) Filter(_query);
    }

    private void UpdateRuntimeAction()
    {
        if (_runtime is null || _page != Page.Detail) return;
        PluginToggleButton.IsEnabled = _opened?.RuntimeCanToggle == true && !_changingPlugin &&
            !_runtime.CtcVocabulary.Busy && !_runtime.IsRecording;
        PluginPrimaryButton.IsEnabled = _opened?.Compatible == true && !_changingPlugin;
        UninstallButton.IsEnabled = !_changingPlugin && _runtime.CanChangeProvider && !_runtime.Models.Busy && !_runtime.CtcVocabulary.Busy;
        if (PluginToggleButton.IsEnabled && _pendingDetailFocus == _opened?.Id) FocusDetailAction();
    }
    private PrototypePlugin? _opened;
    private string _query = string.Empty;
    private string _filter = "all";
    private bool _draftConnected;
    private Action? _pendingNavigation;
    private static readonly PrototypeChoice[] Preferences =
    [new("balanced", "Balanced", "Everyday transcription"), new("fast", "Faster", "Prefer a shorter wait"), new("quality", "More accurate", "Allow more processing time")];
    private static readonly PrototypeChoice[] Formats =
    [new("balanced", "Keep formatting", "Preserve headings and lists"), new("plain", "Plain text", "Remove Markdown formatting")];
    private static readonly PrototypeChoice[] Languages =
    [new("auto", "Detect automatically", "Use the language spoken"), new("de", "German", "Prefer German transcription"), new("en", "English", "Prefer English transcription")];
    internal ObservableCollection<PrototypePlugin> FilteredPlugins { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? DetailModeChanged;
    internal event EventHandler? MarketplaceRequested;
    internal event EventHandler? ReturnToDictationRequested;
    private void ReturnToDictation_Click(object sender, RoutedEventArgs e) => ReturnToDictationRequested?.Invoke(this, EventArgs.Empty);
    internal void EndSetupNavigation() => ReturnToDictationButton.Visibility = Visibility.Collapsed;
    internal bool IsDetail => _page != Page.List;

    internal bool ContainsPlugin(string id) => _runtime?.Packages.Store.IsInstalled(id) ?? _plugins.Any(plugin => plugin.Id == id);
    internal void InstallSample(PrototypePlugin plugin)
    {
        if (_runtime is not null) return;
        if (!plugin.Compatible || ContainsPlugin(plugin.Id)) return;
        _plugins.Add(plugin);
        if (!IsDetail) Filter(_query);
    }

    internal async Task OpenProviderSettingsAsync(string pluginId)
    {
        await RefreshRuntimeAsync();
        var plugin = _plugins.FirstOrDefault(item => Path.GetFileName(item.Id) == pluginId);
        if (plugin is null) return;
        OpenEntry(plugin.Id);
        ReturnToDictationButton.Visibility = Visibility.Visible;
        if (plugin.Enabled || pluginId == CloudTranscriptionPlugin.PluginId)
            Primary_Click(this, new RoutedEventArgs());
    }

    internal async Task OpenInstalledAsync(string id)
    {
        await RefreshRuntimeAsync();
        OpenEntry(id);
    }

    internal void OpenEntry(string id)
    {
        var plugin = _plugins.FirstOrDefault(item => item.Id == id || Path.GetFileName(item.Id) == id);
        if (plugin is null) return;
        _opened = plugin;
        ShowPage(Page.Detail);
        PluginDetailPage.ChangeView(null, 0, null, true);
        FocusDetailAction();
    }

    public PrototypePluginsView()
    {
        InitializeComponent();
        LanguagePicker.Configure("Language", "dictionary", "Plugin language");
        PreferencePicker.SelectionChanged += _ => UpdateDraft();
        LanguagePicker.SelectionChanged += _ => UpdateDraft();
        UpdateFilterButtons();
        Filter(string.Empty);
    }

    internal void Filter(string query)
    {
        if (IsDetail) return;
        _query = query;
        var selectedId = (PluginList.SelectedItem as PrototypePlugin)?.Id ?? _opened?.Id;
        FilteredPlugins.Clear();
        foreach (var plugin in _plugins.Where(plugin =>
            (_filter == "all" || _filter == "enabled" && plugin.Enabled && plugin.Compatible || _filter == "attention" && plugin.NeedsAttention)
            && (plugin.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || plugin.Category.Contains(query, StringComparison.OrdinalIgnoreCase))))
            FilteredPlugins.Add(plugin);
        PluginList.SelectedItem = FilteredPlugins.FirstOrDefault(item => item.Id == selectedId) ?? FilteredPlugins.FirstOrDefault();
        PluginEmptyState.Visibility = FilteredPlugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginSummary.Text = Summary;
        UpdateBreadcrumbs();
    }

    internal void MoveSelection(int delta)
    {
        if (IsDetail || FilteredPlugins.Count == 0) return;
        PluginList.SelectedIndex = Math.Clamp(PluginList.SelectedIndex + delta, 0, FilteredPlugins.Count - 1);
        PluginList.ScrollIntoView(PluginList.SelectedItem);
    }

    internal void OpenSelected()
    {
        if (IsDetail || PluginList.SelectedItem is not PrototypePlugin plugin) return;
        _opened = plugin;
        ShowPage(Page.Detail);
        PluginDetailPage.ChangeView(null, 0, null, true);
        FocusDetailAction();
    }

    private void FocusDetailAction()
    {
        if (_runtime is null) { PluginPrimaryButton.Focus(FocusState.Programmatic); return; }
        var pluginId = _pendingDetailFocus = _opened?.Id;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_page != Page.Detail || _opened?.Id != pluginId) return;
            if (PluginToggleButton.Focus(FocusState.Programmatic))
            {
                _pendingDetailFocus = null;
                PluginDetailPage.IsTabStop = false;
            }
            else
            {
                PluginDetailPage.IsTabStop = true;
                PluginDetailPage.Focus(FocusState.Programmatic);
            }
        });
    }

    private void Plugin_Click(object sender, ItemClickEventArgs e)
    {
        PluginList.SelectedItem = e.ClickedItem;
        OpenSelected();
    }

    private void ShowPage(Page page)
    {
        _page = page;
        PluginListPage.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        PluginDetailPage.Visibility = page == Page.Detail ? Visibility.Visible : Visibility.Collapsed;
        PluginSettingsPage.Visibility = page == Page.Settings && _runtime is null ? Visibility.Visible : Visibility.Collapsed;
        RuntimePluginSettingsPage.Visibility = page == Page.Settings && _runtime is not null ? Visibility.Visible : Visibility.Collapsed;
        if (page != Page.Settings) RuntimePluginSettingsPage.Content = null;
        PluginPageTitle.Text = page == Page.List ? "Integrations" : page == Page.Settings ? _runtime is null ? "Plugin settings" : _opened?.Title : _opened?.Title;
        PluginSummary.Text = page == Page.List ? Summary : "Installed plugin";
        PluginNavigationHint.Text = page == Page.Settings ? "Esc Cancel   Ctrl S Save" : page == Page.Detail ? "⌫ / Esc Back" : "⌫ / Esc Back   ↑↓ Navigate   Enter Open";
        PluginToggleButton.Visibility = page == Page.Detail ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = page == Page.Detail && _runtime is not null ? Visibility.Visible : Visibility.Collapsed;
        IntegrationTabs.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        PluginPrimaryButton.Visibility = page == Page.List ? Visibility.Collapsed : Visibility.Visible;
        PluginPrimaryButton.Content = page == Page.Settings ? "Save changes" : "Settings";
        PluginPrimaryButton.IsEnabled = page == Page.Detail && _opened?.Compatible == true;
        if (_runtime is not null && page == Page.Settings)
        {
            PluginPrimaryButton.Visibility = Visibility.Collapsed;
            PluginSummary.Text = _opened is not null && Path.GetFileName(_opened.Id) == CloudTranscriptionPlugin.PluginId ? "Plugin settings" : "Saved automatically";
            PluginNavigationHint.Text = "Esc Back";
        }
        if (page == Page.Detail && _opened is { } plugin)
        {
            PluginDescription.Text = plugin.Description;
            PluginStatus.Text = plugin.Status;
            PluginStatus.Foreground = plugin.NeedsAttention ? new SolidColorBrush(Colors.Orange) : (Brush)Application.Current.Resources["AccentBrush"];
            PluginStatusExplanation.Text = !plugin.Compatible ? $"Requires TypeWhisper {plugin.MinimumHostVersion} or later. This prototype represents host 1.1; this example cannot be enabled."
                : !plugin.Enabled ? "Disabled in this prototype. Your example settings are kept."
                : plugin.RequiresConnection && !plugin.Connected ? "Open Settings to try the connection setup with sample data."
                : "Enabled in this demo. No plugin is actually running.";
            PluginPermissions.Text = plugin.Permissions;
            PluginVersion.Text = $"Plugin {plugin.Version} · Minimum host {plugin.MinimumHostVersion} · Fictional compatibility data";
            PluginToggleButton.Content = plugin.Enabled ? "Disable" : "Enable";
            AutomationProperties.SetName(PluginToggleButton, $"{(plugin.Enabled ? "Disable" : "Enable")} {plugin.Title}");
            PluginToggleButton.IsEnabled = plugin.Compatible;
            if (_runtime is not null)
            {
                PluginStatusExplanation.Text = plugin.RuntimeExplanation;
                PluginVersion.Text = $"Plugin {plugin.Version} · Minimum host {plugin.MinimumHostVersion} · Portable host {LocalCtcVocabulary.HostVersion}";
                PluginPrimaryButton.Visibility = Path.GetFileName(plugin.Id) is LocalTranscriptionPlugin.PluginId or CloudTranscriptionPlugin.PluginId ? Visibility.Visible : Visibility.Collapsed;
                AutomationProperties.SetName(PluginPrimaryButton, "Settings for " + plugin.Title);
                PluginRuntimeNote.Text = _runtime.Packages.Store.PendingRestart(Path.GetFileName(plugin.Id))
                    ? "An update is ready. Restart TypeWhisper to use it."
                    : "Uninstalling keeps your API key, preferences and downloaded models for a later reinstall.";
                UpdateRuntimeAction();
            }
        }
        if (page == Page.Detail && _uninstallMessage is not null)
        {
            PluginStatus.Text = "Uninstalling…";
            PluginStatusExplanation.Text = _uninstallMessage;
        }
        DetailModeChanged?.Invoke(IsDetail);
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        var crumbs = new List<PrototypeCrumb> { new("Quick Launch", () => Navigate(() =>
        {
            ShowList(true);
            LauncherRequested?.Invoke(this, EventArgs.Empty);
        }), "Plugin breadcrumb Quick Launch") };
        if (_page == Page.List) crumbs.Add(new("Integrations"));
        else
        {
            crumbs.Add(new("Integrations", () => Navigate(() => ShowList(true)), "Plugin breadcrumb Plugins"));
            if (_page == Page.Settings)
            {
                crumbs.Add(new(_opened?.Title ?? "Plugin", () => Navigate(() => ShowPage(Page.Detail)), "Plugin breadcrumb detail"));
                crumbs.Add(new("Settings"));
            }
            else crumbs.Add(new(_opened?.Title ?? "Plugin"));
        }
        PluginBreadcrumbs.SetItems(crumbs.ToArray());
    }

    private void ShowList(bool reset)
    {
        ShowPage(Page.List);
        if (reset)
        {
            _filter = "all";
            UpdateFilterButtons();
            _query = string.Empty;
            ClearSearchRequested?.Invoke(this, EventArgs.Empty);
        }
        Filter(_query);
        PluginList.Focus(FocusState.Programmatic);
    }

    internal void GoBack()
    {
        if (PreferencePicker.IsPopupOpen) { PreferencePicker.ClosePopup(); return; }
        if (LanguagePicker.IsPopupOpen) { LanguagePicker.ClosePopup(); return; }
        if (PluginDiscardPrompt.Visibility == Visibility.Visible) { DismissDiscard(); return; }
        if (_page == Page.Settings) Navigate(() => { ShowPage(Page.Detail); FocusDetailAction(); });
        else if (_page == Page.Detail) ShowList(false);
        else ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsDirty => _runtime is null && _opened is { } plugin && (plugin.Preference != PreferencePicker.SelectedId
        || plugin.Language != LanguagePicker.SelectedId || plugin.Connected != _draftConnected);

    private void Navigate(Action destination)
    {
        if (PluginDiscardPrompt.Visibility == Visibility.Visible) return;
        if (_page == Page.Settings && IsDirty)
        {
            _pendingNavigation = destination;
            PluginDiscardPrompt.Visibility = Visibility.Visible;
            PluginSettingsPage.IsEnabled = false;
            PluginSettingsPage.Opacity = 0.35;
            PluginPrimaryButton.IsEnabled = false;
            KeepPluginEditing.Focus(FocusState.Programmatic);
        }
        else destination();
    }

    private void DismissDiscard()
    {
        _pendingNavigation = null;
        PluginDiscardPrompt.Visibility = Visibility.Collapsed;
        PluginSettingsPage.IsEnabled = true;
        PluginSettingsPage.Opacity = 1;
        UpdateDraft();
        PreferencePicker.FocusEntry();
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => DismissDiscard();
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        var destination = _pendingNavigation;
        DismissDiscard();
        destination?.Invoke();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_opened?.Compatible != true || PluginDiscardPrompt.Visibility == Visibility.Visible) return;
        if (_runtime is not null)
        {
            RuntimePluginSettingsPage.Content = Path.GetFileName(_opened.Id) switch
            {
                LocalTranscriptionPlugin.PluginId => new LiveModelsView(_runtime),
                CloudTranscriptionPlugin.PluginId => new LiveCloudSettingsView(_runtime),
                _ => null
            };
            if (RuntimePluginSettingsPage.Content is null) return;
            ShowPage(Page.Settings);
            RuntimePluginSettingsPage.ChangeView(null, 0, null, true);
            RuntimePluginSettingsPage.Focus(FocusState.Programmatic);
            return;
        }
        if (_page == Page.Settings) { Save(); return; }
        _draftConnected = _opened.Connected;
        var export = _opened.Category.StartsWith("Export", StringComparison.Ordinal);
        PreferenceHeading.Text = export ? "EXPORT FORMAT" : "PROCESSING PREFERENCE";
        PreferencePicker.Configure(export ? "Export format" : "Processing preference", export ? "file" : "settings", "Plugin preference");
        PreferencePicker.SetOptions(export ? Formats : Preferences, _opened.Preference);
        LanguagePicker.SetOptions(Languages, _opened.Language);
        LanguageSection.Visibility = export ? Visibility.Collapsed : Visibility.Visible;
        SettingsPluginName.Text = _opened.Title;
        ConnectionSection.Visibility = _opened.RequiresConnection ? Visibility.Visible : Visibility.Collapsed;
        ShowPage(Page.Settings);
        UpdateDraft();
        PluginSettingsPage.ChangeView(null, 0, null, true);
        if (_opened.RequiresConnection) ConnectionButton.Focus(FocusState.Programmatic);
        else PreferencePicker.FocusEntry();
    }

    private void UpdateDraft()
    {
        if (_page != Page.Settings) return;
        ConnectionButton.Content = _draftConnected ? "Demo connected · Disconnect" : "Simulate connection";
        PluginPrimaryButton.IsEnabled = IsDirty && PluginDiscardPrompt.Visibility != Visibility.Visible;
    }

    private void Save()
    {
        if (_page != Page.Settings || _opened is null || !IsDirty || PluginDiscardPrompt.Visibility == Visibility.Visible) return;
        Replace(_opened with { Preference = PreferencePicker.SelectedId, Language = LanguagePicker.SelectedId, Connected = _draftConnected });
        ShowPage(Page.Detail);
        PluginSummary.Text = "Saved · this session only";
        PluginPrimaryButton.Focus(FocusState.Programmatic);
    }

    private void Replace(PrototypePlugin updated)
    {
        var index = _plugins.FindIndex(plugin => plugin.Id == updated.Id);
        _plugins[index] = updated;
        _opened = updated;
    }

    private void Connection_Click(object sender, RoutedEventArgs e) { _draftConnected = !_draftConnected; UpdateDraft(); }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is not null)
        {
            if (_page != Page.Detail || _opened?.RuntimeCanToggle != true || _changingPlugin || _runtime.CtcVocabulary.Busy || _runtime.IsRecording) return;
            var openedId = _opened.Id;
            _changingPlugin = true; UpdateRuntimeAction();
            try
            {
                var error = await _management!.SetEnabledAsync(_opened.Id, !_opened.Enabled);
                await RefreshRuntimeAsync();
                if (error is not null && _page == Page.Detail) PluginStatusExplanation.Text = error;
            }
            finally
            {
                _changingPlugin = false;
                UpdateRuntimeAction();
                if (_page == Page.Detail && _opened?.Id == openedId) FocusDetailAction();
            }
            return;
        }
        if (_page != Page.Detail || _opened?.Compatible != true) return;
        Replace(_opened with { Enabled = !_opened.Enabled });
        ShowPage(Page.Detail);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _opened is not { } plugin || _changingPlugin) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Uninstall " + plugin.Title + "?",
            Content = "The plugin will be disabled and removed from Installed. Your API key, preferences and downloaded models will be kept. Remaining plugin files are removed at the next launch.",
            PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _changingPlugin = true; UpdateRuntimeAction();
        try
        {
            var error = await _runtime.UninstallPluginAsync(Path.GetFileName(plugin.Id), new Progress<PluginInstallationProgress>(value =>
            {
                if (!_changingPlugin || _page != Page.Detail || _opened?.Id != plugin.Id) return;
                PluginStatus.Text = "Uninstalling…";
                _uninstallMessage = value.Message;
                PluginStatusExplanation.Text = value.Message;
            }));
            await RefreshRuntimeAsync();
            if (error is not null) PluginStatusExplanation.Text = error;
            else ShowList(false);
        }
        finally { _uninstallMessage = null; _changingPlugin = false; UpdateRuntimeAction(); }
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filter = (string)((Button)sender).Tag;
        UpdateFilterButtons();
        Filter(_query);
    }

    private void UpdateFilterButtons()
    {
        foreach (var button in new[] { AllFilter, EnabledFilter, AttentionFilter })
        {
            var selected = (string)button.Tag == _filter;
            button.Style = (Style)Application.Current.Resources[selected ? "PrototypePrimaryButtonStyle" : "PrototypeIconButtonStyle"];
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) { ShowList(true); _ = RefreshRuntimeAsync(); }
    private void Marketplace_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDetail) MarketplaceRequested?.Invoke(this, EventArgs.Empty);
    }
    private void View_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_page != Page.Settings) return;
        if (e.Key == global::Windows.System.VirtualKey.S && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) { Save(); e.Handled = true; }
    }
}
