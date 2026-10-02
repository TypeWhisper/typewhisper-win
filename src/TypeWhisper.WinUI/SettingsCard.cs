using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Settings rows divided by hairlines. The line follows the visible rows, so a hidden dependent row leaves none behind.
internal class SettingsRows : StackPanel
{
    internal SettingsRows() => LayoutUpdated += (_, _) =>
    {
        var first = true;
        foreach (var child in Children.Where(child => child.Visibility == Visibility.Visible))
        {
            if (child is SettingsRow row) row.Divider = !first;
            first = false;
        }
    };

    // Anything that is not a row gets a row's place between the hairlines.
    internal void Add(UIElement element) => Children.Add(element is SettingsRow ? element : SettingsRow.Host(element));
}

// One card of settings rows, as on the plugin pages: an optional heading, then the rows.
internal sealed class SettingsCard : SettingsRows
{
    internal SettingsCard(string? heading = null, string? description = null)
    {
        Padding = new Thickness(18, 2, 18, 2);
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Background = (Brush)Application.Current.Resources["SurfaceBrush"];
        BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
        if (heading is null) return;
        var copy = new StackPanel { Spacing = 3, Padding = new Thickness(0, 12, 0, 12) };
        var title = new TextBlock { Text = heading, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        copy.Children.Add(title);
        if (!string.IsNullOrEmpty(description)) copy.Children.Add(new TextBlock
        {
            Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"]
        });
        Children.Add(copy);
    }

    internal static StackPanel PageTitle(string text, string help = "")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var title = new TextBlock { Text = text, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        row.Children.Add(title);
        if (help.Length > 0) row.Children.Add(SettingsHelp.Button(text, help));
        return row;
    }
}

// Title and description on the left, the control on the right. The tag is the settings search key.
internal sealed class SettingsRow : StackPanel
{
    // Longer explanations move behind the info button.
    private const int InlineHintLength = 120;
    private const double StackedBelowWidth = 460;
    private const double PickerWidth = 260;
    private readonly Grid _header = new() { ColumnSpacing = 16 };
    private readonly StackPanel _copy = new() { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
    // Left-aligned, the star column is as wide as the title but still wraps a long one.
    private readonly Grid _titleLine = new() { ColumnSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _title = Text(14, "TextBrush");
    private readonly TextBlock _description = Text(12, "MutedBrush");
    private readonly TextBlock _status = Text(12, "MutedBrush");
    private HandCursorButton? _help;
    private double _controlWidth = double.NaN;

    internal SettingsRow(string? key = null)
    {
        Tag = key;
        Spacing = 10;
        Padding = new Thickness(0, 12, 0, 12);
        BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _titleLine.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _titleLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _title.VerticalAlignment = VerticalAlignment.Center;
        _titleLine.Children.Add(_title);
        _copy.Children.Add(_titleLine); _copy.Children.Add(_description); _copy.Children.Add(_status);
        _header.Children.Add(_copy);
        _description.Visibility = _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    internal FrameworkElement? Control { get; private set; }

    internal bool Divider
    {
        set
        {
            var thickness = new Thickness(0, value ? 1 : 0, 0, 0);
            if (BorderThickness != thickness) BorderThickness = thickness;
        }
    }

    internal string Title { set => _title.Text = value; }

    internal string Description
    {
        get => _description.Text;
        set { _description.Text = value; _description.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    }

    // For errors and states that need attention. A successful save shows nothing.
    internal string Status
    {
        get => _status.Text;
        set { _status.Text = value; _status.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    }

    internal static SettingsRow Host(UIElement element)
    {
        var row = new SettingsRow { Padding = new Thickness(0) };
        row.Children.Add(element);
        return row;
    }

    // A short hint is shown under the title, a long one behind the info button.
    internal SettingsRow Set(string title, string hint, FrameworkElement? control) => hint.Length <= InlineHintLength
        ? Set(title, hint, "", control) : Set(title, "", hint, control);

    internal SettingsRow Set(string title, string description = "", string help = "", FrameworkElement? control = null)
    {
        if (_header.Parent is null) Children.Insert(0, _header);
        _title.Text = title;
        Description = description;
        if (_help is not null) _titleLine.Children.Remove(_help);
        _help = null;
        if (help.Length > 0)
        {
            _help = SettingsHelp.Button(title, help);
            // Smaller than on a page title, so a row with help is as high as one without.
            _help.Width = _help.MinWidth = 24; _help.Height = _help.MinHeight = 20;
            _help.Padding = new Thickness(4, 2, 4, 2);
            Grid.SetColumn(_help, 1); _titleLine.Children.Add(_help);
        }
        if (Control is not null) _header.Children.Remove(Control);
        Control = control;
        if (control is null) return this;
        if (control is ChoicePicker picker) { picker.UseRowHeight(); picker.Width = PickerWidth; }
        _controlWidth = control.Width;
        control.VerticalAlignment = VerticalAlignment.Center;
        _header.Children.Add(control);
        Arrange(ActualWidth);
        return this;
    }

    // Full-width content under the title, such as dependent fields or actions.
    internal SettingsRow Below(UIElement element)
    {
        Children.Add(element);
        return this;
    }

    // Empties the row and takes its pickers out of the page's picker list.
    internal SettingsRow Reset(List<ChoicePicker> pickers)
    {
        foreach (var picker in Pickers(this).ToArray()) pickers.Remove(picker);
        if (Control is not null) _header.Children.Remove(Control);
        Control = null;
        Children.Clear();
        Status = "";
        IsHitTestVisible = true;
        return this;
    }

    internal static SettingsRow? Find(DependencyObject root, string key)
    {
        if (root is SettingsRow row && Equals(row.Tag, key)) return row;
        foreach (var child in Nested(root)) if (Find(child, key) is { } found) return found;
        return null;
    }

    // Puts a row under this one. The panel is looked up from the page: a page is configured before it is
    // loaded, when a row does not know its parent yet.
    internal void InsertAfter(DependencyObject page, SettingsRow next)
    {
        var panel = Owner(page) ?? throw new InvalidOperationException("The settings row is not on this page.");
        panel.Children.Insert(panel.Children.IndexOf(this) + 1, next);
    }

    private Panel? Owner(DependencyObject root)
    {
        if (root is Panel panel && panel.Children.Contains(this)) return panel;
        foreach (var child in Nested(root)) if (Owner(child) is { } found) return found;
        return null;
    }

    // Settings are configured before loading, when visual children may not exist.
    private static IEnumerable<DependencyObject> Nested(DependencyObject root) => root switch
    {
        Panel panel => panel.Children.Cast<DependencyObject>(),
        Border { Child: { } child } => [child],
        ContentControl { Content: DependencyObject content } => [content],
        UserControl { Content: { } content } => [content],
        _ => []
    };

    internal static SettingsRow Require(DependencyObject root, string key) =>
        Find(root, key) ?? throw new InvalidOperationException("Missing settings row: " + key);

    private static IEnumerable<ChoicePicker> Pickers(DependencyObject root)
    {
        if (root is ChoicePicker picker) { yield return picker; yield break; }
        if (root is not Panel panel) yield break;
        foreach (var child in panel.Children) foreach (var found in Pickers(child)) yield return found;
    }

    // A wide control moves under the text in a narrow window; switches and buttons stay beside it.
    private void Arrange(double width)
    {
        if (Control is not { } control) return;
        var stacked = width > 0 && width < StackedBelowWidth && control is not (ToggleSwitch or ButtonBase);
        Grid.SetRow(control, stacked ? 1 : 0);
        Grid.SetColumn(control, stacked ? 0 : 1);
        Grid.SetColumnSpan(control, stacked ? 2 : 1);
        Grid.SetColumnSpan(_copy, stacked ? 2 : 1);
        // The switch keeps room for a label on its right; without one it lines up with the other controls.
        control.Margin = new Thickness(0, stacked ? 10 : 0, control is ToggleSwitch ? -12 : 0, 0);
        control.Width = stacked ? double.NaN : _controlWidth;
        control.HorizontalAlignment = !stacked ? HorizontalAlignment.Right
            : double.IsNaN(_controlWidth) ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    private static TextBlock Text(double size, string brush) => new()
    {
        FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources[brush]
    };
}
