using TypeWhisper.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Storage;

namespace TypeWhisper.WinUI;

public sealed partial class FileTranscriptionView : UserControl
{
    private readonly FileTranscriptionQueue _queue = new(new FileTranscriptionQueueStore(WinUIProfile.DataPath("file-queue.json")));
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _notice = Text("", 12, true);
    private readonly Breadcrumbs _crumbs = new();
    private readonly Border _primaryHost = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private LocalDictationSession? _session;
    private readonly ScrollViewer _scroll;
    private FileTranscriptionJob? _result;
    private FileTranscriptionJob? _selectedJob;
    private ChoicePicker? _formatPicker;
    private string _format = "txt";
    private bool _picking;
    private ContentDialog? _recoveryDialog;
    private Task _recoveryOperation = Task.CompletedTask;
    private Task _exportOperation = Task.CompletedTask;
    internal event Action? ExitRequested;

    public FileTranscriptionView()
    {
        var root = new Grid { Background = Brush("InkBrush"), RowSpacing = 10, Padding = new Thickness(24, 8, 24, 0) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Spacing = 14 };
        _tabs.SetItems([new("queue", "Files"), new("watch", "Watch folder")], "queue");
        _tabs.SelectionChanged += id => { _watchTab = id == "watch"; _result = null; Render(); _tabs.SelectedControl.Focus(FocusState.Programmatic); };
        header.Children.Add(_tabs);
        var heading = Text("File transcription", 22); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1); header.Children.Add(heading); root.Children.Add(header);
        _scroll = new ScrollViewer { Padding = (Thickness)Application.Current.Resources["VerticalScrollGutter"], Content = _body, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite); Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        var footer = new Grid { MinHeight = 76, RowSpacing = 10 };
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        footer.Children.Add(_crumbs);
        var actionRow = new Grid { ColumnSpacing = 8 };
        actionRow.ColumnDefinitions.Add(new()); actionRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actionRow.Children.Add(_actions); Grid.SetColumn(_primaryHost, 1); actionRow.Children.Add(_primaryHost);
        Grid.SetRow(actionRow, 1); footer.Children.Add(actionRow);
        var border = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush("HairlineBrush") };
        Grid.SetRow(border, 3); root.Children.Add(border);
        Content = root;
        _queue.Changed += () =>
        {
            // Background progress must not replace a transcript the user is selecting or reading.
            void UpdateQueue() { if (!_watchTab && _result is null) Render(); }
            if (DispatcherQueue.HasThreadAccess) UpdateQueue();
            else DispatcherQueue.TryEnqueue(UpdateQueue);
        };
        InitializeWatcher();
        Render();
    }
    internal void Connect(LocalDictationSession session)
    {
        _session = session;
        if (_watcher.Settings?.StartWithApp == true) _watcher.Start();
        session.Changed += () => DispatcherQueue.TryEnqueue(() => { if (IsLoaded && !_watchTab && !_queue.Running && !_picking && _result is null) Render(); });
    }
    internal void Present() { _notice.Text = "Uses the model selected in Dictation. Cloud providers receive the selected audio when you choose Start."; Render(); }
    internal void Stop() { _queue.Cancel(); _recoveryDialog?.Hide(); }
    internal bool ContainsSource(string path) => _queue.Jobs.Any(job =>
        string.Equals(job.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    internal Task CancelAndDrainAsync() => _queue.CancelAndDrainAsync();
    internal async Task ShutdownAsync()
    {
        IsEnabled = false;
        var drain = _queue.ShutdownAsync();
        _recoveryDialog?.Hide();
        _watchTimer.Stop();
        await Task.WhenAll(_recoveryOperation, drain, _exportOperation, _watcher.ShutdownAsync());
    }
    internal void GoBack()
    {
        if (_picking) return;
        if (_formatPicker?.IsPopupOpen == true) { _formatPicker.ClosePopup(); return; }
        if (_watchTab && _watchResult is not null) { _watchResult = null; Render(); FocusPrimaryAction(); }
        else if (_result is not null) { _result = null; Render(); FocusPrimaryAction(); }
        else ExitRequested?.Invoke();
    }
    private void Render()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var restoreFocus = false;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current == _body || current == _actions || current == _primaryHost || current == _crumbs) { restoreFocus = true; break; }
        var previousName = focused is FrameworkElement element ? AutomationProperties.GetName(element) : "";
        _primaryHost.Child = null;
        RenderContent();
        var primary = _actions.Children.OfType<HandCursorButton>().LastOrDefault(button =>
            button.Style == (Style)Application.Current.Resources["PrimaryButtonStyle"] || button.Content?.ToString()?.StartsWith("Cancel run") == true);
        if (primary is not null) { _actions.Children.Remove(primary); _primaryHost.Child = primary; }
        if (!_watchTab && (_queue.Running || _picking))
            foreach (var action in _actions.Children.OfType<Control>()) action.IsEnabled = false;
        if (restoreFocus) DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || (_watchTab && _watchEditing)) return;
            UpdateLayout();
            var candidates = _body.Children.OfType<Control>().Concat(_actions.Children.OfType<Control>());
            var replacement = string.IsNullOrEmpty(previousName) ? null : candidates.FirstOrDefault(c => AutomationProperties.GetName(c) == previousName);
            var next = replacement ?? _primaryHost.Child as Control;
            if (next is { IsEnabled: true }) next.Focus(FocusState.Keyboard);
        });
    }

    private void FocusPrimaryAction()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateLayout();
            if (_primaryHost.Child is Control { IsEnabled: true } primary) primary.Focus(FocusState.Keyboard);
        });
    }
    private void RenderContent()
    {
        _body.Children.Clear(); _actions.Children.Clear(); _formatPicker = null; _primaryAction = null;
        if (_watchTab) { RenderWatcher(); return; }
        _crumbs.SetItems(new("Quick Launch", () => { if (!_picking) ExitRequested?.Invoke(); }),
            new("Files", _result is null ? null : () => { _result = null; Render(); }), new(_result is null ? "Queue" : "Result"));
        if (_result is not null) { RenderResult(_result); return; }
        var recovery = new CheckBox
        {
            Content = "Remember this queue after restart", IsChecked = _queue.RecoveryEnabled,
            IsEnabled = !_queue.Running && !_picking
        };
        AutomationProperties.SetName(recovery, "Remember file queue after restart");
        void ChangeRecovery()
        {
            var enabled = recovery.IsChecked == true;
            var saved = _queue.SetRecoveryEnabled(enabled);
            _notice.Text = saved ? enabled
                ? "Queue recovery is on. Saved jobs require an explicit start or retry after restart."
                : "Queue recovery is off. Saved recovery data was removed; this session’s results remain available."
                : _queue.RecoveryError ?? "The recovery setting could not be changed.";
            Render();
        }
        recovery.Checked += (_, _) => ChangeRecovery();
        recovery.Unchecked += (_, _) => ChangeRecovery();
        var recoveryContent = new StackPanel { Spacing = 6 };
        recoveryContent.Children.Add(recovery);
        recoveryContent.Children.Add(Text("Recovery saves file paths and transcripts locally, including when History is off. Original media files are not copied. Turn recovery off to remove its saved data.", 11, true));
        _body.Children.Add(new Expander { Header = _queue.RecoveryEnabled ? "Queue recovery is on" : "Queue recovery", Content = recoveryContent, HorizontalAlignment = HorizontalAlignment.Stretch });
        if (_queue.RecoveryError is { } recoveryError) _body.Children.Add(Text(recoveryError, 12, true));
        if (_queue.RecoveryError is not null)
        {
            var discardRecovery = Button("Discard saved queue data…", () =>
            {
                if (_recoveryOperation.IsCompleted) _recoveryOperation = DiscardRecoveryAsync();
            }, destructive: true);
            discardRecovery.IsEnabled = !_queue.Running && !_picking;
            _body.Children.Add(discardRecovery);
        }
        var dropContent = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        dropContent.Children.Add(new TypeWhisperGlyph { Kind = "file", Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
        dropContent.Children.Add(Text("Drop audio or video files here", 15));
        var importActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var browse = Button("Choose files…", async () => await ChooseFiles()); browse.IsEnabled = !_queue.Running && !_picking; importActions.Children.Add(browse);
        dropContent.Children.Add(importActions);
        dropContent.Children.Add(Text("Audio & video · up to 20 files · maximum 60 minutes per file", 11, true));
        var drop = new Border { Child = dropContent, Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), AllowDrop = true };
        drop.DragOver += (_, e) => { e.AcceptedOperation = !_queue.Running && !_picking && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; drop.BorderBrush = Brush(e.AcceptedOperation == DataPackageOperation.Copy ? "AccentBrush" : "HairlineBrush"); };
        drop.DragLeave += (_, _) => drop.BorderBrush = Brush("HairlineBrush");
        drop.Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (_queue.Running || _picking || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                AddPaths(items.OfType<StorageFile>().Select(file => file.Path));
                if (items.Any(item => item is not StorageFile)) _notice.Text += " Folders are not supported.";
            }
            catch (Exception) { _notice.Text = "Could not accept this drop. Try Choose files instead."; }
            finally { drop.BorderBrush = Brush("HairlineBrush"); deferral.Complete(); }
        };
        if (_queue.Jobs.Count == 0) _body.Children.Add(drop);
        else _actions.Children.Add(Button("Add files…", async () => await ChooseFiles()));
        if (_session?.IsReady != true) _body.Children.Add(Text("Choose a ready model in Dictation before starting.", 12, true));
        else if (!_queue.Running && !_session.CanTranscribeFile) _body.Children.Add(Text("Finish the current recording or model operation before starting.", 12, true));
        if (_queue.Jobs.Count == 0) _body.Children.Add(Text("Choose audio or video files to transcribe. Turn on queue recovery to keep results after closing the app.", 13, true));
        else
        {
            _body.Children.Add(Text($"{_queue.Jobs.Count} {(_queue.Jobs.Count == 1 ? "file" : "files")} · {_session?.ActiveModelName ?? "No model selected"}", 12, true));
            if (_selectedJob is null || !_queue.Jobs.Contains(_selectedJob)) _selectedJob = _queue.Jobs.FirstOrDefault();
            foreach (var job in _queue.Jobs) AddRow(job);
        }
        if (!_queue.Running && _selectedJob is { } selected)
        {
            var remove = Button("Remove · Del", () => RemoveSelected(), destructive: true); remove.IsEnabled = !_picking;
            _actions.Children.Add(remove);
            if (selected.Status is FileTranscriptionStatus.Failed or FileTranscriptionStatus.Canceled)
                _actions.Children.Add(Button("Retry · R", async () => { if (_queue.Retry(selected)) await RunQueue(selected); }));

        }
        if (!_queue.Running && _queue.Jobs.Count(j => j.Status == FileTranscriptionStatus.Ready) > 1)
            _actions.Children.Add(Button("Export all… · X", async () => await ExportAllAsync()));
        if (_queue.Running)
        {
            _primaryAction = () => { Stop(); _notice.Text = "Canceling… Completed results are kept."; Render(); };
            _actions.Children.Add(Button("Cancel run · Enter", _primaryAction, destructive: true));
        }
        else
        {
            if (!_queue.Jobs.Any(job => job.Status == FileTranscriptionStatus.Queued) && _selectedJob?.Status == FileTranscriptionStatus.Ready)
            {
                _primaryAction = OpenSelectedResult;
                _actions.Children.Add(Button("View result · Enter", _primaryAction, primary: true));
            }
            else
            {
                _primaryAction = async () => await RunQueue();
                var start = Button("Start transcription · Enter", _primaryAction, primary: true);
                start.IsEnabled = _session?.CanTranscribeFile == true && !_picking && _queue.Jobs.Any(job => job.Status == FileTranscriptionStatus.Queued); _actions.Children.Add(start);
            }
        }
    }
    private async Task RunQueue(FileTranscriptionJob? onlyJob = null)
    {
        if (_session is null || !_session.CanTranscribeFile || _queue.Running) return;
        _notice.Text = "Transcribing with the model selected in Dictation…";
        try { await _queue.RunAsync(_session.TranscribeFileAsync, _session.AcceptFileResult, onlyJob); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _notice.Text = "File processing failed: " + ex.Message; return; }
        _notice.Text = $"{_queue.Jobs.Count(j => j.Status == FileTranscriptionStatus.Ready)} completed · {_queue.Jobs.Count(j => j.Status == FileTranscriptionStatus.Failed)} failed · {_queue.Jobs.Count(j => j.Status == FileTranscriptionStatus.Canceled)} canceled";
        if (onlyJob?.Status == FileTranscriptionStatus.Ready) { _selectedJob = onlyJob; _result = onlyJob; }
        if (!_watchTab && (_result is null || onlyJob?.Status == FileTranscriptionStatus.Ready)) Render();
        if (onlyJob?.Status == FileTranscriptionStatus.Ready && !_watchTab) FocusPrimaryAction();
    }
    private async Task DiscardRecoveryAsync()
    {
        if (_queue.Running || _picking || _queue.IsShutdown) return;
        _picking = true;
        try
        {
            var dialog = _recoveryDialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Discard saved queue data?",
                Content = "Remove the saved recovery queue and turn recovery off? Original media files and this session’s results are kept.",
                PrimaryButtonText = "Discard saved data", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_queue.IsShutdown)
                _notice.Text = _queue.DiscardRecoveryData() ? "Saved queue recovery data was removed." : _queue.RecoveryError ?? "Recovery data could not be removed.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_queue.IsShutdown) _notice.Text = "Saved recovery data could not be discarded. Try again."; }
        finally
        {
            _recoveryDialog = null; _picking = false;
            if (!_queue.IsShutdown)
                try { Render(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { _notice.Text = "The queue view could not be refreshed. Reopen Files to try again."; }
        }
    }
    private void AddRow(FileTranscriptionJob job)
    {
        var labels = new StackPanel { Spacing = 6 };
        var name = Text(job.Name, 13); name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap; ToolTipService.SetToolTip(name, job.Name); labels.Children.Add(name);
        labels.Children.Add(Text(job.Stage, 11, true));
        if (job.Status == FileTranscriptionStatus.Processing) labels.Children.Add(new ProgressBar { Height = 2, IsIndeterminate = true });
        var row = new HandCursorButton { Content = labels, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources[_selectedJob == job ? "PrimaryButtonStyle" : "SecondaryButtonStyle"] };
        AutomationProperties.SetName(row, job.Name + ", " + job.Stage);
        AutomationProperties.SetItemStatus(row, _selectedJob == job ? "Selected" : "Not selected");
        row.Click += (_, _) => { _selectedJob = job; Render(); };
        row.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true; _selectedJob = job;
            if (job.Status == FileTranscriptionStatus.Ready) OpenSelectedResult();
            else if (job.Status == FileTranscriptionStatus.Queued) await RunQueue(job);
        };
        _body.Children.Add(row);
    }
    private void OpenSelectedResult()
    {
        if (_selectedJob?.Status != FileTranscriptionStatus.Ready) return;
        _result = _selectedJob; _scroll.ChangeView(null, 0, null, true); Render(); FocusPrimaryAction();
    }
    private void RemoveSelected()
    {
        if (_selectedJob is null || _picking || _queue.Running) return;
        _queue.Remove(_selectedJob); _notice.Text = "Removed from the queue. The original file is unchanged."; Render();
    }
    private void RenderResult(FileTranscriptionJob job)
    {
        _body.Children.Add(Text(job.Name, 16));
        if (_queue.RecoveryError is { } recoveryError) _body.Children.Add(Text(recoveryError, 12, true));
        var duration = TimeSpan.FromSeconds(job.Result!.Duration).ToString(@"hh\:mm\:ss");
        _body.Children.Add(Text($"{job.Result.DisplayName ?? job.Result.Model} · {duration}", 12, true));
        if (job.Result.Warning is { } warning) _body.Children.Add(Text(warning, 12, true));
        _body.Children.Add(new Border { Child = new TextBlock { Text = job.Result!.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Brush("TextBrush") }, Padding = new Thickness(18), Background = Brush("SurfaceBrush"), CornerRadius = new CornerRadius(10) });
        _formatPicker = new ChoicePicker(); _formatPicker.Configure("Export format", "file", "File export format");
        var formats = new List<Choice> { new("txt", "Plain text · TXT", "Transcribed text") };
        if (FileTranscriptionQueue.HasSubtitles(job.Result!))
        {
            formats.Add(new("srt", "Subtitles · SRT", "Provider timestamps"));
            formats.Add(new("vtt", "Subtitles · WebVTT", "Provider timestamps"));
            _body.Children.Add(Text("TXT includes dictionary corrections and snippet expansion. SRT and WebVTT preserve the provider’s original segment text and timing.", 12, true));
        }
        else { _format = "txt"; _body.Children.Add(Text("Subtitle export is unavailable because this provider did not return usable timing.", 12, true)); }
        _formatPicker.SetOptions(formats, _format);
        _formatPicker.SelectionChanged += selected => _format = selected; _body.Children.Add(_formatPicker);
        _actions.Children.Add(Button("Export… · X", async () => await Export(job)));
        _primaryAction = () => CopyResult(job.Result.Text);
        _actions.Children.Add(Button("Copy text · Enter", _primaryAction, primary: true));
    }
    internal async void AddRecording(string path)
    {
        _watchTab = false; _tabs.SetSelected("queue"); _result = null;
        var existing = _queue.Jobs.FirstOrDefault(j => string.Equals(j.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is null) { AddPaths([path]); existing = _queue.Jobs.LastOrDefault(j => string.Equals(j.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)); }
        if (existing is null) return;
        if (existing.Status == FileTranscriptionStatus.Ready) { _result = existing; Render(); return; }
        if (existing.Status is FileTranscriptionStatus.Failed or FileTranscriptionStatus.Canceled) _queue.Retry(existing);
        if (_session?.CanTranscribeFile == true) await RunQueue(existing);
    }
    internal bool CanAcceptActivation => !(_watchTab && (_watchEditing || _watchResult is not null)) && !_queue.IsShutdown && !_queue.Running && !_picking && _result is null
        && _recoveryDialog is null && _recoveryOperation.IsCompleted && _exportOperation.IsCompleted;
    internal string AddActivatedFiles(IReadOnlyList<string> paths)
    {
        if (!CanAcceptActivation) return "Files were not added. Finish the current file operation or close the result, then retry.";
        _watchTab = false; _tabs.SetSelected("queue");
        AddPaths(paths);
        return _notice.Text;
    }
    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0; var errors = new List<string>();
        foreach (var path in paths) { var error = _queue.Add(path); if (error is null) added++; else errors.Add(error); }
        _notice.Text = $"{added} {(added == 1 ? "file" : "files")} added. " + string.Join(' ', errors.Distinct()); Render();
    }
    private async Task ChooseFiles()
    {
        if (_queue.IsShutdown || _picking || _queue.Running || XamlRoot is null) return;
        _picking = true;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = "Choose audio or video files" };
            foreach (var extension in FileTranscriptionQueue.Extensions) picker.FileTypeFilter.Add(extension);
            var files = await picker.PickMultipleFilesAsync();
            if (_queue.IsShutdown) return;
            if (files.Count > 0) AddPaths(files.Select(file => file.Path)); else _notice.Text = "Selection canceled. Your queue is unchanged.";
        }
        catch (Exception) { _notice.Text = "The file dialog could not be opened. Try dropping a file."; }
        finally { _picking = false; if (!_queue.IsShutdown) Render(); }
    }
    private async Task Export(FileTranscriptionJob job)
    {
        if (_queue.IsShutdown || _picking || XamlRoot is null) return;
        _picking = true; var format = _format;
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = Path.GetFileNameWithoutExtension(job.Name), Title = "Export transcript" };
            picker.FileTypeChoices.Add(format.ToUpperInvariant(), new List<string> { "." + format });
            var file = await picker.PickSaveFileAsync();
            if (_queue.IsShutdown) return;
            if (file is null) { _notice.Text = "Export canceled. Nothing was written."; return; }
            var destination = Path.GetFullPath(file.Path);
            if (_queue.Jobs.Any(item => string.Equals(Path.GetFullPath(item.Path), destination, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Choose a filename different from the source media.");
            _exportOperation = WriteExportAsync(destination, FileTranscriptionQueue.Export(job, format));
            await _exportOperation;
            if (!_queue.IsShutdown) _notice.Text = "Transcript exported.";
        }
        catch (IOException) { _notice.Text = "Could not save. Choose a writable destination different from the source media."; }
        catch (Exception) { _notice.Text = "Export could not be completed. Your result is still available."; }
        finally { _picking = false; _exportOperation = Task.CompletedTask; }
    }
    private static async Task WriteExportAsync(string destination, string text)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".transcript-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static HandCursorButton Button(string text, Action action, bool primary = false, bool destructive = false)
    {
        var button = new HandCursorButton { Content = text, MinHeight = 34, Style = (Style)Application.Current.Resources[destructive ? "DestructiveButtonStyle" : primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"] };
        button.Click += (_, _) => action(); return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
