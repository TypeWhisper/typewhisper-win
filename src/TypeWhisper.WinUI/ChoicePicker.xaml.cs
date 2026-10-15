using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed record Choice(string Id, string Label, string Description, bool Enabled = true)
{
    public string? PluginId { get; init; }
}

public sealed partial class ChoicePicker : UserControl
{
    private IReadOnlyList<Choice> _options = [];
    private string _automationName = Loc.T("Choice");
    private bool _keyboard;
    private HandCursorButton? _selectedButton;
    private int _comparisonVariant;
    private TextBlock? _comparisonValue;
    private TextBlock? _comparisonDescription;
    private readonly List<(Choice Option, HandCursorButton Button)> _rows = [];
    internal string SelectedId { get; private set; } = "";
    internal bool IsPopupOpen { get; private set; }
    internal event Action<string>? SelectionChanged;

    // Long lists such as spoken languages or provider models get a search field.
    private const int SearchThreshold = 10;
    private bool Searchable => _options.Count >= SearchThreshold;

    public ChoicePicker()
    {
        InitializeComponent();
        SearchPlaceholder.Text = Loc.T("Search…");
        NoMatches.Text = Loc.T("No matches.");
        AutomationProperties.SetName(ChoiceSearch, Loc.T("Search {0}", _automationName));
    }

    internal void Configure(string heading, string icon, string automationName)
    {
        ChoiceHeading.Text = heading.ToUpperInvariant();
        ChoiceIcon.Kind = icon;
        _automationName = automationName;
        AutomationProperties.SetName(ChoiceButton, automationName);
        AutomationProperties.SetName(ChoiceSearch, Loc.T("Search {0}", automationName));
    }

    // Beside a settings row's title the picker is as high as a button.
    internal void UseRowHeight() => ChoiceButton.MinHeight = 36;

    internal void SetOptions(IReadOnlyList<Choice> options, string selectedId, string? placeholder = null)
    {
        _options = options;
        SelectedId = selectedId;
        ChoiceLabel.Text = options.FirstOrDefault(option => option.Id == selectedId)?.Label ?? placeholder ?? Loc.T("Choose a model");
        UpdateSelectedIcon();
        UpdateComparisonContent();
        if (IsPopupOpen) RebuildChoices(restoreFocus: true);
    }

    private void UpdateSelectedIcon()
    {
        var pluginId = _options.FirstOrDefault(option => option.Id == SelectedId)?.PluginId;
        var branded = !string.IsNullOrEmpty(pluginId);
        ChoiceBrandIcon.PluginId = pluginId ?? string.Empty;
        ChoiceBrandIcon.Visibility = branded ? Visibility.Visible : Visibility.Collapsed;
        ChoiceIcon.Visibility = branded ? Visibility.Collapsed : Visibility.Visible;
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
            row.Children.Add(new TextBlock { Text = Loc.T("Microphone"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
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
        if (_comparisonValue is not null) _comparisonValue.Text = selected?.Label ?? Loc.T("Choose\u2026");
        if (_comparisonDescription is not null) _comparisonDescription.Text = selected?.Description ?? "";
    }

    private void Choice_Opening(object sender, object e)
    {
        IsPopupOpen = true;
        _keyboard = ChoiceButton.FocusState == FocusState.Keyboard;
        ChoiceSearch.Text = "";
        RebuildChoices();
    }

    private void RebuildChoices(bool restoreFocus = false)
    {
        var focusedElement = FocusManager.GetFocusedElement(XamlRoot);
        var searching = ReferenceEquals(focusedElement, ChoiceSearch);
        var focused = focusedElement as HandCursorButton;
        var focusedId = focused is not null && Choices.Children.Contains(focused) ? focused.Tag as string : null;
        var focusState = focusedId is not null ? focused!.FocusState : _keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        _selectedButton = null;
        _rows.Clear();
        var compact = _comparisonVariant is 2 or 3;
        ChoiceHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var option in _options)
        {
            var selected = option.Id == SelectedId;
            var grid = new Grid { ColumnSpacing = 12 };
            var branded = !string.IsNullOrEmpty(option.PluginId);
            if (branded)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                grid.Children.Add(new PluginBrandIcon(option.PluginId!) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(new TextBlock { Text = option.Label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextBrush"] });
            if (!compact) labels.Children.Add(new TextBlock { Text = option.Description, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
            Grid.SetColumn(labels, branded ? 1 : 0);
            grid.Children.Add(labels);
            var check = new TypeWhisperGlyph { Kind = "check", Width = 16, Height = 16, Opacity = selected ? 1 : 0 };
            Grid.SetColumn(check, branded ? 2 : 1);
            grid.Children.Add(check);
            var button = new HandCursorButton { Content = grid, MinHeight = compact ? 36 : 56, Padding = new Thickness(12, compact ? 6 : 9, 12, compact ? 6 : 9),
                HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
            button.Tag = option.Id;
            button.IsEnabled = option.Enabled;
            if (selected)
            {
                button.Background = (Brush)Application.Current.Resources["SelectionSurfaceBrush"];
                if (option.Enabled) _selectedButton = button;
            }
            AutomationProperties.SetName(button, Loc.T("{0} option {1}", _automationName, option.Id));
            AutomationProperties.SetHelpText(button, $"{option.Label}. {option.Description}");
            AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
            button.Click += (_, _) => Choose(option);
            _rows.Add((option, button));
        }
        SearchBox.Visibility = Searchable ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
        if (!restoreFocus || searching && Searchable) return;
        var visible = Choices.Children.OfType<HandCursorButton>().ToList();
        (visible.FirstOrDefault(button => button.IsEnabled && button.Tag as string == focusedId)
            ?? (_selectedButton is not null && visible.Contains(_selectedButton) ? _selectedButton : null)
            ?? visible.FirstOrDefault(button => button.IsEnabled))?.Focus(focusState);
    }

    private void Choose(Choice option)
    {
        var changed = SelectedId != option.Id;
        SelectedId = option.Id;
        ChoiceLabel.Text = option.Label;
        UpdateSelectedIcon();
        UpdateComparisonContent();
        ChoiceButton.Flyout.Hide();
        if (changed) SelectionChanged?.Invoke(option.Id);
    }

    // Matches at the start of the name or code come first, then matches at a word start.
    private void ApplyFilter()
    {
        var query = Searchable ? ChoiceSearch.Text.Trim() : "";
        SearchPlaceholder.Visibility = ChoiceSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Choices.Children.Clear();
        foreach (var row in _rows.Select(row => (Rank: MatchRank(row.Option, query), row.Button))
            .Where(row => row.Rank >= 0).OrderBy(row => row.Rank))
            Choices.Children.Add(row.Button);
        var empty = Choices.Children.Count == 0 && query.Length > 0;
        var announce = empty && NoMatches.Visibility == Visibility.Collapsed;
        NoMatches.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // LiveSetting alone does not make Narrator read the message.
        if (announce) (FrameworkElementAutomationPeer.FromElement(NoMatches) ?? FrameworkElementAutomationPeer.CreatePeerForElement(NoMatches))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // 0 name prefix, 1 code prefix, 2 word prefix, 3 inside a word, -1 no match.
    private static int MatchRank(Choice option, string query)
    {
        if (query.Length == 0) return 0;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;
        if (compare.IsPrefix(option.Label, query, options)) return 0;
        if (compare.IsPrefix(option.Id, query, options)) return 1;
        var rank = -1;
        for (var index = compare.IndexOf(option.Label, query, options); index >= 0;
            index = index + 1 < option.Label.Length ? compare.IndexOf(option.Label, query, index + 1, options) : -1)
        {
            if (index == 0 || !char.IsLetterOrDigit(option.Label[index - 1])) return 2;
            rank = 3;
        }
        return rank;
    }

    private void ChoiceSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        (Choices.Parent as ScrollViewer)?.ChangeView(null, 0, null, true);
    }

    private void Choice_Opened(object sender, object e)
    {
        var state = _keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        if (Searchable)
        {
            ChoiceSearch.Focus(state);
            _selectedButton?.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false });
            return;
        }
        (_selectedButton ?? Choices.Children.OfType<HandCursorButton>().FirstOrDefault(button => button.IsEnabled))?.Focus(state);
    }
    private void Choice_Closed(object sender, object e)
    {
        IsPopupOpen = false;
        ChoiceButton.Focus(_keyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }
    internal void ClosePopup() { _keyboard = true; ChoiceButton.Flyout.Hide(); }
    internal void FocusEntry() => ChoiceButton.Focus(FocusState.Programmatic);
    private void Choices_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var inSearch = ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), ChoiceSearch);
        var buttons = Choices.Children.OfType<HandCursorButton>().Where(button => button.IsEnabled).ToList();
        if (e.Key == global::Windows.System.VirtualKey.Escape) { ClosePopup(); e.Handled = true; }
        else if (e.Key == global::Windows.System.VirtualKey.Enter && inSearch)
        {
            // Enter takes the best match, so typing "ger" and Enter picks German.
            if (ChoiceSearch.Text.Trim().Length > 0 && buttons.Count > 0) Choose(_rows.First(row => ReferenceEquals(row.Button, buttons[0])).Option);
            e.Handled = true;
        }
        else if (e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            var down = e.Key == global::Windows.System.VirtualKey.Down;
            var index = inSearch ? -1 : buttons.FindIndex(button => ReferenceEquals(button, FocusManager.GetFocusedElement(XamlRoot)));
            if (Searchable && !down && index <= 0) ChoiceSearch.Focus(FocusState.Keyboard);
            else if (buttons.Count > 0) buttons[Math.Clamp(index + (down ? 1 : -1), 0, buttons.Count - 1)].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    // Typing while a choice has focus continues in the search field.
    private void Choices_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (!Searchable || char.IsControl(e.Character) || char.IsWhiteSpace(e.Character)
            || ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), ChoiceSearch)) return;
        ChoiceSearch.Text += e.Character;
        ChoiceSearch.Select(ChoiceSearch.Text.Length, 0);
        ChoiceSearch.Focus(FocusState.Keyboard);
        e.Handled = true;
    }
}
