using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypePluginsView : UserControl
{
    private enum Page { List, Detail, Settings }
    private Page _page;
    private readonly List<PrototypePlugin> _plugins = PrototypePlugin.Samples.ToList();
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
    internal bool IsDetail => _page != Page.List;

    internal bool ContainsPlugin(string id) => _plugins.Any(plugin => plugin.Id == id);
    internal void InstallSample(PrototypePlugin plugin)
    {
        if (!plugin.Compatible || ContainsPlugin(plugin.Id)) return;
        _plugins.Add(plugin);
        if (!IsDetail) Filter(_query);
    }

    internal void OpenEntry(string id)
    {
        var plugin = _plugins.FirstOrDefault(item => item.Id == id);
        if (plugin is null) return;
        _opened = plugin;
        ShowPage(Page.Detail);
        PluginDetailPage.ChangeView(null, 0, null, true);
        PluginPrimaryButton.Focus(FocusState.Programmatic);
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
        PluginSummary.Text = $"{FilteredPlugins.Count} plugins · sample data";
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
        PluginPrimaryButton.Focus(FocusState.Programmatic);
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
        PluginSettingsPage.Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        PluginPageTitle.Text = page == Page.List ? "Plugins" : page == Page.Settings ? "Plugin settings" : _opened?.Title;
        PluginSummary.Text = page == Page.List ? $"{FilteredPlugins.Count} plugins · sample data" : "Example plugin";
        PluginNavigationHint.Text = page == Page.Settings ? "Esc Cancel   Ctrl S Save" : page == Page.Detail ? "⌫ / Esc Back" : "⌫ / Esc Back   ↑↓ Navigate   Enter Open";
        PluginToggleButton.Visibility = page == Page.Detail ? Visibility.Visible : Visibility.Collapsed;
        BrowseMarketplaceButton.Visibility = page == Page.List ? Visibility.Visible : Visibility.Collapsed;
        PluginPrimaryButton.Visibility = page == Page.List ? Visibility.Collapsed : Visibility.Visible;
        PluginPrimaryButton.Content = page == Page.Settings ? "Save changes" : "Settings";
        PluginPrimaryButton.IsEnabled = page == Page.Detail && _opened?.Compatible == true;
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
            PluginToggleButton.IsEnabled = plugin.Compatible;
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
        if (_page == Page.List) crumbs.Add(new("Plugins"));
        else
        {
            crumbs.Add(new("Plugins", () => Navigate(() => ShowList(true)), "Plugin breadcrumb Plugins"));
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
        if (_page == Page.Settings) Navigate(() => ShowPage(Page.Detail));
        else if (_page == Page.Detail) ShowList(false);
        else ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsDirty => _opened is { } plugin && (plugin.Preference != PreferencePicker.SelectedId
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
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_page != Page.Detail || _opened?.Compatible != true) return;
        Replace(_opened with { Enabled = !_opened.Enabled });
        ShowPage(Page.Detail);
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

    private void Reset_Click(object sender, RoutedEventArgs e) => ShowList(true);
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
