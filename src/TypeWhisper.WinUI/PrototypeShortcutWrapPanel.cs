using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.Foundation;

namespace TypeWhisper.WinUI;

// Small, dependency-free flow layout for variable-width shortcut chips.
public sealed class PrototypeShortcutWrapPanel : Panel
{
    public double Spacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                width = Math.Max(width, x - Spacing);
                y += lineHeight + Spacing; x = 0; lineHeight = 0;
            }
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(Math.Max(width, Math.Max(0, x - Spacing)), y + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += lineHeight + Spacing; x = 0; lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return finalSize;
    }
}
