using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Throwaway comparison only. Choosing a sample never changes actual settings.
public sealed class PrototypeSelectComparison : UserControl
{
    private readonly List<PrototypeChoicePicker> _pickers = [];
    private readonly Grid _grid = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly List<Border> _cards = [];
    private readonly PrototypeChoice[] _options =
    [
        new("default", "System default", "The input device selected by Windows"),
        new("usb", "USB microphone", "Sample external microphone"),
        new("headset", "Wireless headset", "Sample headset microphone")
    ];

    public PrototypeSelectComparison()
    {
        var content = new StackPanel { Spacing = 18 };
        content.Children.Add(Text("Select boxes · 4 directions", 24));
        content.Children.Add(Text("Try each dropdown, then tell me 1–4. All variants use the same sample devices; no app-wide style has been selected.", 13, true));
        (string Title, string Description)[] variants =
        [
            ("1 · Current", "Label above, icon inside. Detailed menu entries."),
            ("2 · Clean field", "Label above, no icon. Compact menu entries."),
            ("3 · Inline row", "Label and value share one line. Compact menu entries."),
            ("4 · With context", "The selected value includes a short explanation.")
        ];
        _grid.ColumnDefinitions.Add(new ColumnDefinition()); _grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++) _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < variants.Length; i++)
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(Text(variants[i].Title, 17));
            panel.Children.Add(Text(variants[i].Description, 12, true));
            if (i != 2) panel.Children.Add(Text("Microphone", 12, true));
            var picker = new PrototypeChoicePicker();
            picker.Configure("Microphone", "microphone", $"Select comparison variant {i + 1}");
            picker.SetComparisonVariant(i + 1);
            picker.SetOptions(_options, "default");
            picker.SelectionChanged += selected =>
            {
                foreach (var other in _pickers) other.SetOptions(_options, selected);
            };
            _pickers.Add(picker); panel.Children.Add(picker);
            var card = new Border { Padding = new Thickness(16), MinHeight = 210,
                BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
                CornerRadius = new CornerRadius(10), Child = panel };
            _cards.Add(card); _grid.Children.Add(card);
        }
        content.Children.Add(_grid);
        content.Children.Add(Text("Selecting a device updates all four examples so you can compare the same value. Use Tab, Space and arrow keys, or click a field.", 12, true));
        Content = content;
        SizeChanged += (_, _) => LayoutCards();
        LayoutCards();
    }

    private static TextBlock Text(string value, double size, bool muted = false) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = muted ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };

    private void LayoutCards()
    {
        var narrow = ActualWidth > 0 && ActualWidth < 650;
        _grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        for (var i = 0; i < _cards.Count; i++)
        {
            Grid.SetRow(_cards[i], narrow ? i : i / 2);
            Grid.SetColumn(_cards[i], narrow ? 0 : i % 2);
        }
    }

    internal bool CloseOpenPicker()
    {
        var picker = _pickers.FirstOrDefault(p => p.IsPopupOpen);
        if (picker is null) return false;
        picker.ClosePopup(); return true;
    }
}
