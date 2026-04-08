using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.Controls;

/// <summary>
/// Capsule badge showing supporter tier (Bronze/Silver/Gold).
/// </summary>
public sealed class SupporterBadge : Border
{
    public static readonly DependencyProperty TierProperty =
        DependencyProperty.Register(nameof(Tier), typeof(SupporterTier), typeof(SupporterBadge),
            new PropertyMetadata(SupporterTier.None, OnTierChanged));

    public SupporterTier Tier
    {
        get => (SupporterTier)GetValue(TierProperty);
        set => SetValue(TierProperty, value);
    }

    public SupporterBadge()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 3, 8, 3);
        Visibility = Visibility.Collapsed;
        UpdateVisual();
    }

    private static void OnTierChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SupporterBadge badge) badge.UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (Tier == SupporterTier.None)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        var (icon, color, label) = Tier switch
        {
            SupporterTier.Bronze => ("\u2764", Color.FromRgb(0xCD, 0x7F, 0x32), "Bronze"),
            SupporterTier.Silver => ("\u2B50", Color.FromRgb(0xC0, 0xC0, 0xC0), "Silver"),
            SupporterTier.Gold => ("\uD83D\uDC51", Color.FromRgb(0xFF, 0xD7, 0x00), "Gold"),
            _ => ("", Colors.Transparent, "")
        };

        Background = new SolidColorBrush(color) { Opacity = 0.12 };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });

        Child = panel;
    }
}
