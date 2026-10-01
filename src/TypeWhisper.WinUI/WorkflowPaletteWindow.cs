using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;
using global::Windows.Graphics;
using global::Windows.System;

namespace TypeWhisper.WinUI;

// The macOS workflow palette: a small floating panel that runs a workflow on the selected text
// or inserts a recent transcription. It closes when it loses focus.
internal sealed class WorkflowPaletteWindow : Window
{
    private const int WorkflowWidth = 380, WorkflowHeight = 344, RecentWidth = 520, RecentHeight = 380;
    private readonly IReadOnlyList<WorkflowPaletteItem> _workflows;
    private readonly IReadOnlyList<TranscriptionRecord> _recent;
    private readonly bool _hasText, _recentOnly;
    private readonly TextBox _search = new() { PlaceholderText = Loc.T("Search workflows…") };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _hint = Text("", 11, muted: true);
    private readonly Grid _status = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(20) };
    private readonly TextBlock _statusText = Text("", 14);
    private readonly ProgressRing _progress = new() { IsActive = true, Width = 28, Height = 28 };
    private readonly HandCursorButton _statusClose = new() { Content = Loc.T("Close"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly List<(HandCursorButton Button, Action Run)> _items = [];
    private int _selected = -1;
    private bool _recentLevel;
    private bool _busy;
    private bool _closing;
    private DisplayArea? _area;

    internal Func<WorkflowPaletteItem, Task>? RunWorkflow { get; set; }
    internal Func<TranscriptionRecord, Task>? InsertRecent { get; set; }
    internal Action? Cancel { get; set; }
    internal bool IsClosed => _closing;

    internal WorkflowPaletteWindow(IReadOnlyList<WorkflowPaletteItem> workflows, IReadOnlyList<TranscriptionRecord> recent, bool hasText, bool recentOnly = false)
    {
        _workflows = workflows;
        _recent = recent;
        _hasText = hasText;
        _recentOnly = recentOnly;
        Title = recentOnly ? Loc.T("Recent Transcriptions") : Loc.T("Workflow Palette");
        var root = new Grid { Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetName(_search, Loc.T("Search workflows and recent transcriptions"));
        root.Children.Add(_search);
        _scroll = new ScrollViewer { Content = _rows, Margin = new Thickness(0, 8, 0, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        _hint.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetRow(_hint, 2); root.Children.Add(_hint);
        var statusPanel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        statusPanel.Children.Add(_progress);
        _statusText.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetLiveSetting(_statusText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        statusPanel.Children.Add(_statusText);
        _statusClose.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
        _statusClose.Click += (_, _) => Dismiss();
        statusPanel.Children.Add(_statusClose);
        _status.Children.Add(statusPanel);
        Grid.SetRowSpan(_status, 3); root.Children.Add(_status);
        Content = root;

        _search.TextChanged += (_, _) => Render();
        Closed += (_, _) => _closing = true;
        root.PreviewKeyDown += Root_PreviewKeyDown;
        Activated += (_, args) =>
        {
            // A click outside hides the panel, as on macOS; a running workflow keeps it for its result.
            if (args.WindowActivationState == WindowActivationState.Deactivated && !_busy && !_closing) Dismiss();
        };
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        NativeWindowAppearance.RemoveSystemBorder(this);
        RoundCorners();
        // Without text, only recent transcriptions can be inserted, as on macOS.
        _recentLevel = !_hasText || _workflows.Count == 0;
        Render();
    }

    internal void ShowOn(DisplayArea area)
    {
        _area = area;
        Place();
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _search.Focus(FocusState.Programmatic);
    }

    // Centered horizontally on the display under the pointer, a little above the middle.
    private void Place()
    {
        if (_area is not { } area) return;
        var work = area.WorkArea;
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96d;
        var width = (int)((_recentLevel ? RecentWidth : WorkflowWidth) * scale);
        var height = (int)((_recentLevel ? RecentHeight : WorkflowHeight) * scale);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2 - (int)(60 * scale), width, height));
    }

    internal void ShowRunning(string name)
    {
        if (_closing) return;
        _busy = true;
        AppWindow.Show();
        _status.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _statusClose.Visibility = Visibility.Collapsed;
        _statusText.Text = name + "…";
    }

    internal void ShowMessage(string message)
    {
        if (_closing) return;
        _busy = false;
        _status.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Collapsed;
        _statusClose.Visibility = Visibility.Visible;
        _statusText.Text = message;
        AppWindow.Show();
        Activate();
        _statusClose.Focus(FocusState.Programmatic);
    }

    // Hides the panel so the source app can take the foreground for insertion.
    internal void HideForInsertion()
    {
        if (_closing) return;
        _busy = true;
        AppWindow.Hide();
    }

    internal void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Render()
    {
        _rows.Children.Clear();
        _items.Clear();
        var query = _search.Text.Trim();
        _search.PlaceholderText = _recentLevel ? Loc.T("Search recent transcriptions…") : Loc.T("Search workflows…");
        if (!_recentLevel)
        {
            foreach (var item in WorkflowPalette.Filter(_workflows, query))
                Add(Row(WorkflowIcons.Normalize(item.Workflow.Icon), item.Workflow.Name, item.Subtitle), () => Run(item));
            var recent = FilterRecent(query);
            if (recent.Count > 0)
            {
                // Typing searches both levels; the group opens the full list.
                if (query.Length == 0)
                    Add(Row("history", Loc.T("Recent Transcriptions"), recent.Count == 1 ? Loc.T("1 recent transcription") : Loc.T("{0} recent transcriptions", recent.Count), chevron: true), OpenRecent);
                else foreach (var record in recent.Take(5)) Add(RecentRow(record), () => Insert(record));
            }
        }
        else
        {
            if (!_hasText && !_recentOnly)
            {
                var note = Text(Loc.T("No text is selected or copied. Choose a recent transcription to insert."), 12, muted: true);
                note.Margin = new Thickness(6, 2, 6, 8);
                _rows.Children.Add(note);
            }
            foreach (var record in FilterRecent(query)) Add(RecentRow(record), () => Insert(record));
        }
        if (_items.Count == 0)
        {
            var empty = Text(query.Length > 0 ? Loc.T("No matches.") : Loc.T("Nothing to show yet."), 13, muted: true);
            empty.Margin = new Thickness(6, 12, 6, 0);
            _rows.Children.Add(empty);
        }
        _hint.Text = _recentLevel && _hasText && _workflows.Count > 0 ? Loc.T("↑↓ Select   Enter Insert   Esc Back") : _recentLevel ? Loc.T("↑↓ Select   Enter Insert   Esc Close") : Loc.T("↑↓ Select   Enter Run   Esc Close");
        Select(_items.Count > 0 ? 0 : -1);
    }

    private IReadOnlyList<TranscriptionRecord> FilterRecent(string query) => query.Length == 0 ? _recent
        : _recent.Where(record => record.DisplayText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || record.AppName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true).ToArray();

    private void Add(HandCursorButton button, Action run)
    {
        var index = _items.Count;
        button.Click += (_, _) => run();
        button.PointerEntered += (_, _) => Select(index);
        _items.Add((button, run));
        _rows.Children.Add(button);
    }

    private void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _items.Count; i++)
        {
            var selected = i == index;
            _items[i].Button.Style = (Style)Application.Current.Resources[selected ? "SidebarSelectedButtonStyle" : "MenuButtonStyle"];
            AutomationProperties.SetItemStatus(_items[i].Button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
            foreach (var glyph in Descendants<TypeWhisperGlyph>(_items[i].Button.Content as Panel)) glyph.Inverse = selected;
            foreach (var text in Descendants<TextBlock>(_items[i].Button.Content as Panel))
                text.Foreground = selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : Brush(text.Tag as string == "muted" ? "MutedBrush" : "TextBrush");
        }
        if (index >= 0) _items[index].Button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void OpenRecent()
    {
        _recentLevel = true;
        _search.Text = "";
        Place();
        Render();
    }

    private void Run(WorkflowPaletteItem item)
    {
        if (_busy || RunWorkflow is null) return;
        _ = RunWorkflow(item);
    }

    private void Insert(TranscriptionRecord record)
    {
        if (_busy || InsertRecent is null) return;
        _ = InsertRecent(record);
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_status.Visibility == Visibility.Visible)
        {
            if (e.Key != VirtualKey.Escape) return;
            // Esc cancels a running workflow; the panel then reports the cancellation.
            if (_busy) Cancel?.Invoke(); else Dismiss();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Down or VirtualKey.Up:
                Select(WorkflowPalette.Move(_selected, e.Key == VirtualKey.Down ? 1 : -1, _items.Count));
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (_selected >= 0) _items[_selected].Run();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                // Esc inside the recent group goes back to the workflows with the group selected.
                if (_recentLevel && _hasText && _workflows.Count > 0)
                {
                    _recentLevel = false;
                    _search.Text = "";
                    Place();
                    Render();
                    Select(_items.Count - 1);
                }
                else Dismiss();
                e.Handled = true;
                break;
        }
    }

    private static HandCursorButton Row(string icon, string title, string subtitle, bool chevron = false)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
        var copy = new StackPanel { Spacing = 1 };
        var name = Text(title, 13, bold: true); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        copy.Children.Add(name);
        var sub = Text(subtitle, 11, muted: true); sub.TextTrimming = TextTrimming.CharacterEllipsis; sub.TextWrapping = TextWrapping.NoWrap;
        copy.Children.Add(sub);
        Grid.SetColumn(copy, 1); grid.Children.Add(copy);
        if (chevron) { var arrow = Text("›", 16, muted: true); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 2); grid.Children.Add(arrow); }
        var button = new HandCursorButton { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 7, 10, 7), IsTabStop = false, Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
        AutomationProperties.SetName(button, title + ", " + subtitle);
        return button;
    }

    private static HandCursorButton RecentRow(TranscriptionRecord record)
    {
        var text = record.DisplayText.ReplaceLineEndings(" ").Trim();
        var local = DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc).ToLocalTime();
        var when = local.Date == DateTime.Today ? local.ToString("t") : local.ToString("g");
        return Row("text", text.Length == 0 ? Loc.T("Untitled transcript") : text, string.IsNullOrWhiteSpace(record.AppName) ? when : when + " · " + record.AppName);
    }

    private void RoundCorners()
    {
        // Windows 11 rounds borderless windows only when asked.
        var preference = 2; // DWMWCP_ROUND
        _ = NativeMethods.DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 33, ref preference, sizeof(int));
    }

    private static IEnumerable<T> Descendants<T>(Panel? panel) where T : class
    {
        if (panel is null) yield break;
        foreach (var child in panel.Children)
        {
            if (child is T match) yield return match;
            if (child is Panel nested) foreach (var inner in Descendants<T>(nested)) yield return inner;
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Tag = muted ? "muted" : null,
        Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
}
