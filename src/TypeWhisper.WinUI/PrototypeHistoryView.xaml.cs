using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeHistoryView : UserControl
{
    private PrototypeHistoryStore _store = new([]);
    private readonly Dictionary<Guid, string> _timeLabels = [];
    private TypeWhisper.Presentation.HistoryReader? _reader;
    private bool _loading;
    private string? _loadError;

    internal void Connect(TypeWhisper.Presentation.HistoryReader reader) => _reader = reader;

    internal async Task RefreshAsync()
    {
        if (_reader is null || _loading) return;
        _loading = true;
        _loadError = null;
        ApplyFilters();
        try
        {
            var records = await _reader.ReadAsync();
            _store = new PrototypeHistoryStore(records.Select(HistoryEntryAdapter.FromRecord));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loadError = "History could not be loaded. Your files were not changed.";
            System.Diagnostics.Debug.WriteLine(ex);
            _store = new PrototypeHistoryStore([]);
        }
        finally { _loading = false; ApplyFilters(); }
    }
    private string _query = string.Empty;
    private PrototypeHistoryEntryKind? _kind;
    private string? _deviceId;
    private bool _deviceMenuKeyboard;
    private HandCursorButton? _selectedDeviceButton;

    internal ObservableCollection<PrototypeTranscript> FilteredEntries { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event EventHandler? ClearSearchRequested;
    internal bool IsReading => ReadingPage.Visibility == Visibility.Visible;
    private PrototypeTranscript? _opened;

    public PrototypeHistoryView()
    {
        InitializeComponent();
        Filter(string.Empty);
    }

    internal void Filter(string query)
    {
        // A queued TextChanged from opening the scope must not close a detail
        // that was explicitly opened by the recorder's deep link afterwards.
        if (IsReading && query == _query) return;
        _query = query;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var selectedId = (Entries.SelectedItem as PrototypeTranscript)?.Entry.RecordId;
        ShowList();
        FilteredEntries.Clear();
        foreach (var entry in _store.Query(_query, _kind, _deviceId))
            FilteredEntries.Add(new PrototypeTranscript(entry, _timeLabels.GetValueOrDefault(entry.RecordId)
                ?? FormatTime(entry.Content.CreatedAt)));
        EmptyState.Visibility = FilteredEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loading || _loadError is not null) EmptyState.Visibility = Visibility.Visible;
        Entries.Visibility = _loading || _loadError is not null ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _loading ? "Loading history…" : _loadError is not null ? "History unavailable"
            : _store.Query().Count == 0 ? "No history yet" : "No matching entries";
        EmptyDescription.Text = _loading ? "Reading local history." : _loadError
            ?? (_store.Query().Count == 0 ? "New transcriptions will appear here. No previous history was imported." : "Try another search or reset the filters.");
        EmptyAction.Content = _loadError is not null ? "Retry" : "Reset filters";
        EmptyAction.Visibility = !_loading && (_loadError is not null || _store.Query().Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        Entries.SelectedItem = FilteredEntries.FirstOrDefault(item => item.Entry.RecordId == selectedId)
            ?? FilteredEntries.FirstOrDefault();
        AllFilter.Style = FilterStyle(_kind is null);
        DictationFilter.Style = FilterStyle(_kind == PrototypeHistoryEntryKind.Dictation);
        RecordingFilter.Style = FilterStyle(_kind == PrototypeHistoryEntryKind.Recording);
        var device = _store.Devices.FirstOrDefault(device => device.DeviceId == _deviceId);
        DeviceFilterLabel.Text = device?.DeviceName ?? "All devices";
        DeviceFilterIcon.Kind = DeviceIcon(device?.Platform);
        UpdateResultSummary();
    }

    private static string FormatTime(DateTimeOffset createdAt)
    {
        var local = createdAt.ToLocalTime();
        var day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : local.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
        return $"{day} · {local:HH:mm}";
    }

    private static Style FilterStyle(bool selected) => (Style)Application.Current.Resources[
        selected ? "PrototypePrimaryButtonStyle" : "PrototypeIconButtonStyle"];

    internal void UpsertRecorderEntry(PrototypeHistoryEntry entry)
    {
        _store.Upsert(entry);
        ApplyFilters();
    }

    internal void OpenEntry(Guid recordId)
    {
        _query = string.Empty;
        _kind = null;
        _deviceId = null;
        ApplyFilters();
        Entries.SelectedItem = FilteredEntries.FirstOrDefault(item => item.Entry.RecordId == recordId);
        OpenSelected();
    }

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        _kind = Enum.TryParse<PrototypeHistoryEntryKind>((string)((Button)sender).Tag, out var kind) ? kind : null;
        ApplyFilters();
    }

    private void Devices_Opening(object sender, object e)
    {
        _deviceMenuKeyboard = DeviceFilter.FocusState == FocusState.Keyboard;
        _selectedDeviceButton = null;
        DeviceChoices.Children.Clear();
        AddDeviceChoice(null, "All devices", "History from every device", "devices");
        DeviceChoices.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 5, 12, 5),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["HairlineBrush"] });
        foreach (var device in _store.Devices)
            AddDeviceChoice(device.DeviceId, device.DeviceName ?? device.Platform,
                device.DeviceId == "unknown" ? "Device identity was not stored with these entries" : device.Platform, DeviceIcon(device.Platform));
    }

    private void Devices_Opened(object sender, object e) =>
        _selectedDeviceButton?.Focus(_deviceMenuKeyboard ? FocusState.Keyboard : FocusState.Programmatic);

    private static string DeviceIcon(string? platform) => platform switch
    {
        "Windows" => "desktop", "macOS" => "laptop", "iOS" => "phone", _ => "devices"
    };

    private void AddDeviceChoice(string? id, string label, string description, string icon)
    {
        var selected = _deviceId == id;
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 22, Height = 22, Opacity = selected ? 1 : 0.8 });
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock { Text = description, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, 1);
        content.Children.Add(labels);
        var check = new TypeWhisperGlyph { Kind = "check", Width = 16, Height = 16, Opacity = selected ? 1 : 0 };
        Grid.SetColumn(check, 2);
        content.Children.Add(check);
        var button = new HandCursorButton { Content = content, MinHeight = 56, Padding = new Thickness(12, 9, 12, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["PrototypeMenuButtonStyle"] };
        if (selected) button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 19, 40, 58));
        if (selected) _selectedDeviceButton = button;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"History device {id ?? "all"}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, $"{label}. {description}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        button.Click += (_, _) => { _deviceId = id; DeviceFilter.Flyout.Hide(); ApplyFilters(); };
        DeviceChoices.Children.Add(button);
    }

    private void Devices_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            DeviceFilter.Flyout.Hide();
            DeviceFilter.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
        else if (e.Key is Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up)
        {
            var buttons = DeviceChoices.Children.OfType<HandCursorButton>().ToList();
            var current = buttons.FindIndex(button => ReferenceEquals(button, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)));
            var next = current < 0 ? 0 : Math.Clamp(current + (e.Key == Windows.System.VirtualKey.Down ? 1 : -1), 0, buttons.Count - 1);
            if (buttons.Count > 0) buttons[next].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    internal void MoveSelection(int offset)
    {
        if (IsReading || FilteredEntries.Count == 0) return;
        Entries.SelectedIndex = Math.Clamp(Entries.SelectedIndex + offset, 0, FilteredEntries.Count - 1);
        Entries.ScrollIntoView(Entries.SelectedItem);
    }

    internal void OpenSelected()
    {
        if (IsReading || Entries.SelectedItem is not PrototypeTranscript entry) return;
        _opened = entry;
        TranscriptTitle.Text = entry.Title;
        TranscriptMetadata.Text = $"{entry.Time} · {entry.Metadata}";
        AudioAvailabilityText.Text = entry.AudioDescription;
        TranscriptBody.Text = entry.Entry.HasTranscript ? entry.Text
            : "This demo session has been added to History. No audio was captured and no transcript was generated.";
        ListPage.Visibility = Visibility.Collapsed;
        ReadingPage.Visibility = Visibility.Visible;
        CopyButton.Visibility = entry.Entry.HasTranscript ? Visibility.Visible : Visibility.Collapsed;
        HistoryBreadcrumbs.SetItems(new("Quick Launch", OpenLauncher, "History breadcrumb Quick Launch"),
            new("History", GoBack, "Back from history"), new(entry.Entry.Content.Kind == PrototypeHistoryEntryKind.Recording ? "Recording" : "Transcript"));
        PageTitle.Text = entry.Entry.Content.Kind == PrototypeHistoryEntryKind.Recording ? "Recording" : "Transcript";
        ResultSummary.Text = "Local entry · sync not connected";
        HistoryNavigationHint.Text = entry.Entry.HasTranscript ? "⌫ / Esc Back   ·   Select text to copy a passage" : "⌫ / Esc Back";
        TranscriptScroll.ChangeView(null, 0, null, true);
    }

    internal void GoBack()
    {
        if (IsReading)
        {
            ShowList();
            Entries.Focus(FocusState.Programmatic);
        }
        else ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowList()
    {
        ReadingPage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        HistoryBreadcrumbs.SetItems(new("Quick Launch", OpenLauncher, "Back from history"), new("History"));
        PageTitle.Text = "History";
        HistoryNavigationHint.Text = "⌫ / Esc Back   ↑↓ Navigate   Enter Open";
        UpdateResultSummary();
    }

    private void OpenLauncher() { ShowList(); LauncherRequested?.Invoke(this, EventArgs.Empty); }

    private void UpdateResultSummary() => ResultSummary.Text =
        _loading ? "Loading…" : _loadError is not null ? "Read failed"
            : $"{FilteredEntries.Count} {(FilteredEntries.Count == 1 ? "entry" : "entries")} · local history";

    private void Entry_Click(object sender, ItemClickEventArgs e)
    {
        Entries.SelectedItem = e.ClickedItem;
        OpenSelected();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_loadError is not null) { await RefreshAsync(); return; }
        _kind = null;
        _deviceId = null;
        Filter(string.Empty);
        ClearSearchRequested?.Invoke(this, EventArgs.Empty);
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_opened is null) return;
        try
        {
            var content = new DataPackage();
            content.SetText(_opened.Text);
            Clipboard.SetContent(content);
            ResultSummary.Text = "Transcript copied";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ResultSummary.Text = "Clipboard unavailable. Select the text and try copying again.";
        }
    }
}
