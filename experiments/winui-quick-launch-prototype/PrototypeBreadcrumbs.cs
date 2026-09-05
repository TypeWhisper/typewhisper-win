using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUIPrototype;

internal sealed record PrototypeCrumb(string Label, Action? Navigate = null, string? AutomationName = null);

// Compact footer navigation, using the same pointer and focus treatment as actions.
public sealed class PrototypeBreadcrumbs : UserControl
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    public PrototypeBreadcrumbs() => Content = _items;

    internal void SetItems(params PrototypeCrumb[] items)
    {
        _items.Children.Clear();
        foreach (var item in items)
        {
            if (_items.Children.Count > 0) _items.Children.Add(new TextBlock { Text = "›", FontSize = 13,
                Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
            var label = new TextBlock { Text = item.Label, FontSize = 11, MaxWidth = 112, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources[item.Navigate is null ? "TextBrush" : "MutedBrush"] };
            if (item.Navigate is null) { label.Margin = new Thickness(6, 0, 6, 0); _items.Children.Add(label); }
            else
            {
                var button = new HandCursorButton { Content = label, Padding = new Thickness(6, 5, 6, 5),
                    Style = (Style)Application.Current.Resources["PrototypeIconButtonStyle"] };
                AutomationProperties.SetName(button, item.AutomationName ?? $"Navigate to {item.Label}");
                button.Click += (_, _) => item.Navigate();
                ToolTipService.SetToolTip(button, item.Label);
                _items.Children.Add(button);
            }
        }
    }
}
