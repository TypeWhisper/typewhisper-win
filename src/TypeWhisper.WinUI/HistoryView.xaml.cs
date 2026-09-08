using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

public sealed partial class HistoryView : UserControl
{
    private HistoryStore _store = new([]);
    private readonly Dictionary<Guid, string> _timeLabels = [];
    private TypeWhisper.Presentation.HistoryReader? _reader;
    private TypeWhisper.Presentation.HistoryActions? _actions;
    private bool _acting;
    private bool _closing;
    private readonly HashSet<ContentDialog> _dialogs = [];
    private readonly HashSet<Task> _writes = [];
    private Task? _shutdown;

    internal Task ShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        StopAudioPlayback();
        StopReadback();
        _closing = true;
        IsEnabled = false;
        foreach (var dialog in _dialogs.ToArray()) dialog.Hide();
        return _shutdown = Task.WhenAll(_writes.ToArray());
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_closing) return ContentDialogResult.None;
        _dialogs.Add(dialog);
        try { return await dialog.ShowAsync(); }
        finally { _dialogs.Remove(dialog); }
    }

    private async Task<T> TrackWriteAsync<T>(Task<T> write)
    {
        _writes.Add(write);
        try { return await write; }
        finally { _writes.Remove(write); }
    }

    private async Task TrackWriteAsync(Task write)
    {
        _writes.Add(write);
        try { await write; }
        finally { _writes.Remove(write); }
    }
    private bool _selecting;
    private int _bulkFocusIndex = -1;
    private bool _applyingFilters;
    private string[] SelectedIds => Entries.SelectedItems.OfType<Transcript>()
        .Select(item => item.Entry.PersistedRecordId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
    private bool _loading;
    private string? _loadError;

    internal void Connect(TypeWhisper.Presentation.HistoryReader reader, TypeWhisper.Presentation.HistoryActions? actions = null,
        TypeWhisper.Core.Interfaces.IHistoryAudioService? audio = null)
    {
        _reader = reader;
        _actions = actions;
        _historyAudio = audio;
    }

    internal async Task RefreshAsync()
    {
        if (_closing || _reader is null || _loading || _acting || ReadbackActive || AudioPlayerActive) return;
        var openedId = IsReading ? _opened?.Entry.RecordId : null;
        _loading = true;
        _loadError = null;
        ApplyFilters();
        try
        {
            var records = await _reader.ReadAsync();
            if (_closing) return;
            _store = new HistoryStore(records.Select(HistoryEntryAdapter.FromRecord));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loadError = "History could not be loaded. Your files were not changed.";
            System.Diagnostics.Debug.WriteLine(ex);
            _store = new HistoryStore([]);
        }
        finally
        {
            _loading = false;
            if (!_closing) UpdateAudioCleanupNotice();
            if (!_closing) ApplyFilters();
            if (!_closing && openedId is { } id && FilteredEntries.FirstOrDefault(item => item.Entry.RecordId == id) is { } opened)
            {
                Entries.SelectedItem = opened;
                OpenSelected();
            }
        }
    }
    private string _query = string.Empty;
    private HistoryEntryKind? _kind;
    private string? _deviceId;
    private bool _deviceMenuKeyboard;
    private HandCursorButton? _selectedDeviceButton;

    internal ObservableCollection<Transcript> FilteredEntries { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event EventHandler? ClearSearchRequested;
    internal bool IsReading => ReadingPage.Visibility == Visibility.Visible;
    internal bool IsSelecting => _selecting;
    private Transcript? _opened;

    public HistoryView()
    {
        InitializeComponent();
        HistoryTabs.SetItems([new("all", "All"), new("Dictation", "Dictations"), new("Recording", "Recordings")], "all");
        HistoryTabs.SelectionChanged += id =>
        { _kind = Enum.TryParse<HistoryEntryKind>(id, out var kind) ? kind : null; ApplyFilters(); };
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
        var selectedIds = _selecting ? SelectedIds.ToHashSet(StringComparer.Ordinal) : [];
        _applyingFilters = true;
        var selectedId = (Entries.SelectedItem as Transcript)?.Entry.RecordId;
        ShowList();
        FilteredEntries.Clear();
        foreach (var entry in _store.Query(_query, _kind, _deviceId))
            FilteredEntries.Add(new Transcript(entry, _timeLabels.GetValueOrDefault(entry.RecordId)
                ?? FormatTime(entry.Content.CreatedAt)));
        EmptyState.Visibility = FilteredEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loading || _loadError is not null) EmptyState.Visibility = Visibility.Visible;
        Entries.Visibility = _loading || _loadError is not null ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _loading ? "Loading history…" : _loadError is not null ? "History unavailable"
            : _store.Query().Count == 0 ? "No history yet" : "No matching entries";
        EmptyDescription.Text = _loading ? "Reading local history." : _loadError
            ?? (_store.Query().Count == 0 ? "New transcriptions will appear here. No previous history was imported." : "Try another search or reset the filters.");
        EmptyAction.Content = _loadError is not null ? "Retry" : "Reset filters";
        EmptyAction.Visibility = !_loading && FilteredEntries.Count == 0 && (_loadError is not null || _store.Query().Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        if (_selecting)
        {
            foreach (var item in FilteredEntries.Where(item => item.Entry.PersistedRecordId is { } id && selectedIds.Contains(id)))
                Entries.SelectedItems.Add(item);
        }
        else Entries.SelectedItem = FilteredEntries.FirstOrDefault(item => item.Entry.RecordId == selectedId)
                ?? FilteredEntries.FirstOrDefault();
        HistoryTabs.SetSelected(_kind?.ToString() ?? "all");
        var device = _store.Devices.FirstOrDefault(device => device.DeviceId == _deviceId);
        DeviceFilterLabel.Text = device?.DeviceName ?? "All devices";
        DeviceFilterIcon.Kind = DeviceIcon(device?.Platform);
        _applyingFilters = false;
        UpdateResultSummary();
        UpdateBulkActions();
    }

    private static string FormatTime(DateTimeOffset createdAt)
    {
        var local = createdAt.ToLocalTime();
        var day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : local.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
        return $"{day} · {local:HH:mm}";
    }

    private static Style FilterStyle(bool selected) => (Style)Application.Current.Resources[
        selected ? "PrimaryButtonStyle" : "IconButtonStyle"];

    internal void UpsertRecorderEntry(HistoryEntry entry)
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
            HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
        if (selected) button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 19, 40, 58));
        if (selected) _selectedDeviceButton = button;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"History device {id ?? "all"}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, $"{label}. {description}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        button.Click += (_, _) => { _deviceId = id; DeviceFilter.Flyout.Hide(); ApplyFilters(); };
        DeviceChoices.Children.Add(button);
    }

    private void Devices_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            DeviceFilter.Flyout.Hide();
            DeviceFilter.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
        else if (e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            var buttons = DeviceChoices.Children.OfType<HandCursorButton>().ToList();
            var current = buttons.FindIndex(button => ReferenceEquals(button, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)));
            var next = current < 0 ? 0 : Math.Clamp(current + (e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1), 0, buttons.Count - 1);
            if (buttons.Count > 0) buttons[next].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    internal void MoveSelection(int offset)
    {
        if (IsReading || FilteredEntries.Count == 0) return;
        if (_selecting)
        {
            _bulkFocusIndex = Math.Clamp(_bulkFocusIndex + offset, 0, FilteredEntries.Count - 1);
            var item = FilteredEntries[_bulkFocusIndex];
            Entries.ScrollIntoView(item);
            Entries.UpdateLayout();
            (Entries.ContainerFromItem(item) as Control)?.Focus(FocusState.Keyboard);
            return;
        }
        Entries.SelectedIndex = Math.Clamp(Entries.SelectedIndex + offset, 0, FilteredEntries.Count - 1);
        Entries.ScrollIntoView(Entries.SelectedItem);
    }

    internal void OpenSelected()
    {
        if (_selecting)
        {
            if (_bulkFocusIndex >= 0 && _bulkFocusIndex < FilteredEntries.Count)
            {
                var item = FilteredEntries[_bulkFocusIndex];
                if (Entries.SelectedItems.Contains(item)) Entries.SelectedItems.Remove(item);
                else if (item.Entry.PersistedRecordId is not null) Entries.SelectedItems.Add(item);
            }
            return;
        }
        if (IsReading || Entries.SelectedItem is not Transcript entry) return;
        StopAudioPlayback();
        StopReadback();
        _opened = entry;
        TranscriptTitle.Text = entry.Title;
        TranscriptMetadata.Text = $"{entry.Time} · {entry.Metadata}";
        TranscriptModel.Text = entry.ModelMetadata;
        var details = entry.Entry.Content;
        TranscriptProvenance.Text = $"App: {details.AppName ?? details.AppProcessName ?? "Not recorded"} · Task: {details.TranscriptionTaskUsed ?? "Not recorded"}";
        if (!string.IsNullOrWhiteSpace(details.WorkflowName) || !string.IsNullOrWhiteSpace(details.WorkflowId))
            TranscriptProvenance.Text += "\nWorkflow: " + (string.IsNullOrWhiteSpace(details.WorkflowName) ? details.WorkflowId : details.WorkflowName);
        if (details.ProcessingState == HistoryProcessingState.Failed)
            TranscriptProvenance.Text += "\nProcessing failed: " + (details.FailureMessage ?? "The result needs review.");
        EntryActions.Visibility = _actions is not null && entry.Entry.PersistedRecordId is not null ? Visibility.Visible : Visibility.Collapsed;
        ActionNotice.Text = "";
        RefreshAudioAvailability();
        TranscriptBody.Text = entry.Entry.HasTranscript ? entry.Text
            : "This demo session has been added to History. No audio was captured and no transcript was generated.";
        ListPage.Visibility = HistoryFilters.Visibility = Visibility.Collapsed;
        ReadingPage.Visibility = Visibility.Visible;
        ListActions.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = Visibility.Visible;
        CopyButton.Visibility = entry.Entry.HasTranscript ? Visibility.Visible : Visibility.Collapsed;
        UpdateReadbackButton();
        HistoryBreadcrumbs.SetItems(new("Quick Launch", OpenLauncher, "History breadcrumb Quick Launch"),
            new("History", GoBack, "Back from history"), new(entry.Entry.Content.Kind == HistoryEntryKind.Recording ? "Recording" : "Transcript"));
        PageTitle.Text = entry.Entry.Content.Kind == HistoryEntryKind.Recording ? "Recording" : "Transcript";
        ResultSummary.Text = "Local entry · sync not connected";
        HistoryNavigationHint.Text = entry.Entry.HasTranscript ? "⌫ / Esc Back   ·   Select text to copy a passage" : "⌫ / Esc Back";
        TranscriptScroll.ChangeView(null, 0, null, true);
        if (entry.Entry.HasTranscript) CopyButton.Focus(FocusState.Programmatic);
        else if (EntryActions.Visibility == Visibility.Visible) EditButton.Focus(FocusState.Programmatic);
    }

    internal void GoBack()
    {
        if (_acting) return;
        if (_selecting) { SetSelecting(false); return; }
        if (IsReading)
        {
            ShowList();
            Entries.Focus(FocusState.Programmatic);
        }
        else ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowList()
    {
        StopAudioPlayback();
        StopReadback();
        ReadingPage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = HistoryFilters.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = AudioActions.Visibility = Visibility.Collapsed;
        ListActions.Visibility = Visibility.Visible;
        ++_audioAvailabilityGeneration;
        HistoryBreadcrumbs.SetItems(new("Quick Launch", OpenLauncher, "Back from history"), new("History"));
        PageTitle.Text = "History";
        HistoryNavigationHint.Text = _selecting ? "↑↓ Navigate   Enter / Space Select   Esc Done" : "⌫ / Esc Back   ↑↓ Navigate   Enter Open";
        UpdateResultSummary();
    }

    private void OpenLauncher() { ShowList(); LauncherRequested?.Invoke(this, EventArgs.Empty); }

    private void UpdateResultSummary() => ResultSummary.Text =
        _loading ? "Loading…" : _loadError is not null ? "Read failed"
            : _selecting ? $"{SelectedIds.Length} selected · {FilteredEntries.Count} shown"
            : $"{FilteredEntries.Count} {(FilteredEntries.Count == 1 ? "entry" : "entries")} · local history";

    private void UpdateBulkActions()
    {
        if (BulkToolbar is null) return;
        var available = !_acting && !_loading && _loadError is null && _actions is not null;
        var hasShownEntries = FilteredEntries.Any(item => item.Entry.PersistedRecordId is not null);
        SelectEntriesButton.IsEnabled = available && (_selecting || hasShownEntries);
        SelectAllShownButton.IsEnabled = available && hasShownEntries;
        SelectEntriesButton.Content = _selecting ? "Done selecting · S" : "Select entries · S";
        OpenButton.Visibility = _selecting ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.IsEnabled = !_loading && !_acting && FilteredEntries.Count > 0;
        SelectionActions.Visibility = _selecting ? Visibility.Visible : Visibility.Collapsed;
        ExportSelectedButton.IsEnabled = DeleteSelectedButton.IsEnabled = available && SelectedIds.Length > 0;
        ClearHistoryButton.IsEnabled = available && _store.Query().Any(entry => entry.PersistedRecordId is not null);
    }

    private void SetSelecting(bool selecting)
    {
        _selecting = selecting;
        _bulkFocusIndex = -1;
        if (Entries.SelectionMode == ListViewSelectionMode.Multiple) Entries.SelectedItems.Clear();
        else Entries.SelectedIndex = -1;
        Entries.ItemContainerStyle = selecting ? null : (Style)Application.Current.Resources["CommandItemStyle"];
        Entries.SelectionMode = selecting ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        Entries.IsItemClickEnabled = !selecting;
        HistoryNavigationHint.Text = selecting ? "↑↓ Navigate   Enter / Space Select   Esc Done" : "↑↓ Navigate   Enter Open";
        if (!selecting) Entries.SelectedItem = FilteredEntries.FirstOrDefault();
        UpdateResultSummary(); UpdateBulkActions();
    }

    private void SelectEntries_Click(object sender, RoutedEventArgs e) => SetSelecting(!_selecting);
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilteredEntries.Where(item => item.Entry.PersistedRecordId is not null))
            if (!Entries.SelectedItems.Contains(item)) Entries.SelectedItems.Add(item);
    }
    private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingFilters || !_selecting) return;
        UpdateResultSummary(); UpdateBulkActions();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) => await DeleteSnapshotAsync(SelectedIds, false);
    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _acting || _actions is null) return;
        try { await DeleteSnapshotAsync(await _actions.SnapshotIdsAsync(), true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ResultSummary.Text = "Could not read history for confirmation. Try again."; }
    }

    private async Task DeleteSnapshotAsync(string[] ids, bool clear)
    {
        if (_closing || _acting || _actions is null || ids.Length == 0) return;
        _acting = true; UpdateBulkActions();
        string? notice = null;
        try
        {
            var entryLabel = ids.Length == 1 ? "entry" : "entries";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = clear ? $"Clear {ids.Length} history {entryLabel}?" : $"Delete {ids.Length} selected {entryLabel}?",
                Content = "This permanently deletes these entries and their saved local audio. Entries added after this confirmation opens will be kept.",
                PrimaryButtonText = clear ? "Clear history" : "Delete selected", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || _closing) return;
            notice = await TrackWriteAsync(_actions.DeleteAsync(ids)) ? "Confirmed history entries deleted."
                : "History could not be deleted. No entries were removed by this action.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { notice = "History could not be deleted. Refresh and try again."; }
        finally
        {
            _acting = false;
            await RefreshAsync();
            if (!_closing)
            {
                UpdateBulkActions();
                if (notice is not null) ResultSummary.Text = notice;
                RestoreBulkFocus(clear ? ClearHistoryButton : DeleteSelectedButton);
            }
        }
    }

    private void RestoreBulkFocus(Control preferred)
    {
        var target = preferred.IsEnabled ? preferred : SelectEntriesButton.IsEnabled ? SelectEntriesButton : HistoryTabs.SelectedControl;
        target.Focus(FocusState.Programmatic);
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedIds;
        if (_closing || _acting || _actions is null || ids.Length == 0 || XamlRoot is null) return;
        _acting = true; UpdateBulkActions();
        string? notice = null;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { SuggestedFileName = "history-selection", Title = $"Export {ids.Length} selected {(ids.Length == 1 ? "entry" : "entries")}" };
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (_closing || file is null) return;
            await TrackWriteAsync(_actions.ExportFileAsync(ids, file.Path));
            notice = $"Exported {ids.Length} history {(ids.Length == 1 ? "entry" : "entries")}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { notice = "Export failed. Refresh your selection or choose a writable location."; }
        finally
        {
            _acting = false;
            await RefreshAsync();
            if (!_closing)
            {
                UpdateBulkActions();
                if (notice is not null) ResultSummary.Text = notice;
                RestoreBulkFocus(ExportSelectedButton);
            }
        }
    }

    private void Entry_Click(object sender, ItemClickEventArgs e)
    {
        Entries.SelectedItem = e.ClickedItem;
        OpenSelected();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _acting || _actions is null || _opened?.Entry.PersistedRecordId is not { } id) return;
        StopAudioPlayback();
        StopReadback();
        _acting = true;
        var openedId = _opened.Entry.RecordId;
        var editor = new TextBox { AcceptsReturn = true, Text = _opened.Text.ReplaceLineEndings("\r"), TextWrapping = TextWrapping.Wrap,
            MinWidth = 320, MaxHeight = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, "Transcript text to edit");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(editor);
        panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Edit transcript", Content = panel,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        TypeWhisper.Core.Models.TranscriptionRecord? saved = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (_closing) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            try
            {
                saved = await TrackWriteAsync(_actions.EditAsync(id, editor.Text.ReplaceLineEndings("\n")));
                args.Cancel = saved is null;
                if (saved is null) error.Text = "Could not save. Your edits are still here. Try again.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                args.Cancel = true;
                error.Text = string.IsNullOrWhiteSpace(editor.Text) ? "Enter transcript text." : "Could not save. Your edits are still here. Try again.";
            }
            finally { deferral.Complete(); }
        };
        try
        {
            await ShowDialogAsync(dialog);
            if (!_closing && saved is not null)
            {
                _store.Upsert(HistoryEntryAdapter.FromRecord(saved));
                ApplyFilters();
                Entries.SelectedItem = FilteredEntries.FirstOrDefault(item => item.Entry.RecordId == openedId);
                OpenSelected();
                ActionNotice.Text = "Transcript saved.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ActionNotice.Text = "The editor could not be opened. Try again."; }
        finally { _acting = false; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _acting || _actions is null || _opened?.Entry.PersistedRecordId is not { } id) return;
        StopAudioPlayback();
        StopReadback();
        _acting = true;
        var entry = _opened;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Delete this history entry?",
                Content = $"{entry.Title}\n\nThis permanently deletes this entry and its saved audio from local history.",
                PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || _closing) return;
            if (!await TrackWriteAsync(_actions.DeleteAsync(id))) { if (!_closing) ActionNotice.Text = "Could not delete this entry. Try again."; return; }
            if (_closing) return;
            _store.Remove(entry.Entry.RecordId);
            ApplyFilters();
            ResultSummary.Text = "History entry deleted";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ActionNotice.Text = "Could not delete this entry. Try again."; }
        finally { _acting = false; if (!_closing) UpdateAudioCleanupNotice(); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _acting || _actions is null || _opened?.Entry.PersistedRecordId is not { } id || XamlRoot is null) return;
        _acting = true;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { SuggestedFileName = "transcript", Title = "Export transcript" };
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (_closing || file is null) return;
            await TrackWriteAsync(_actions.ExportFileAsync(id, file.Path));
            if (!_closing) ActionNotice.Text = "Transcript exported.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ActionNotice.Text = "Could not export the transcript. Choose a writable location and try again."; }
        finally { _acting = false; }
    }
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
        if (_closing || _acting || !IsReading || _opened is null || !_opened.Entry.HasTranscript) return;
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
