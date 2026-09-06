using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed record PrototypeChoice(string Id, string Label, string Description, bool Enabled = true);

public sealed partial class PrototypeChoicePicker : UserControl
{
    private IReadOnlyList<PrototypeChoice> _options = [];
    private string _automationName = "Choice";
    private bool _keyboard;
    private HandCursorButton? _selectedButton;
    private int _comparisonVariant;
    private TextBlock? _comparisonValue;
    private TextBlock? _comparisonDescription;
    internal string SelectedId { get; private set; } = "";
    internal bool IsPopupOpen { get; private set; }
    internal event Action<string>? SelectionChanged;

    public PrototypeChoicePicker() => InitializeComponent();

    internal void Configure(string heading, string icon, string automationName)
    {
        ChoiceHeading.Text = heading.ToUpperInvariant();
        ChoiceIcon.Kind = icon;
        _automationName = automationName;
        AutomationProperties.SetName(ChoiceButton, automationName);
    }

    internal void SetOptions(IReadOnlyList<PrototypeChoice> options, string selectedId, string placeholder = "Choose a model")
    {
        _options = options;
        SelectedId = selectedId;
        ChoiceLabel.Text = options.FirstOrDefault(option => option.Id == selectedId)?.Label ?? placeholder;
        UpdateComparisonContent();
    }

    // Used only by the isolated select-box comparison. Existing consumers stay unchanged.
    internal void SetComparisonVariant(int variant)
    {
        _comparisonVariant = variant;
        if (variant == 1) return;
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _comparisonValue = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextBrush"] };
        if (variant == 3)
        {
            row.Children.Add(new TextBlock { Text = "Microphone", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
            Grid.SetColumn(_comparisonValue, 1);
            _comparisonValue.MaxWidth = 145;
            row.Children.Add(_comparisonValue);
            ChoiceButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ChoiceButton.BorderThickness = new Thickness(0, 0, 0, 1);
            ChoiceButton.CornerRadius = new CornerRadius(0);
            ChoiceButton.Padding = new Thickness(0, 10, 0, 10);
        }
        else if (variant == 4)
        {
            var labels = new StackPanel { Spacing = 5 };
            labels.Children.Add(_comparisonValue);
            _comparisonDescription = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
            labels.Children.Add(_comparisonDescription);
            row.Children.Add(labels);
            ChoiceButton.MinHeight = 70;
            ChoiceButton.Padding = new Thickness(14, 12, 14, 12);
            ChoiceButton.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
            ChoiceButton.CornerRadius = new CornerRadius(10);
        }
        else
        {
            row.Children.Add(_comparisonValue);
            ChoiceButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ChoiceButton.CornerRadius = new CornerRadius(5);
            ChoiceButton.BorderBrush = (Brush)Application.Current.Resources["MutedBrush"];
        }
        var chevron = new TypeWhisperGlyph { Kind = "chevron-down", Width = 12, Height = 12 };
        Grid.SetColumn(chevron, 2); row.Children.Add(chevron);
        ChoiceButton.Content = row;
        UpdateComparisonContent();
    }

    private void UpdateComparisonContent()
    {
        var selected = _options.FirstOrDefault(o => o.Id == SelectedId);
        if (_comparisonValue is not null) _comparisonValue.Text = selected?.Label ?? "Choose…";
        if (_comparisonDescription is not null) _comparisonDescription.Text = selected?.Description ?? "";
    }

    private void Choice_Opening(object sender, object e)
    {
        IsPopupOpen = true;
        _keyboard = ChoiceButton.FocusState == FocusState.Keyboard;
        _selectedButton = null;
        Choices.Children.Clear();
        var compact = _comparisonVariant is 2 or 3;
        ChoiceHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var option in _options)
        {
            var selected = option.Id == SelectedId;
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(new TextBlock { Text = option.Label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextBrush"] });
            if (!compact) labels.Children.Add(new TextBlock { Text = option.Description, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
            grid.Children.Add(labels);
            var check = new TypeWhisperGlyph { Kind = "check", Width = 16, Height = 16, Opacity = selected ? 1 : 0 };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
            var button = new HandCursorButton { Content = grid, MinHeight = compact ? 36 : 56, Padding = new Thickness(12, compact ? 6 : 9, 12, compact ? 6 : 9),
                HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["PrototypeMenuButtonStyle"] };
            button.IsEnabled = option.Enabled;
            if (selected)
            {
                button.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 19, 40, 58));
                if (option.Enabled) _selectedButton = button;
            }
            AutomationProperties.SetName(button, $"{_automationName} option {option.Id}");
            AutomationProperties.SetHelpText(button, $"{option.Label}. {option.Description}");
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
            button.Click += (_, _) =>
            {
                var changed = SelectedId != option.Id;
                SelectedId = option.Id;
                ChoiceLabel.Text = option.Label;
                UpdateComparisonContent();
                ChoiceButton.Flyout.Hide();
                if (changed) SelectionChanged?.Invoke(option.Id);
            };
            Choices.Children.Add(button);
        }
    }

    private void Choice_Opened(object sender, object e) =>
        (_selectedButton ?? Choices.Children.OfType<HandCursorButton>().FirstOrDefault(button => button.IsEnabled))?.Focus(_keyboard ? FocusState.Keyboard : FocusState.Programmatic);
    private void Choice_Closed(object sender, object e)
    {
        IsPopupOpen = false;
        ChoiceButton.Focus(_keyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }
    internal void ClosePopup() { _keyboard = true; ChoiceButton.Flyout.Hide(); }
    internal void FocusEntry() => ChoiceButton.Focus(FocusState.Programmatic);
    private void Choices_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape) { ClosePopup(); e.Handled = true; }
        else if (e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            var buttons = Choices.Children.OfType<HandCursorButton>().Where(button => button.IsEnabled).ToList();
            var index = buttons.FindIndex(button => ReferenceEquals(button, FocusManager.GetFocusedElement(XamlRoot)));
            if (buttons.Count > 0) buttons[Math.Clamp(index + (e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1), 0, buttons.Count - 1)].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }
}
