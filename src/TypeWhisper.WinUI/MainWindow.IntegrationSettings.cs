using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly PluginsView PluginsView = new();
    private readonly MarketplaceView MarketplaceView = new();
    private Grid? _integrationSettingsHost;
    private TextBox? _integrationSearch;
    private bool _discoverSettings;

    private void InitializeIntegrationSettings()
    {
        // These views belong to Settings from creation; no loaded visual parent is required.
        var host = _integrationSettingsHost = new Grid { RowSpacing = 16 };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var search = _integrationSearch = new TextBox { PlaceholderText = "Search integrations…", Margin = new Thickness(8, 0, 8, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search integrations");
        search.TextChanged += (_, _) =>
        { if (_discoverSettings) MarketplaceView.Filter(search.Text); else PluginsView.Filter(search.Text); };
        PluginsView.ClearSearchRequested += (_, _) => search.Text = "";
        MarketplaceView.ClearSearchRequested += (_, _) => search.Text = "";
        MarketplaceView.DetailModeChanged += detail => search.IsEnabled = !detail;
        host.Children.Add(search);
        foreach (var view in new FrameworkElement[] { PluginsView, MarketplaceView })
        {
            Grid.SetRow(view, 1); Grid.SetRowSpan(view, 1); view.Margin = new Thickness(0);
            host.Children.Add(view);
        }
        PluginsView.Visibility = Visibility.Collapsed;
        MarketplaceView.Visibility = Visibility.Visible;
        _discoverSettings = true;
        PluginsView.SettingsNavigationChanged += () => _settingsWindow?.UpdateIntegrationNavigation(PluginsView.SettingsNavigationItems);
    }

    private void ShowIntegrationSettings(bool discover)
    {
        OpenSettings();
        var first = PluginsView.SettingsNavigationItems.FirstOrDefault();
        _settingsWindow?.ShowCategory(discover || first.Id is null ? "Integrations" : "plugin:" + first.Id);
    }

    private async void ShowIntegrationPage(string? pluginId)
    {
        _discoverSettings = pluginId is null;
        _integrationSearch!.Visibility = _discoverSettings ? Visibility.Visible : Visibility.Collapsed;
        PluginsView.Visibility = _discoverSettings ? Visibility.Collapsed : Visibility.Visible;
        MarketplaceView.Visibility = _discoverSettings ? Visibility.Visible : Visibility.Collapsed;
        _integrationSearch.IsEnabled = _discoverSettings && !MarketplaceView.IsDetail;
        if (!_discoverSettings) _integrationSearch.Text = "";
        if (_discoverSettings) MarketplaceView.Filter(_integrationSearch.Text);
        else await PluginsView.OpenProviderSettingsAsync(pluginId!);
    }

    private bool NavigateIntegrationSettingsBack()
    {
        if (_discoverSettings && MarketplaceView.IsDetail) { MarketplaceView.GoBack(); return true; }
        if (_integrationSearch?.Text.Length > 0) { _integrationSearch.Text = ""; return true; }
        if (_discoverSettings) { ShowIntegrationSettings(false); return true; }
        return false;
    }
}
