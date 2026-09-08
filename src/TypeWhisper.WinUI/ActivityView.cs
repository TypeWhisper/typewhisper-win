using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed class ActivityView : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 20 };
    private readonly Grid _header = new() { ColumnSpacing = 12, Padding = new Thickness(24, 16, 24, 16) };
    private readonly TextBlock _title = Text("Statistics", 24);
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _scroll;
    private readonly List<Action<double>> _responsive = [];
    private UsagePeriod _period = UsagePeriod.AllTime;
    private HistoryReader? _reader;
    private Func<bool> _historySavingEnabled = () => true;
    private IReadOnlyList<TranscriptionRecord> _records = [];
    private bool _loading;
    private bool _refreshAgain;
    private string? _loadError;
    private DateOnly _rangeStart = DateOnly.FromDateTime(DateTime.Today).AddDays(-29);
    private DateOnly _rangeEnd = DateOnly.FromDateTime(DateTime.Today);
    private DateRangePicker? _rangePicker;
    internal event Action<string>? NavigateRequested;

    public ActivityView()
    {
        var root = new Grid { Background = Brush("InkBrush") }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        _header.ColumnDefinitions.Add(new()); _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; AutomationProperties.SetHeadingLevel(_title, AutomationHeadingLevel.Level1);
        _header.Children.Add(_title); Grid.SetColumn(_periods, 1); _header.Children.Add(_periods);
        root.Children.Add(new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brush("HairlineBrush") });
        _scroll = new ScrollViewer { Content = _body, Padding = new Thickness(24, 20, 24, 24), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll); Content = root;
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto }); _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        SizeChanged += (_, _) =>
        {
            foreach (var resize in _responsive) resize(Math.Max(0, ActualWidth - 48));
            var narrow = ActualWidth < 660;
            Grid.SetRow(_periods, narrow ? 1 : 0); Grid.SetColumn(_periods, narrow ? 0 : 1); Grid.SetColumnSpan(_periods, narrow ? 2 : 1);
            _periods.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
        };
    }

    internal void Connect(HistoryReader reader, Func<bool> historySavingEnabled)
    {
        _reader = reader;
        _historySavingEnabled = historySavingEnabled;
    }

    internal async Task RefreshAsync()
    {
        if (_reader is null) return;
        if (_loading) { _refreshAgain = true; return; }
        _loading = true;
        if (_records.Count == 0) Render();
        try
        {
            do
            {
                _refreshAgain = false;
                _records = await _reader.ReadAsync();
                _loadError = null;
            } while (_refreshAgain);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loadError = "Activity could not be loaded from local history. Try again.";
        }
        finally { _loading = false; Render(); }
    }

    internal void Present() { Render(); _ = RefreshAsync(); }
    internal bool CloseRangeIfOpen()
    {
        if (_rangePicker?.IsOpen != true) return false;
        _rangePicker.Close(); return true;
    }
    private void Render()
    {
        _rangePicker?.Close(); _rangePicker = null;
        _body.Children.Clear(); _periods.Children.Clear(); _responsive.Clear();
        _title.Text = "Statistics";
        var data = new UsageData(_records);
        var summary = _period == UsagePeriod.Custom
            ? data.SummarizeRange(_rangeStart, _rangeEnd)
            : data.Summarize(_period);
        {
            foreach (var period in new[] { UsagePeriod.Week, UsagePeriod.Month, UsagePeriod.AllTime })
            {
                var label = period switch { UsagePeriod.Week => "Week", UsagePeriod.Month => "Month", _ => "All history" };
                var button = Button(label, () => { _period = period; Render(); }, period == _period);
                AutomationProperties.SetItemStatus(button, period == _period ? "Selected" : "Not selected"); _periods.Children.Add(button);
            }
            _rangePicker = new DateRangePicker(_rangeStart, _rangeEnd, _period == UsagePeriod.Custom);
            _rangePicker.Applied += (start, end) => { _rangeStart = start; _rangeEnd = end; _period = UsagePeriod.Custom; Render(); };
            _periods.Children.Add(_rangePicker);
        }
        if (_reader is null || _loading && _records.Count == 0 || _loadError is not null)
        {
            _body.Children.Add(Text(_loadError ?? (_reader is null ? "Activity is not connected to history." : "Loading local history…"), 14));
            if (_loadError is not null) _body.Children.Add(Button("Retry", () => _ = RefreshAsync()));
        }
        else if (summary.Transcriptions == 0) RenderEmpty();
        else RenderStatistics(summary);
        _body.Children.Add(Text("Based only on entries currently saved in local history. Editing, deletion and retention change these figures. Dictations that were not saved are not counted. Dates and hours use this device's local time.", 11, true));
        if (!_historySavingEnabled()) _body.Children.Add(Text("History saving is off. New dictations will not appear in these statistics. Existing saved entries are still included.", 12, true));
        foreach (var resize in _responsive) resize(Math.Max(0, ActualWidth - 48));
        _scroll.ChangeView(null, 0, null, true);
    }

    private void RenderEmpty()
    {
        var body = new StackPanel { Spacing = 12, Padding = new Thickness(8, 28, 8, 28) };
        body.Children.Add(new TypeWhisperGlyph { Kind = "signal", Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Center });
        var title = Text("No saved activity in this date range", 20); title.TextAlignment = TextAlignment.Center; body.Children.Add(title);
        var hint = Text("Choose another date range or save a new dictation to history.", 13, true); hint.TextAlignment = TextAlignment.Center; body.Children.Add(hint);
        var history = Button("Open history", () => NavigateRequested?.Invoke("History"), true); history.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(history);
        _body.Children.Add(Card(body));
    }
    private void RenderStatistics(UsageSummary summary)
    {
        _body.Children.Add(MetricGrid([
            ("Days with saved entries", summary.ActiveDays.ToString(), "calendar"), ("Saved entries", summary.Transcriptions.ToString("N0"), "signal"),
            ("Recorded apps", summary.KnownApps.ToString(), "desktop"), ("Recorded models", summary.KnownModels.ToString(), "chip")], false));
        _body.Children.Add(MetricGrid([("Words", summary.Words.ToString("N0"), "text"),
            ("Recorded minutes", summary.Minutes.ToString("N1"), "history")], false));
        _body.Children.Add(ActivityChart(summary));
        var usage = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        usage.Children.Add(Ranking("Top apps", summary.Apps, summary.Transcriptions)); usage.Children.Add(Ranking("Models used", summary.Models, summary.Transcriptions));
        ResponsiveColumns(usage, 2, 560); _body.Children.Add(usage);
        _body.Children.Add(Heatmap(summary));
        _body.Children.Add(Text("Words use the current displayed transcript. Recorded minutes sum the stored duration; entries without a recorded duration contribute zero. Rankings count saved entries, including entries with missing attribution.", 11, true));
    }
    private Grid MetricGrid((string Label, string Value, string Icon)[] metrics, bool links)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (var metric in metrics)
        {
            var panel = new StackPanel { Spacing = 9, Padding = new Thickness(2, 4, 2, 4), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TypeWhisperGlyph { Kind = metric.Icon, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center });
            var number = Text(metric.Value, 27); number.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; number.TextAlignment = TextAlignment.Center; panel.Children.Add(number);
            var label = Text(metric.Label, 11, true); label.TextAlignment = TextAlignment.Center; panel.Children.Add(label);
            var card = Card(panel, 12); AutomationProperties.SetName(card, $"{metric.Label}: {metric.Value}");
            if (links)
            {
                var button = Button("", () => NavigateRequested?.Invoke("Statistics")); button.Content = card;
                button.Padding = new Thickness(0); button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.HorizontalAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(button, $"{metric.Label}: {metric.Value}. View statistics"); grid.Children.Add(button);
            }
            else grid.Children.Add(card);
        }
        ResponsiveColumns(grid, Math.Min(4, metrics.Length), 600); return grid;
    }
    private void ResponsiveColumns(Grid grid, int wideColumns, double breakpoint)
    {
        var last = 0;
        void Resize(double width)
        {
            var columns = width >= breakpoint ? wideColumns : wideColumns == 4 ? 2 : 1;
            if (columns == last) return; last = columns; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new());
            for (var i = 0; i < (grid.Children.Count + columns - 1) / columns; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var i = 0; i < grid.Children.Count; i++) { Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns); Grid.SetRow((FrameworkElement)grid.Children[i], i / columns); }
        }
        _responsive.Add(Resize);
    }
    private Border ActivityChart(UsageSummary summary)
    {
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(Text("Activity", 14));
        var buckets = summary.Days.Chunk(Math.Max(1, (int)Math.Ceiling(summary.Days.Length / 90d)))
            .Select(days => (Date: days[0].Date, End: days[^1].Date, Words: days.Sum(day => day.Words))).ToArray();
        if (summary.Transcriptions == 0) panel.Children.Add(Text("No activity in this date range.", 11, true));
        var chart = new Grid { Height = 170, ColumnSpacing = 8 }; chart.ColumnDefinitions.Add(new()); chart.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
        var plot = new Grid(); chart.Children.Add(plot); var max = Math.Max(100, (int)(Math.Ceiling(buckets.Max(day => day.Words) / 500d) * 500));
        var axis = new Grid(); Grid.SetColumn(axis, 1); chart.Children.Add(axis);
        for (var i = 0; i < 5; i++)
        {
            var line = new Border { Height = 1, Background = Brush("HairlineBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, i * 40, 0, 0) }; plot.Children.Add(line);
            axis.Children.Add(new TextBlock { Text = ((4 - i) * max / 4).ToString("N0"), FontSize = 10, Foreground = Brush("MutedBrush"), Margin = new Thickness(0, i * 40 - 6, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        }
        var bars = new Grid { ColumnSpacing = 2, Margin = new Thickness(0, 0, 0, 10) }; plot.Children.Add(bars);
        foreach (var day in buckets)
        {
            bars.ColumnDefinitions.Add(new());
            var bar = new Border { Height = Math.Max(day.Words == 0 ? 0 : 2, 160d * day.Words / max), Background = Brush("AccentBrush"), CornerRadius = new CornerRadius(3, 3, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Opacity = .78 };
            // The shared button presenter centers content. A full-height plot cell
            // keeps each bar anchored to the zero baseline, not vertically centered.
            var plotCell = new Grid { Height = 160 }; plotCell.Children.Add(bar);
            var button = Button("", () => { }); button.Content = plotCell; button.Style = (Style)Application.Current.Resources["IconButtonStyle"];
            button.Padding = new Thickness(0); button.MinWidth = 0; button.MinHeight = 0; button.VerticalContentAlignment = VerticalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            var dates = day.End == day.Date ? $"{day.Date:MMM d, yyyy}" : $"{day.Date:MMM d, yyyy} – {day.End:MMM d, yyyy}";
            var value = $"{day.Words:N0} {(day.Words == 1 ? "word" : "words")}";
            var tooltipContent = new StackPanel { Spacing = 5 };
            tooltipContent.Children.Add(Text(dates, 11, true)); tooltipContent.Children.Add(Text(value, 14));
            var tooltip = new ToolTip { Content = tooltipContent, Style = (Style)Application.Current.Resources["HeatmapToolTipStyle"] };
            ToolTipService.SetToolTip(button, tooltip);
            ToolTipService.SetPlacement(button, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Top);
            AutomationProperties.SetName(button, $"{dates}: {value}");
            var over = false;
            void Update()
            {
                var active = over || button.FocusState == FocusState.Keyboard;
                bar.Opacity = active ? 1 : .78; tooltip.IsOpen = active;
            }
            button.PointerEntered += (_, _) => { over = true; Update(); };
            button.PointerExited += (_, _) => { over = false; Update(); };
            button.GotFocus += (_, _) => Update(); button.LostFocus += (_, _) => Update();
            button.Unloaded += (_, _) => tooltip.IsOpen = false;
            button.KeyDown += (_, e) =>
            {
                if (e.Key == global::Windows.System.VirtualKey.Escape && tooltip.IsOpen)
                { tooltip.IsOpen = false; e.Handled = true; }
            };
            Grid.SetColumn(button, bars.Children.Count); bars.Children.Add(button);
        }
        panel.Children.Add(chart);
        var labels = new Grid(); labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new());
        for (var i = 0; i < 3; i++) { var label = Text(summary.Days[(summary.Days.Length - 1) * i / 2].Date.ToString("MMM d"), 10, true); label.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right; Grid.SetColumn(label, i); labels.Children.Add(label); }
        panel.Children.Add(labels); return Card(panel);
    }
    private Border Ranking(string title, UsageRank[] ranks, int total)
    {
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(Text(title, 14));
        foreach (var rank in ranks)
        {
            var row = new StackPanel { Spacing = 5 }; var labels = new Grid(); labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            labels.Children.Add(Text(rank.Name, 12)); var value = Text(rank.Count.ToString(), 11, true); value.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(value, 1); labels.Children.Add(value); row.Children.Add(labels);
            var track = new Grid { Height = 5, Background = Brush("HairlineBrush"), CornerRadius = new CornerRadius(2) };
            var fill = new Border { Height = 5, Background = Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * rank.Count / Math.Max(1, total); track.Children.Add(fill); row.Children.Add(track); body.Children.Add(row);
        }
        var card = Card(body); card.VerticalAlignment = VerticalAlignment.Top; return card;
    }
    private Border Heatmap(UsageSummary summary)
    {
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(Text("Usage by time of day", 14));
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
        for (var hour = 0; hour < 24; hour++) grid.ColumnDefinitions.Add(new());
        for (var row = 0; row < 8; row++) grid.RowDefinitions.Add(new() { Height = new GridLength(17) });
        for (var hour = 0; hour < 24; hour += 6) { var label = Text(hour.ToString(), 9, true); Grid.SetColumn(label, hour + 1); grid.Children.Add(label); }
        var max = Math.Max(1, summary.Hours.Cast<int>().Max()); var names = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var cells = new List<HandCursorButton>();
        for (var day = 0; day < 7; day++)
        {
            var label = Text(names[day], 10, true); Grid.SetRow(label, day + 1); grid.Children.Add(label);
            for (var hour = 0; hour < 24; hour++)
            {
                var count = summary.Hours[day, hour];
                var caption = $"{names[day]} · {hour:00}:00–{hour:00}:59 · {count} {(count == 1 ? "transcription" : "transcriptions")}";
                var opacity = count == 0 ? .65 : .2 + .8 * count / max;
                var fill = new Border { Background = Brush(count == 0 ? "HairlineBrush" : "AccentBrush"), Opacity = opacity, CornerRadius = new CornerRadius(2) };
                var outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
                var surface = new Grid { Height = 17 }; surface.Children.Add(fill); surface.Children.Add(outline);
                var cell = Button("", () => { });
                cell.Style = (Style)Application.Current.Resources["IconButtonStyle"];
                cell.Content = surface; cell.Padding = new Thickness(0); cell.MinHeight = cell.MinWidth = 0;
                cell.CornerRadius = new CornerRadius(2);
                cell.HorizontalAlignment = HorizontalAlignment.Stretch; cell.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                cell.IsTabStop = cells.Count == 0;
                var tooltipContent = new StackPanel { Spacing = 5 };
                tooltipContent.Children.Add(Text($"{names[day]} · {hour:00}:00–{hour:00}:59", 11, true));
                tooltipContent.Children.Add(Text($"{count} {(count == 1 ? "transcription" : "transcriptions")}", 14));
                var tooltip = new ToolTip { Content = tooltipContent, Style = (Style)Application.Current.Resources["HeatmapToolTipStyle"] };
                ToolTipService.SetToolTip(cell, tooltip);
                ToolTipService.SetPlacement(cell, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Top);
                var over = false;
                void Update()
                {
                    var active = over || cell.FocusState != FocusState.Unfocused;
                    outline.BorderBrush = active ? Brush("FocusBrush") : null;
                    fill.Opacity = active ? 1 : opacity;
                    tooltip.IsOpen = over || cell.FocusState == FocusState.Keyboard;
                }
                cell.PointerEntered += (_, _) => { over = true; Update(); };
                cell.PointerExited += (_, _) => { over = false; Update(); };
                cell.GotFocus += (_, _) => { foreach (var item in cells) item.IsTabStop = ReferenceEquals(item, cell); Update(); };
                cell.LostFocus += (_, _) => Update();
                cell.Unloaded += (_, _) => tooltip.IsOpen = false;
                var index = cells.Count;
                cell.KeyDown += (_, e) =>
                {
                    if (e.Key == global::Windows.System.VirtualKey.Escape && tooltip.IsOpen)
                    { tooltip.IsOpen = false; e.Handled = true; return; }
                    var destination = e.Key switch
                    {
                        global::Windows.System.VirtualKey.Left => index % 24 > 0 ? index - 1 : index,
                        global::Windows.System.VirtualKey.Right => index % 24 < 23 ? index + 1 : index,
                        global::Windows.System.VirtualKey.Up => index >= 24 ? index - 24 : index,
                        global::Windows.System.VirtualKey.Down => index < 144 ? index + 24 : index,
                        _ => -1
                    };
                    if (destination < 0) return;
                    cells[destination].Focus(FocusState.Keyboard); e.Handled = true;
                };
                AutomationProperties.SetName(cell, caption);
                cells.Add(cell); Grid.SetColumn(cell, hour + 1); Grid.SetRow(cell, day + 1); grid.Children.Add(cell);
            }
        }
        AutomationProperties.SetName(grid, "Hourly activity, Monday to Sunday, 00:00 to 23:00. Darker cells mean less activity.");
        body.Children.Add(grid); body.Children.Add(Text("Less  ░ ▒ ▓  More · local time", 10, true)); return Card(body);
    }
    private static Border Card(UIElement child, double padding = 18) => new() { Child = child, Padding = new Thickness(padding), Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
    private static HandCursorButton Button(string label, Action click, bool primary = false)
    {
        var button = new HandCursorButton { Content = label, Style = (Style)Application.Current.Resources[primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"] };
        button.Click += (_, _) => click(); return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
