using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal sealed record Crumb(string Label, Action? Navigate = null, string? AutomationName = null);

// Shared navigation content; the main shell can mirror it in the title bar.
public sealed class Breadcrumbs : UserControl
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    internal static List<Breadcrumbs> LoadedSources { get; } = [];
    internal Crumb[] Items { get; private set; } = [];
    internal bool IsTitleDestination { get; set; }
    public Breadcrumbs()
    {
        Content = _items;
        Loaded += (_, _) => { if (!LoadedSources.Contains(this)) LoadedSources.Add(this); };
        Unloaded += (_, _) => LoadedSources.Remove(this);
    }

    internal void SetItems(params Crumb[] items)
    {
        Items = items;
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
                    Style = (Style)Application.Current.Resources["IconButtonStyle"] };
                AutomationProperties.SetName(button, item.AutomationName ?? $"Navigate to {item.Label}");
                button.Click += (_, _) => item.Navigate();
                ToolTipService.SetToolTip(button, item.Label);
                _items.Children.Add(button);
            }
        }
    }
}
