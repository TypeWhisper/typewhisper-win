using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class PluginsView
{
    private bool _settingsLayout;
    private string? _selectedSettingsPlugin;
    internal event Action? SettingsNavigationChanged;
    internal IReadOnlyList<(string Id, string Title)> SettingsNavigationItems => _plugins
        .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
        .Select(p => (Path.GetFileName(p.Id), p.Title)).ToArray();
    private readonly Dictionary<string, SettingsRow> _settingsRows = [];

    internal void UseSettingsLayout()
    {
        _settingsLayout = true;
        PluginBreadcrumbs.Visibility = PluginList.Visibility = PluginNavigationHint.Visibility = Visibility.Collapsed;
        PluginContentScroll.Visibility = Visibility.Visible;
        IntegrationTabs.Visibility = PluginFilterTabs.Visibility = Visibility.Collapsed;
        RefreshSettingsPages();
    }

    private void RefreshSettingsPages()
    {
        if (_runtime is null) return;
        foreach (var removed in _settingsRows.Keys.Where(id => !_plugins.Any(p => p.Id == id)).ToArray())
        {
            SettingsPages.Children.Remove(_settingsRows[removed].Page);
            _settingsRows.Remove(removed);
        }
        foreach (var plugin in _plugins.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_settingsRows.TryGetValue(plugin.Id, out var row))
            {
                row = CreateSettingsRow(plugin);
                _settingsRows.Add(plugin.Id, row);
                SettingsPages.Children.Add(row.Page);
            }
            row.Plugin = plugin;
            row.Status.Text = plugin.Status;
            row.Toggle.Content = plugin.Enabled ? "Disable plugin" : "Enable plugin";
            AutomationProperties.SetName(row.Toggle, (plugin.Enabled ? "Disable " : "Enable ") + plugin.Title);
            row.Toggle.IsEnabled = plugin.RuntimeCanToggle && !_changingPlugin && !_runtime.CtcVocabulary.Busy && _runtime.CanChangeProvider;
            row.Remove.IsEnabled = !_changingPlugin && _runtime.CanChangeProvider && !_runtime.Models.Busy && !_runtime.CtcVocabulary.Busy;
            var id = Path.GetFileName(plugin.Id);
            row.Update.Visibility = _runtime.Packages.Updates.HasUpdate(id) ? Visibility.Visible : Visibility.Collapsed;
            row.Update.IsEnabled = !_changingPlugin && !_runtime.Packages.Updates.Busy && _runtime.CanChangeProvider;
            var selected = Path.GetFileName(plugin.Id) == _selectedSettingsPlugin;
            row.Page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (!selected) row.Settings.Content = null;
            if (selected)
            {
                PluginPageTitle.Text = plugin.Title;
                PluginSummary.Text = plugin.Status;
                SettingsPluginActions.Content = row.Options;
                row.Settings.Content ??= CreatePluginSettings(plugin);
            }
        }
        SettingsNavigationChanged?.Invoke();
        if (_selectedSettingsPlugin is not null && !_plugins.Any(p => Path.GetFileName(p.Id) == _selectedSettingsPlugin))
        {
            PluginPageTitle.Text = "Plugin unavailable";
            PluginSummary.Text = "Choose another integration from the sidebar.";
        }
    }

    private UIElement CreatePluginSettings(Plugin plugin)
    {
        var id = Path.GetFileName(plugin.Id);
        SetProfileLayout(false);
        if (id == LocalTranscriptionPlugin.PluginId) return new LiveModelsView(_runtime!);
        var settings = new LivePortablePluginSettings(_runtime!, id, showEnableAction: false);
        settings.ProfileLayoutChanged += profile => { if (_selectedSettingsPlugin == id) SetProfileLayout(profile); };
        return settings;
    }

    private void SetProfileLayout(bool profile)
    {
        // The plugin summary describes its default provider, not the profile being edited.
        PluginSummary.Visibility = profile ? Visibility.Collapsed : Visibility.Visible;
        PluginContentScroll.VerticalScrollMode = profile ? ScrollMode.Disabled : ScrollMode.Auto;
        PluginContentScroll.VerticalScrollBarVisibility = profile ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ContextActionsFooter.Visibility = profile ? Visibility.Collapsed : Visibility.Visible;
    }

    private SettingsRow CreateSettingsRow(Plugin plugin)
    {
        var status = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        var page = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        var body = new Grid { RowSpacing = 8 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(new TextBlock { Text = plugin.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 280 });
        var toggle = SettingsButton("Enable plugin");
        var update = SettingsButton("Update plugin");
        var remove = SettingsButton("Uninstall…");
        actions.Children.Add(toggle); actions.Children.Add(update); actions.Children.Add(remove);
        var options = SettingsButton("•••");
        AutomationProperties.SetName(options, "Manage " + plugin.Title);
        ToolTipService.SetToolTip(options, "Manage plugin");
        options.Flyout = new Flyout { Content = actions };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        body.Children.Add(message);
        var settings = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(settings, 1);
        body.Children.Add(settings); page.Child = body;
        var row = new SettingsRow(plugin, page, status, toggle, update, remove, settings, options);
        async Task Run(Func<Task<string?>> operation)
        {
            if (_changingPlugin || _runtime?.CanChangeProvider != true) return;
            _changingPlugin = true; RefreshSettingsPages();
            try
            {
                var error = await operation();
                message.Text = error ?? ""; message.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { message.Text = "The plugin operation could not finish. Please try again."; message.Visibility = Visibility.Visible; }
            finally { _changingPlugin = false; await RefreshRuntimeAsync(); }
        }
        toggle.Click += async (_, _) => await Run(() => _management!.SetEnabledAsync(row.Plugin.Id, !row.Plugin.Enabled));
        update.Click += async (_, _) => await Run(async () => { await _runtime!.Packages.Updates.UpdateAsync(Path.GetFileName(row.Plugin.Id)); return _runtime.Packages.Updates.Status; });
        remove.Click += async (_, _) =>
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Uninstall " + row.Plugin.Title + "?",
                Content = "Your API key, preferences and downloaded models will be kept.", PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Run(() => _runtime!.UninstallPluginAsync(Path.GetFileName(row.Plugin.Id)));
        };
        return row;
    }

    private void SelectSettingsPlugin(string pluginId)
    {
        _selectedSettingsPlugin = Path.GetFileName(pluginId);
        RefreshSettingsPages();
        PluginContentScroll.ChangeView(null, 0, null, true);
    }

    internal void CloseSettingsPage()
    {
        _selectedSettingsPlugin = null;
        SettingsPluginActions.Content = null;
        SetProfileLayout(false);
        foreach (var row in _settingsRows.Values) row.Settings.Content = null;
    }

    private static HandCursorButton SettingsButton(string title) => new() { Content = title,
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], HorizontalAlignment = HorizontalAlignment.Left };

    private sealed class SettingsRow(Plugin plugin, Border page, TextBlock status, HandCursorButton toggle,
        HandCursorButton update, HandCursorButton remove, ContentControl settings, HandCursorButton options)
    {
        internal Plugin Plugin = plugin;
        internal readonly Border Page = page;
        internal readonly TextBlock Status = status;
        internal readonly HandCursorButton Toggle = toggle, Update = update, Remove = remove;
        internal readonly ContentControl Settings = settings;
        internal readonly HandCursorButton Options = options;
    }
}
