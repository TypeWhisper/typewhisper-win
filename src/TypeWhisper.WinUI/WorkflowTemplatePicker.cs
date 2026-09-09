using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed class WorkflowTemplatePicker : UserControl
{
    private readonly Grid _grid = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly List<(HandCursorButton Button, TextBlock Check, Choice Choice)> _cards = [];
    private int _columns = 1;
    internal string SelectedId { get; private set; } = "";
    internal event Action<string>? SelectionChanged;

    public WorkflowTemplatePicker()
    {
        Content = _grid;
        SizeChanged += (_, _) => LayoutCards();
    }

    internal void SetOptions(IReadOnlyList<Choice> options, string selectedId)
    {
        SelectedId = selectedId;
        _grid.Children.Clear(); _cards.Clear();
        foreach (var choice in options)
        {
            var index = _cards.Count;
            var panel = new StackPanel { Spacing = 6 };
            var top = new Grid { ColumnSpacing = 8 };
            top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new());
            top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            top.Children.Add(new TypeWhisperGlyph { Kind = Icon(choice.Id), Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });
            var check = new TextBlock { Text = "✓", FontSize = 18, Foreground = Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(check, 2); top.Children.Add(check);
            var title = new TextBlock { Text = choice.Label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(title, 1); top.Children.Add(title); panel.Children.Add(top);
            panel.Children.Add(new TextBlock { Text = choice.Description, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap });
            var button = new HandCursorButton { Content = panel, Padding = new Thickness(12, 10, 12, 10), MinHeight = 88,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(8), IsEnabled = choice.Enabled };
            AutomationProperties.SetName(button, choice.Label + ". " + choice.Description);
            ToolTipService.SetToolTip(button, choice.Description);
            button.Click += (_, _) => { SelectedId = choice.Id; UpdateSelection(); SelectionChanged?.Invoke(choice.Id); };
            button.KeyDown += (_, e) =>
            {
                var next = e.Key switch
                {
                    global::Windows.System.VirtualKey.Left => index % _columns > 0 ? index - 1 : index,
                    global::Windows.System.VirtualKey.Right => index % _columns < _columns - 1 ? Math.Min(index + 1, _cards.Count - 1) : index,
                    global::Windows.System.VirtualKey.Up => Math.Max(0, index - _columns),
                    global::Windows.System.VirtualKey.Down => Math.Min(_cards.Count - 1, index + _columns),
                    _ => -1
                };
                if (next < 0) return;
                _cards[next].Button.Focus(FocusState.Keyboard); e.Handled = true;
            };
            _cards.Add((button, check, choice)); _grid.Children.Add(button);
        }
        UpdateSelection(); LayoutCards();
    }

    private void UpdateSelection()
    {
        foreach (var (button, check, choice) in _cards)
        {
            var selected = choice.Id == SelectedId;
            button.Style = (Style)Application.Current.Resources[selected ? "PrimaryButtonStyle" : "SecondaryButtonStyle"];
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
    }

    private void LayoutCards()
    {
        _columns = ActualWidth >= 620 ? 3 : ActualWidth >= 400 ? 2 : 1;
        _grid.ColumnDefinitions.Clear(); _grid.RowDefinitions.Clear();
        for (var i = 0; i < _columns; i++) _grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < (_cards.Count + _columns - 1) / _columns; i++) _grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var i = 0; i < _cards.Count; i++) { Grid.SetRow(_cards[i].Button, i / _columns); Grid.SetColumn(_cards[i].Button, i % _columns); }
    }

    private static string Icon(string id) => id switch
    {
        "CleanedText" => "correction", "Translation" => "globe", "EmailReply" => "mail", "MeetingNotes" => "file",
        "Checklist" => "check", "Json" => "plugin", "Summary" => "text", "Dictation" => "microphone", _ => "settings"
    };
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
