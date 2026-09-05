using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// A small source-owned calendar using the approved picker surface and buttons.
public sealed class PrototypeDateRangePicker : UserControl
{
    private readonly HandCursorButton _button;
    private readonly Flyout _flyout;
    private readonly StackPanel _panel = new() { Width = 320, Spacing = 12 };
    private readonly StackPanel _calendar = new() { Spacing = 6 };
    private readonly TextBlock _message = Label("", 11);
    private TextBox _from = null!;
    private TextBox _to = null!;
    private HandCursorButton _apply = null!;
    private DateOnly _start, _end, _month;
    private bool _editingStart = true;
    internal bool IsOpen { get; private set; }
    internal event Action<DateOnly, DateOnly>? Applied;

    internal PrototypeDateRangePicker(DateOnly start, DateOnly end, bool active)
    {
        _start = start; _end = end; _month = new(start.Year, start.Month, 1);
        _button = Button(active ? $"{start:dd.MM.yy} – {end:dd.MM.yy}" : "Custom…", () => _flyout!.ShowAt(_button!), active);
        AutomationProperties.SetName(_button, "Choose custom date range");
        ToolTipService.SetToolTip(_button, "Choose an inclusive start and end date");
        _flyout = new Flyout { Content = new ScrollViewer { Content = _panel, MaxHeight = 540, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, FlyoutPresenterStyle = (Style)Application.Current.Resources["PrototypeRangeFlyoutStyle"] };
        _flyout.Opening += (_, _) => { IsOpen = true; Build(); };
        _flyout.Opened += (_, _) => { _from.Focus(FocusState.Programmatic); _from.SelectAll(); };
        _flyout.Closed += (_, _) => { IsOpen = false; _button.Focus(FocusState.Programmatic); };
        _panel.KeyDown += (_, e) =>
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape) { Close(); e.Handled = true; }
        };
        Unloaded += (_, _) => Close(); Content = _button;
    }
    internal void Close() => _flyout.Hide();
    private static bool Parse(string text, out DateOnly date) => DateOnly.TryParseExact(text.Trim(), ["dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    private void Build()
    {
        _panel.Children.Clear(); _editingStart = true; _month = new(_start.Year, _start.Month, 1);
        _panel.Children.Add(Label("Custom date range", 16));
        var fields = new Grid { ColumnSpacing = 12 }; fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new());
        _from = Field("From", _start, fields, 0); _to = Field("To", _end, fields, 1); _panel.Children.Add(fields);
        _panel.Children.Add(Label("DD.MM.YYYY · both dates are included", 11));
        _panel.Children.Add(_calendar); _panel.Children.Add(_message);
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(Button("Cancel", Close)); _apply = Button("Apply", Apply, true); actions.Children.Add(_apply); _panel.Children.Add(actions);
        _from.TextChanged += (_, _) => Validate(); _to.TextChanged += (_, _) => Validate();
        _from.GotFocus += (_, _) => SelectField(true); _to.GotFocus += (_, _) => SelectField(false);
        Validate(); RenderCalendar();
    }
    private TextBox Field(string name, DateOnly date, Grid grid, int column)
    {
        var field = new StackPanel { Spacing = 6 }; field.Children.Add(Label(name, 12));
        var input = new TextBox { Text = date.ToString("dd.MM.yyyy"), MaxLength = 10, Height = 38, Padding = new Thickness(8), Style = (Style)Application.Current.Resources["PrototypeSearchTextBoxStyle"], IsSpellCheckEnabled = false };
        AutomationProperties.SetName(input, name + " date, day month year");
        var border = new Border { Child = input, BorderThickness = new Thickness(1), BorderBrush = Brush("HairlineBrush"), CornerRadius = new CornerRadius(7), Background = Brush("InkBrush") };
        input.GotFocus += (_, _) => border.BorderBrush = Brush("FocusBrush"); input.LostFocus += (_, _) => border.BorderBrush = Brush("HairlineBrush");
        field.Children.Add(border); Grid.SetColumn(field, column); grid.Children.Add(field); return input;
    }
    private void SelectField(bool start)
    {
        _editingStart = start;
        if (Parse((start ? _from : _to).Text, out var date) && date.Year is >= 1900 and <= 2100) _month = new(date.Year, date.Month, 1);
        RenderCalendar();
    }
    private void Validate()
    {
        var valid = Parse(_from.Text, out var from) && Parse(_to.Text, out _);
        var error = valid ? PrototypeUsageData.ValidateRange(from, DateOnly.ParseExact(_to.Text.Trim(), ["dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture)) : "Enter valid dates as DD.MM.YYYY or YYYY-MM-DD.";
        _message.Text = error ?? "Select a date below or type it above."; _apply.IsEnabled = error is null;
    }
    private void Apply()
    {
        if (!Parse(_from.Text, out var start) || !Parse(_to.Text, out var end) || PrototypeUsageData.ValidateRange(start, end) is not null) return;
        _start = start; _end = end; Close(); Applied?.Invoke(start, end);
    }
    private void RenderCalendar()
    {
        _calendar.Children.Clear();
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(38) }); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = new GridLength(38) });
        var previous = Button("‹", () => { _month = _month.AddMonths(-1); RenderCalendar(); }); previous.IsEnabled = _month > new DateOnly(1900, 1, 1); AutomationProperties.SetName(previous, "Previous month"); heading.Children.Add(previous);
        var title = Label(_month.ToString("MMMM yyyy", CultureInfo.CurrentCulture), 13); title.VerticalAlignment = VerticalAlignment.Center; title.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(title, 1); heading.Children.Add(title);
        var next = Button("›", () => { _month = _month.AddMonths(1); RenderCalendar(); }); next.IsEnabled = _month < new DateOnly(2100, 12, 1); AutomationProperties.SetName(next, "Next month"); Grid.SetColumn(next, 2); heading.Children.Add(next); _calendar.Children.Add(heading);
        _calendar.Children.Add(Label(_editingStart ? "Selecting start date" : "Selecting end date", 11));
        var days = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        for (var col = 0; col < 7; col++) days.ColumnDefinitions.Add(new());
        for (var row = 0; row < 7; row++) days.RowDefinitions.Add(new() { Height = new GridLength(row == 0 ? 22 : 32) });
        var names = new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
        for (var i = 0; i < 7; i++) { var dayName = Label(names[i], 10); dayName.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(dayName, i); days.Children.Add(dayName); }
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        Parse(_from.Text, out var start); Parse(_to.Text, out var end);
        for (var day = 1; day <= DateTime.DaysInMonth(_month.Year, _month.Month); day++)
        {
            var date = new DateOnly(_month.Year, _month.Month, day);
            var button = Button(day.ToString(), () =>
            {
                (_editingStart ? _from : _to).Text = date.ToString("dd.MM.yyyy");
                if (_editingStart) { _to.Focus(FocusState.Programmatic); _to.SelectAll(); } else RenderCalendar();
            }, date == start || date == end);
            button.Padding = new Thickness(0); button.MinHeight = 28; button.MinWidth = 0; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (date > start && date < end) button.Background = Brush("ElevatedBrush");
            AutomationProperties.SetName(button, date.ToString("D", CultureInfo.CurrentCulture));
            Grid.SetRow(button, 1 + (offset + day - 1) / 7); Grid.SetColumn(button, (offset + day - 1) % 7); days.Children.Add(button);
        }
        _calendar.Children.Add(days);
    }
    private static HandCursorButton Button(string text, Action click, bool primary = false)
    {
        var button = new HandCursorButton { Content = text, Style = (Style)Application.Current.Resources[primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
        button.Click += (_, _) => click(); return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, Foreground = Brush(size <= 12 ? "MutedBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap };
}
