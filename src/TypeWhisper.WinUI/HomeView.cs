using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// The settings start page, following the macOS dashboard: activity totals and recent transcriptions.
public sealed class HomeView : UserControl
{
    // macOS estimates saved time against typing at 45 words per minute.
    private const double TypingWordsPerMinute = 45;
    private readonly StackPanel _body = new() { Spacing = 20, Padding = new Thickness(24, 20, 24, 24) };
    private HistoryReader? _reader;
    private Func<string> _shortcut = () => "";
    private IReadOnlyList<TranscriptionRecord> _records = [];
    private bool _loading;
    private bool _refreshAgain;
    private string? _loadError;
    internal event Action<string>? NavigateRequested;

    public HomeView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var title = Text("Dashboard", 22);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        root.Children.Add(new Border { Child = title, Padding = new Thickness(24, 14, 24, 16),
            BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
        var scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
    }

    internal void Connect(HistoryReader reader, Func<string> dictationShortcut)
    {
        _reader = reader;
        _shortcut = dictationShortcut;
    }

    internal void Present() { Render(); _ = RefreshAsync(); }

    internal async Task RefreshAsync()
    {
        if (_reader is null) return;
        if (_loading) { _refreshAgain = true; return; }
        _loading = true;
        try
        {
            do
            {
                _refreshAgain = false;
                _records = await _reader.ReadAsync();
                _loadError = null;
            } while (_refreshAgain);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _loadError = "Recent activity could not be loaded from local history."; }
        finally { _loading = false; Render(); }
    }

    private void Render()
    {
        _body.Children.Clear();
        if (_loadError is not null)
        {
            _body.Children.Add(Text(_loadError, 13, true));
            _body.Children.Add(Button("Retry", () => _ = RefreshAsync()));
            return;
        }
        _body.Children.Add(_records.Count == 0 && !_loading ? GettingStarted() : Activity());
        _body.Children.Add(Recent());
    }

    private Border GettingStarted()
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(8, 12, 8, 12), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TypeWhisperGlyph { Kind = "microphone", Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center });
        var heading = Text("Ready to start dictating?", 16);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; heading.TextAlignment = TextAlignment.Center;
        panel.Children.Add(heading);
        var shortcut = _shortcut();
        if (string.IsNullOrWhiteSpace(shortcut) || shortcut == "No shortcut assigned")
        {
            var hint = Text("Assign a dictation shortcut in Shortcuts to begin.", 13, true);
            hint.TextAlignment = TextAlignment.Center;
            panel.Children.Add(hint);
        }
        else
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            line.Children.Add(Centered(Text("Press", 13, true)));
            line.Children.Add(new Border { Child = Text(shortcut, 13), Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(5),
                Background = Brush("ElevatedBrush") });
            line.Children.Add(Centered(Text("in any app to begin.", 13, true)));
            panel.Children.Add(line);
        }
        var setup = Button("Open setup wizard", () => NavigateRequested?.Invoke("Setup"));
        setup.HorizontalAlignment = HorizontalAlignment.Center;
        setup.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(setup);
        return Card(panel);
    }

    private Border Activity()
    {
        var summary = new UsageData(_records).Summarize(UsagePeriod.AllTime);
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Header("stats", "Your activity", "View all statistics", "Statistics"));
        var wpm = summary.Minutes > 0 && summary.Words > 0 ? ((int)(summary.Words / summary.Minutes)).ToString() : "—";
        var saved = summary.Words / TypingWordsPerMinute - summary.Minutes;
        (string Label, string Value, string Icon)[] metrics =
        [
            ("Words", summary.Words.ToString("N0"), "text"), ("Avg. WPM", wpm, "speed"),
            ("Apps used", summary.KnownApps.ToString(), "desktop"), ("Time saved", SavedTime(saved), "history")
        ];
        var grid = new Grid { ColumnSpacing = 12 };
        foreach (var (label, value, icon) in metrics)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(4, 14, 4, 14) };
            tile.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center });
            var number = Text(value, 26); number.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; number.TextAlignment = TextAlignment.Center;
            tile.Children.Add(number);
            var caption = Text(label, 12, true); caption.TextAlignment = TextAlignment.Center;
            tile.Children.Add(caption);
            var button = new HandCursorButton
            {
                Content = tile, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0), Background = Brush("InkBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Style = (Style)Application.Current.Resources["MenuButtonStyle"]
            };
            button.Click += (_, _) => NavigateRequested?.Invoke("Statistics");
            AutomationProperties.SetName(button, $"{label}: {value}. View statistics");
            Grid.SetColumn(button, grid.Children.Count);
            grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        return Card(panel);
    }

    private Border Recent()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Header("history", "Recent transcriptions", "View all history", "History"));
        if (_records.Count == 0)
        {
            var shortcut = _shortcut();
            var empty = Text(_loading ? "Loading local history…" : string.IsNullOrWhiteSpace(shortcut) || shortcut == "No shortcut assigned"
                ? "Your transcriptions appear here after your first dictation." : $"Press {shortcut} in any app to get started.", 13, true);
            empty.TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(0, 14, 0, 14);
            panel.Children.Add(empty);
            return Card(panel);
        }
        var rows = new StackPanel();
        foreach (var record in _records.Take(5))
        {
            if (rows.Children.Count > 0)
                rows.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new Thickness(4, 0, 4, 0) });
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel { Spacing = 3 };
            var text = Text(record.DisplayText.ReplaceLineEndings(" "), 14);
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            copy.Children.Add(text);
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            meta.Children.Add(Text(Relative(record.Timestamp), 12, true));
            if (!string.IsNullOrWhiteSpace(record.AppName)) meta.Children.Add(Text("· " + record.AppName, 12, true));
            copy.Children.Add(meta);
            content.Children.Add(copy);
            var chevron = Centered(Text("›", 18, true));
            Grid.SetColumn(chevron, 1);
            content.Children.Add(chevron);
            var button = new HandCursorButton { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 10, 8, 10),
                Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
            button.Click += (_, _) => NavigateRequested?.Invoke("History");
            AutomationProperties.SetName(button, "Open in History: " + text.Text);
            rows.Children.Add(button);
        }
        panel.Children.Add(rows);
        return Card(panel);
    }

    private Grid Header(string icon, string title, string link, string destination)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center });
        var heading = Text(title, 15); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; heading.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Grid.SetColumn(heading, 1); header.Children.Add(heading);
        var label = Text(link + "  ›", 12, true);
        var open = new HandCursorButton { Content = label, Padding = new Thickness(6, 4, 6, 4), Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
        open.Click += (_, _) => NavigateRequested?.Invoke(destination);
        AutomationProperties.SetName(open, link);
        Grid.SetColumn(open, 2); header.Children.Add(open);
        return header;
    }

    private static string SavedTime(double minutes)
    {
        if (minutes <= 0) return "—";
        var total = (int)minutes;
        return total >= 60 ? $"{total / 60}h {total % 60}m" : $"{total}m";
    }

    private static string Relative(DateTime timestamp)
    {
        var local = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime();
        var elapsed = DateTime.Now - local;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromHours(24)) return $"{(int)elapsed.TotalHours} {((int)elapsed.TotalHours == 1 ? "hour" : "hours")} ago";
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return local.ToString("d");
    }

    private static FrameworkElement Centered(FrameworkElement element) { element.VerticalAlignment = VerticalAlignment.Center; return element; }
    private static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(18, 16, 18, 16), Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12)
    };
    private static HandCursorButton Button(string label, Action click)
    {
        var button = new HandCursorButton { Content = label, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += (_, _) => click();
        return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush")
    };
}
