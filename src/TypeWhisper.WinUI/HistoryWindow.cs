using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;
using global::Windows.Graphics;
using global::Windows.System;

namespace TypeWhisper.WinUI;

// The History workspace, following the macOS History window: smart mailboxes and devices,
// a date-grouped list, and a detail pane for reading, editing and acting on entries.
public sealed partial class HistoryWindow : Window
{
    private const int PageSize = 100;
    private readonly HistoryReader _reader;
    private readonly HistoryActions _actions;
    private readonly IHistoryAudioService? _audio;
    private readonly StackPanel _sidebar = new() { Spacing = 2, Padding = new Thickness(10, 4, 10, 16) };
    private readonly StackPanel _list = new() { Padding = new Thickness(10, 0, 10, 16) };
    private readonly ScrollViewer _listScroll;
    private readonly TextBlock _listTitle = Text(Loc.T("All History"), 15, bold: true);
    private readonly TextBlock _listCount = Text("", 12, muted: true);
    private readonly TextBox _search = new() { PlaceholderText = Loc.T("Search History"), VerticalAlignment = VerticalAlignment.Center };
    private readonly DropDownButton _filterButton = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DropDownButton _sortButton = new() { Content = Text(Loc.T("Sort"), 12), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _toolbarActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _detailHost = new();
    private readonly TextBlock _notice = Text("", 12, muted: true);
    private IReadOnlyList<TranscriptionRecord> _records = [];
    private IReadOnlyList<TranscriptionRecord> _visible = [];
    private readonly HashSet<HistoryDateGroup> _collapsed = [];
    private readonly List<string> _selection = [];
    private string? _anchor;
    private int _shown = PageSize;
    private HistoryScope _scope = HistoryScope.All;
    private HistoryDateRange _range = HistoryDateRange.AllTime;
    private string? _app;
    private HistorySort _sort = HistorySort.NewestFirst;
    private bool _loading = true;
    private string? _loadError;
    private bool _closing;
    private bool _allowClose;
    private bool _dialogOpen;

    internal Func<bool>? CanPlayAudio { get; set; }
    internal Func<Task>? PrepareAudioPlayback { get; set; }
    // History sync identity of this PC and the devices found in the sync folder.
    internal Func<string?> LocalDeviceId { get; set; } = () => null;
    internal Func<IReadOnlyDictionary<string, TypeWhisper.Core.Services.Sync.HistorySyncDevice>> Devices { get; set; } =
        () => new Dictionary<string, TypeWhisper.Core.Services.Sync.HistorySyncDevice>();

    internal HistoryWindow(HistoryReader reader, HistoryActions actions, IHistoryAudioService? audio)
    {
        _reader = reader;
        _actions = actions;
        _audio = audio;
        Title = Loc.T("History");
        var root = new Grid { Background = Brush("InkBrush") };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition());

        // Sidebar spans both rows so its surface reaches the title bar, as on macOS.
        var sidebarSurface = new Border { Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 0, 1, 0) };
        Grid.SetRowSpan(sidebarSurface, 2);
        root.Children.Add(sidebarSurface);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Glyph("history", 16));
        brand.Children.Add(Text(Loc.T("History"), 13, bold: true));
        root.Children.Add(brand);
        var sidebarScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(sidebarScroll, 1);
        root.Children.Add(sidebarScroll);

        var listHeader = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 16, 0) };
        listHeader.Children.Add(_listTitle);
        listHeader.Children.Add(_listCount);
        var listHeaderSurface = new Border { Child = listHeader, BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 0, 1, 0) };
        Grid.SetColumn(listHeaderSurface, 1);
        root.Children.Add(listHeaderSurface);
        _listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _listScroll.ViewChanged += (_, _) =>
        {
            // Load the next page when the last rows come into view.
            if (_shown < VisibleRowCount() && _listScroll.VerticalOffset >= _listScroll.ScrollableHeight - 200) { _shown += PageSize; RenderList(); }
        };
        // Search, filter and sort sit above the list they narrow down, so they keep their width at any window size.
        var find = new Grid { ColumnSpacing = 6, Margin = new Thickness(18, 12, 16, 8) };
        find.ColumnDefinitions.Add(new ColumnDefinition());
        find.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        find.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        find.Children.Add(_search);
        Grid.SetColumn(_filterButton, 1); find.Children.Add(_filterButton);
        Grid.SetColumn(_sortButton, 2); find.Children.Add(_sortButton);
        AutomationProperties.SetName(_filterButton, Loc.T("Filter history"));
        AutomationProperties.SetName(_sortButton, Loc.T("Sort history"));
        AutomationProperties.SetName(_search, Loc.T("Search history"));
        var listColumn = new Grid();
        listColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listColumn.RowDefinitions.Add(new RowDefinition());
        listColumn.Children.Add(find);
        Grid.SetRow(_listScroll, 1); listColumn.Children.Add(_listScroll);
        var listSurface = new Border { Child = listColumn, BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 1, 1, 0) };
        Grid.SetColumn(listSurface, 1); Grid.SetRow(listSurface, 1);
        root.Children.Add(listSurface);

        // Entry actions share the title bar row; the caption buttons keep their space on the right.
        _toolbarActions.Margin = new Thickness(24, 0, 150, 0);
        Grid.SetColumn(_toolbarActions, 2);
        root.Children.Add(_toolbarActions);
        var detail = new Grid();
        detail.RowDefinitions.Add(new RowDefinition());
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.Children.Add(_detailHost);
        _notice.Margin = new Thickness(28, 0, 28, 12);
        AutomationProperties.SetLiveSetting(_notice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Grid.SetRow(_notice, 1); detail.Children.Add(_notice);
        var detailSurface = new Border { Child = detail, BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetColumn(detailSurface, 2); Grid.SetRow(detailSurface, 1);
        root.Children.Add(detailSurface);
        Content = root;

        _search.TextChanged += async (_, _) => { if (await ConfirmLeaveEditAsync()) { _shown = PageSize; Refilter(); } };
        root.KeyDown += Root_KeyDown;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(brand);
        NativeWindowAppearance.ApplyAppTitleBar(this);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 560;
        }
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose || _closing) return;
            args.Cancel = true;
            if (!await ConfirmLeaveEditAsync()) return;
            _allowClose = true;
            Close();
        };
        Closed += (_, _) => { _closing = true; StopAudioPlayback(); };
        BuildMenus();
        Render();
    }

    internal void ShowOn(DisplayArea area)
    {
        var work = area.WorkArea;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(hwnd)) / 96d;
        var width = Math.Min((int)(1180 * scale), work.Width - (int)(48 * scale));
        var height = Math.Min((int)(760 * scale), work.Height - (int)(48 * scale));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(hwnd);
        _ = RefreshAsync();
    }

    // Shutdown already drained pending work; unsaved edits cannot be kept at this point.
    internal void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    internal async Task RefreshAsync()
    {
        if (_closing) return;
        try
        {
            _records = await _reader.ReadAsync();
            _loadError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("History window load failed: {0}", ex);
            _loadError = Loc.T("History could not be loaded. Your files were not changed.");
        }
        finally { _loading = false; }
        if (_closing) return;
        // Drop selections of entries that no longer exist.
        _selection.RemoveAll(id => _records.All(record => record.Id != id));
        BuildMenus();
        Render();
    }

    private void Render()
    {
        RenderSidebar();
        Refilter();
    }

    private void Refilter()
    {
        _visible = HistoryWorkspace.Query(_records, _scope, _search.Text, _range, _app, _sort, DateTimeOffset.Now, localDeviceId: LocalDeviceId());
        _listTitle.Text = ScopeTitle(_scope);
        _listCount.Text = _loading ? Loc.T("Loading…") : _visible.Count == 1 ? Loc.T("1 entry") : Loc.T("{0:N0} entries", _visible.Count);
        RenderList();
        RenderDetail();
        RenderToolbar();
    }

    private string ScopeTitle(HistoryScope scope) => scope.Mailbox switch
    {
        HistoryMailbox.Inbox => Loc.T("Inbox"),
        HistoryMailbox.WithAudio => Loc.T("With Audio"),
        HistoryMailbox.Failed => Loc.T("Failed"),
        HistoryMailbox.All => Loc.T("All History"),
        _ => scope.Device is { } device ? DeviceName(device, _records.FirstOrDefault(record => record.OriginDeviceId == device)?.OriginPlatform)
            : scope.Source is null ? Loc.T("This PC") : SourceName(scope.Source)
    };

    internal void RenderDevices()
    {
        if (_closing) return;
        RenderSidebar();
        RenderList();
    }

    // Device names as on macOS: the name the device reported, otherwise its platform.
    private string DeviceName(string deviceId, string? platform)
    {
        if (Devices().TryGetValue(deviceId, out var device) && device.Name is { } name) return name;
        return (platform ?? device?.Platform ?? "").ToLowerInvariant() switch
        {
            var value when value.Contains("mac") => "Mac",
            var value when value.Contains("ipad") => "iPad",
            var value when value.Contains("ios") || value.Contains("iphone") || value.Contains("watch") => "iPhone",
            var value when value.Contains("windows") => Loc.T("Windows PC"),
            _ => Loc.T("Device")
        };
    }

    private string OriginName(TranscriptionRecord record) =>
        HistoryWorkspace.IsLocal(record, LocalDeviceId()) ? Loc.T("This PC") : DeviceName(record.OriginDeviceId!, record.OriginPlatform);

    private static string DeviceIcon(string? platform) => (platform ?? "").ToLowerInvariant() switch
    {
        var value when value.Contains("ios") || value.Contains("iphone") || value.Contains("watch") => "phone",
        var value when value.Contains("mac") || value.Contains("windows") => "laptop",
        _ => "devices"
    };

    private static string SourceName(string source) => source switch
    {
        "dictation" => Loc.T("Windows Dictation"),
        "recording" => Loc.T("Recorder"),
        "file" => Loc.T("File Transcription"),
        _ => Loc.T("Other")
    };

    // Sidebar -----------------------------------------------------------------------------

    private void RenderSidebar()
    {
        _sidebar.Children.Clear();
        _sidebar.Children.Add(SectionHeader(Loc.T("Smart Mailboxes")));
        foreach (var (mailbox, icon, label) in new[]
        {
            (HistoryMailbox.Inbox, "mail", Loc.T("Inbox")), (HistoryMailbox.All, "history", Loc.T("All History")),
            (HistoryMailbox.WithAudio, "wave-history", Loc.T("With Audio")), (HistoryMailbox.Failed, "info", Loc.T("Failed"))
        })
        {
            var count = _records.Count(record => HistoryWorkspace.InMailbox(record, mailbox));
            _sidebar.Children.Add(SidebarRow(icon, label, count, _scope.Mailbox == mailbox, 0, () => SelectScope(new HistoryScope(mailbox))));
        }
        _sidebar.Children.Add(SectionHeader(Loc.T("Devices")));
        var localId = LocalDeviceId();
        var local = _records.Where(record => HistoryWorkspace.IsLocal(record, localId)).ToArray();
        _sidebar.Children.Add(SidebarRow("laptop", Loc.T("This PC"), local.Length, _scope is { Mailbox: null, Source: null, Device: null }, 0,
            () => SelectScope(new HistoryScope(null))));
        foreach (var (source, icon) in new[] { ("dictation", "microphone"), ("recording", "recorder"), ("file", "file"), ("other", "history") })
        {
            var count = local.Count(record => HistoryWorkspace.SourceOf(record) == source);
            if (count == 0) continue;
            _sidebar.Children.Add(SidebarRow(icon, SourceName(source), count, _scope is { Mailbox: null, Device: null } && _scope.Source == source, 18,
                () => SelectScope(new HistoryScope(null, source))));
        }
        // Devices known from the sync folder, even before any of their entries arrived.
        var remote = _records.Where(record => !HistoryWorkspace.IsLocal(record, localId)).GroupBy(record => record.OriginDeviceId!)
            .ToDictionary(group => group.Key, group => (Count: group.Count(), Platform: group.First().OriginPlatform));
        foreach (var (id, device) in Devices())
            if (id != localId && !id.StartsWith("transport:", StringComparison.Ordinal)) remote.TryAdd(id, (0, device.Platform));
        foreach (var (id, device) in remote.OrderBy(pair => DeviceName(pair.Key, pair.Value.Platform), StringComparer.CurrentCultureIgnoreCase))
            _sidebar.Children.Add(SidebarRow(DeviceIcon(device.Platform), DeviceName(id, device.Platform), device.Count, _scope.Device == id, 0,
                () => SelectScope(new HistoryScope(null, Device: id))));
    }

    private static TextBlock SectionHeader(string text)
    {
        var header = Text(text, 11, muted: true, bold: true);
        header.Margin = new Thickness(10, 14, 0, 6);
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    private static HandCursorButton SidebarRow(string icon, string label, int count, bool selected, double indent, Action click)
    {
        var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(indent, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = Glyph(icon, 18); glyph.Inverse = selected;
        row.Children.Add(glyph);
        var name = Text(label, 14, bold: selected);
        name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        if (selected) name.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        Grid.SetColumn(name, 1); row.Children.Add(name);
        // Counts are shown only when there is something to count, as on macOS.
        if (count > 0)
        {
            var number = Text(count.ToString("N0"), 12, muted: !selected);
            number.VerticalAlignment = VerticalAlignment.Center;
            if (selected) number.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            Grid.SetColumn(number, 2); row.Children.Add(number);
        }
        var button = new HandCursorButton { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36, Padding = new Thickness(10, 7, 10, 7),
            Style = (Style)Application.Current.Resources[selected ? "SidebarSelectedButtonStyle" : "MenuButtonStyle"] };
        AutomationProperties.SetName(button, count > 0 ? Loc.T("{0}, {1} entries", label, count) : label);
        AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
        button.Click += (_, _) => click();
        return button;
    }

    private async void SelectScope(HistoryScope scope)
    {
        if (scope == _scope || !await ConfirmLeaveEditAsync()) return;
        _scope = scope;
        _shown = PageSize;
        _selection.Clear();
        _anchor = null;
        RenderSidebar();
        Refilter();
    }

    // List --------------------------------------------------------------------------------

    private int VisibleRowCount() => _visible.Count(record => !_collapsed.Contains(HistoryWorkspace.GroupOf(record.Timestamp, DateTimeOffset.Now)));

    private void RenderList()
    {
        _list.Children.Clear();
        if (_loadError is not null) { _list.Children.Add(EmptyState("info", Loc.T("History unavailable"), _loadError)); return; }
        if (_loading) { _list.Children.Add(EmptyState("history", Loc.T("Loading history…"), "")); return; }
        if (_visible.Count == 0) { _list.Children.Add(EmptyList()); return; }
        var now = DateTimeOffset.Now;
        var rendered = 0;
        // Group sections only make sense for date ordering.
        var grouped = _sort is HistorySort.NewestFirst or HistorySort.OldestFirst;
        foreach (var section in _visible.GroupBy(record => grouped ? HistoryWorkspace.GroupOf(record.Timestamp, now) : HistoryDateGroup.Today))
        {
            if (rendered >= _shown) break;
            var collapsed = grouped && _collapsed.Contains(section.Key);
            if (grouped) _list.Children.Add(GroupHeader(section.Key, section.Count(), collapsed));
            if (collapsed) continue;
            var first = true;
            foreach (var record in section)
            {
                if (rendered++ >= _shown) break;
                if (!first) _list.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new Thickness(12, 0, 12, 0) });
                first = false;
                _list.Children.Add(ListRow(record, now));
            }
        }
    }

    private UIElement EmptyList()
    {
        if (_search.Text.Trim().Length > 0 || _range != HistoryDateRange.AllTime || _app is not null)
        {
            var empty = EmptyState("search", Loc.T("No Results"), Loc.T("No entries match your search or filters."));
            var clear = new HandCursorButton { Content = Loc.T("Clear Filters"), HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
            clear.Click += (_, _) => ClearFilters();
            ((StackPanel)empty).Children.Add(clear);
            return empty;
        }
        return _scope.Mailbox switch
        {
            HistoryMailbox.Inbox => EmptyState("mail", Loc.T("Inbox is Empty"), Loc.T("Entries from your other devices appear here until you mark them complete.")),
            HistoryMailbox.Failed => EmptyState("check", Loc.T("No Failed Entries"), Loc.T("Entries whose processing failed appear here.")),
            HistoryMailbox.WithAudio => EmptyState("wave-history", Loc.T("No Audio Saved"), Loc.T("Turn on Keep dictation audio in History & Sync to listen to new dictations again.")),
            _ => EmptyState("history", Loc.T("No History Yet"), Loc.T("Your dictations appear here once they are saved."))
        };
    }

    private UIElement GroupHeader(HistoryDateGroup group, int count, bool collapsed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Text(collapsed ? "›" : "⌄", 12, muted: true));
        row.Children.Add(Text(group switch
        {
            HistoryDateGroup.Today => Loc.T("Today"), HistoryDateGroup.Yesterday => Loc.T("Yesterday"), HistoryDateGroup.ThisWeek => Loc.T("This Week"),
            HistoryDateGroup.ThisMonth => Loc.T("This Month"), _ => Loc.T("Older")
        }, 13, bold: true, muted: true));
        row.Children.Add(Text(count.ToString("N0"), 13, muted: true));
        var button = new HandCursorButton { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 4), Padding = new Thickness(10, 4, 10, 4), Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
        AutomationProperties.SetName(button, collapsed ? Loc.T("Expand {0} section, {1} entries", group, count) : Loc.T("Collapse {0} section, {1} entries", group, count));
        button.Click += (_, _) =>
        {
            if (!_collapsed.Remove(group)) _collapsed.Add(group);
            RenderList();
        };
        return button;
    }

    private HandCursorButton ListRow(TranscriptionRecord record, DateTimeOffset now)
    {
        var selected = _selection.Contains(record.Id);
        var foreground = selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : Brush("TextBrush");
        var secondary = selected ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)) : Brush("MutedBrush");
        var content = new Grid { ColumnSpacing = 8, RowSpacing = 3 };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 3; i++) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Text(FirstLine(record.DisplayText), 14, bold: true);
        title.Foreground = foreground; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(title);
        var origin = Text(string.Join(" · ", new[] { HistoryWorkspace.AppOf(record), OriginName(record) }.OfType<string>()), 12);
        origin.Foreground = secondary; origin.TextTrimming = TextTrimming.CharacterEllipsis; origin.TextWrapping = TextWrapping.NoWrap;
        Grid.SetRow(origin, 1); content.Children.Add(origin);
        var when = Text(Elapsed(record.Timestamp, now) + " · " + Duration(record.DurationSeconds), 12);
        when.Foreground = secondary;
        Grid.SetRow(when, 2); content.Children.Add(when);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (record.InboxState == HistoryWorkspace.InboxOpen) status.Children.Add(Tinted(Glyph("mail", 14), selected, Loc.T("Open in Inbox")));
        if (record.Status != TranscriptionRecordStatus.Succeeded) status.Children.Add(Tinted(Glyph("info", 14), selected, Loc.T("Processing failed")));
        if (!string.IsNullOrWhiteSpace(record.AudioFileName)) status.Children.Add(Tinted(Glyph("wave-history", 14), selected, Loc.T("Audio saved")));
        Grid.SetColumn(status, 1); Grid.SetRow(status, 2); content.Children.Add(status);
        var button = new HandCursorButton
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 1, 0, 1),
            Style = (Style)Application.Current.Resources[selected ? "SidebarSelectedButtonStyle" : "MenuButtonStyle"]
        };
        AutomationProperties.SetName(button, $"{title.Text}, {origin.Text}, {when.Text}");
        AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
        button.Click += (_, _) => SelectRecord(record.Id);
        button.ContextFlyout = RowMenu(record);
        button.ContextRequested += (_, _) => { if (!_selection.Contains(record.Id)) SelectRecord(record.Id); };
        return button;
    }

    private static UIElement Tinted(TypeWhisperGlyph glyph, bool inverse, string description)
    {
        glyph.Inverse = inverse;
        ToolTipService.SetToolTip(glyph, description);
        AutomationProperties.SetName(glyph, description);
        return glyph;
    }

    private async void SelectRecord(string id)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (!ctrl && !shift && _selection is [var only] && only == id) return;
        if (!await ConfirmLeaveEditAsync()) return;
        if (shift && _anchor is { } anchor && _visible.Any(record => record.Id == anchor))
        {
            var ids = _visible.Select(record => record.Id).ToList();
            int from = ids.IndexOf(anchor), to = ids.IndexOf(id);
            _selection.Clear();
            _selection.AddRange(ids.GetRange(Math.Min(from, to), Math.Abs(to - from) + 1));
        }
        else if (ctrl)
        {
            if (!_selection.Remove(id)) _selection.Add(id);
            _anchor = id;
        }
        else
        {
            _selection.Clear();
            _selection.Add(id);
            _anchor = id;
        }
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        StopAudioPlayback();
        _detailMode = "final";
        _notice.Text = "";
        RenderList();
        RenderDetail();
        RenderToolbar();
    }

    private async void MoveSelection(int offset)
    {
        var rows = _visible.Where(record => !_collapsed.Contains(HistoryWorkspace.GroupOf(record.Timestamp, DateTimeOffset.Now))).ToList();
        if (rows.Count == 0 || !await ConfirmLeaveEditAsync()) return;
        var index = _selection.Count == 0 ? (offset > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(rows.FindIndex(record => record.Id == _selection[^1]) + offset, 0, rows.Count - 1);
        _selection.Clear();
        _selection.Add(rows[index].Id);
        _anchor = rows[index].Id;
        if (index >= _shown) _shown = index + PageSize;
        OnSelectionChanged();
    }

    private async void ClearFilters()
    {
        if (!await ConfirmLeaveEditAsync()) return;
        _range = HistoryDateRange.AllTime;
        _app = null;
        BuildMenus();
        _search.Text = "";
        Refilter();
    }

    // Toolbar menus ------------------------------------------------------------------------

    private void BuildMenus()
    {
        var filter = new MenuFlyout();
        var date = new MenuFlyoutSubItem { Text = Loc.T("Date") };
        foreach (var (range, label) in new[] { (HistoryDateRange.Last7Days, Loc.T("Last 7 Days")), (HistoryDateRange.Last30Days, Loc.T("Last 30 Days")),
                     (HistoryDateRange.Last90Days, Loc.T("Last 90 Days")), (HistoryDateRange.AllTime, Loc.T("All Time")) })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "date", IsChecked = _range == range };
            item.Click += async (_, _) => { if (await ConfirmLeaveEditAsync()) { _range = range; _shown = PageSize; BuildMenus(); Refilter(); } };
            date.Items.Add(item);
        }
        filter.Items.Add(date);
        var apps = new MenuFlyoutSubItem { Text = Loc.T("App") };
        var allApps = new RadioMenuFlyoutItem { Text = Loc.T("All Apps"), GroupName = "app", IsChecked = _app is null };
        allApps.Click += async (_, _) => { if (await ConfirmLeaveEditAsync()) { _app = null; BuildMenus(); Refilter(); } };
        apps.Items.Add(allApps);
        foreach (var app in HistoryWorkspace.Apps(_records))
        {
            var item = new RadioMenuFlyoutItem { Text = app, GroupName = "app", IsChecked = string.Equals(_app, app, StringComparison.OrdinalIgnoreCase) };
            item.Click += async (_, _) => { if (await ConfirmLeaveEditAsync()) { _app = app; _shown = PageSize; BuildMenus(); Refilter(); } };
            apps.Items.Add(item);
        }
        filter.Items.Add(apps);
        if (_range != HistoryDateRange.AllTime || _app is not null || _search.Text.Length > 0)
        {
            filter.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem { Text = Loc.T("Clear Filters") };
            clear.Click += (_, _) => ClearFilters();
            filter.Items.Add(clear);
        }
        _filterButton.Flyout = filter;
        _filterButton.Content = Text(_range != HistoryDateRange.AllTime || _app is not null ? Loc.T("Filter") + " •" : Loc.T("Filter"), 12);

        var sort = new MenuFlyout();
        foreach (var (order, label) in new[] { (HistorySort.NewestFirst, Loc.T("Newest First")), (HistorySort.OldestFirst, Loc.T("Oldest First")),
                     (HistorySort.Duration, Loc.T("Duration")), (HistorySort.AppName, Loc.T("App Name")) })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "sort", IsChecked = _sort == order };
            item.Click += async (_, _) => { if (await ConfirmLeaveEditAsync()) { _sort = order; BuildMenus(); Refilter(); } };
            sort.Items.Add(item);
        }
        _sortButton.Flyout = sort;
    }

    // Keyboard -----------------------------------------------------------------------------

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen) return;
        var inText = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;
        var ctrl = IsDown(VirtualKey.Control);
        if (ctrl && e.Key == VirtualKey.S) { _ = SaveAsync(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.F) { _search.Focus(FocusState.Keyboard); _search.SelectAll(); e.Handled = true; }
        else if (inText) return;
        else if (ctrl && e.Key == VirtualKey.C) { CopySelection(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.A) { _selection.Clear(); _selection.AddRange(_visible.Select(record => record.Id)); OnSelectionChanged(); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete && _selection.Count > 0) { _ = DeleteSelectionAsync(); e.Handled = true; }
        else if (e.Key is VirtualKey.Down or VirtualKey.Up) { MoveSelection(e.Key == VirtualKey.Down ? 1 : -1); e.Handled = true; }
    }

    // Helpers ------------------------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length == 0 ? Loc.T("Untitled transcript") : line;
    }

    // Short elapsed time as in the macOS list; older entries show their date.
    private static string Elapsed(DateTime timestampUtc, DateTimeOffset now)
    {
        var local = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc).ToLocalTime();
        var elapsed = now.LocalDateTime - local;
        if (elapsed < TimeSpan.FromMinutes(1)) return Loc.T("now");
        if (elapsed < TimeSpan.FromHours(1)) return Loc.T("{0}m", (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromHours(24)) return Loc.T("{0}h {1}m", (int)elapsed.TotalHours, elapsed.Minutes);
        return local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy");
    }

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private static StackPanel EmptyState(string icon, string title, string description)
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 80, 24, 24), MaxWidth = 360 };
        var glyph = Glyph(icon, 36); glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.Opacity = 0.8;
        panel.Children.Add(glyph);
        var heading = Text(title, 17, bold: true); heading.TextAlignment = TextAlignment.Center;
        panel.Children.Add(heading);
        if (description.Length > 0)
        {
            var body = Text(description, 13, muted: true); body.TextAlignment = TextAlignment.Center;
            panel.Children.Add(body);
        }
        return panel;
    }

    private static TypeWhisperGlyph Glyph(string kind, double size) => new() { Kind = kind, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush"),
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
}
