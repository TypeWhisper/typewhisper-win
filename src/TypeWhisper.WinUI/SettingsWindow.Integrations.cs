using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

public sealed partial class SettingsWindow
{
    internal void UpdateIntegrationNavigation(IReadOnlyList<(string Id, string Title)> items)
    {
        if (_integrationItems.SequenceEqual(items) && _pluginNavigationButtons.Count > 0) return;
        _integrationItems = items.ToArray();
        foreach (var button in _pluginNavigationButtons) _navigationButtons.Remove(button);
        _pluginNavigationButtons.Clear();
        _integrationNavigation.Children.Clear();
        AddIntegrationNavigation("Discover plugins", "Integrations", "discover");
        foreach (var (id, title) in items) AddIntegrationNavigation(title, "plugin:" + id, "plugin");
    }

    private void AddIntegrationNavigation(string title, string category, string icon)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(category.StartsWith("plugin:", StringComparison.Ordinal)
            ? new PluginBrandIcon(category["plugin:".Length..]) { Width = 18, Height = 18 }
            : new TypeWhisperGlyph { Kind = icon, Width = 18, Height = 18 });
        var label = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        var selected = _currentCategory == category;
        var button = new HandCursorButton { Content = row, Tag = category,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 34, Padding = new Thickness(10, 7, 10, 7),
            Style = (Style)Application.Current.Resources[selected ? "PrimaryButtonStyle" : "MenuButtonStyle"] };
        AutomationProperties.SetName(button, "Settings integration " + title);
        AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        ToolTipService.SetToolTip(button, title);
        button.Click += (_, _) => ShowCategory(category);
        _pluginNavigationButtons.Add(button); _navigationButtons.Add(button);
        _integrationNavigation.Children.Add(button);
    }
}
