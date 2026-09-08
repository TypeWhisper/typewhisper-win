using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Presentation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.System;

namespace TypeWhisper.WinUI;

public sealed partial class FileTranscriptionView
{
    private readonly WatchedFolderProcessor _watcher = new(WinUIProfile.DataPath("watched-folder.json"));
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TabBar _tabs = new();
    private bool _watchTab;
    private bool _watchEditing;
    private TextBlock? _watchStatus;
    private string _watchRenderedState = "";
    private string WatchState => _watcher.Watching + "|" + (!_watcher.Watching && _watcher.Busy) + "|" + _watcher.Error + "|" + string.Join("|", _watcher.Files.Select(f => f.Id + f.Status + f.Message));
    private Action? _primaryAction;
    private WatchedFile? _watchResult;
    private WatchedFolderSettings? _watchDraft;
    private TextBox? _watchInput;
    private TextBox? _watchOutput;
    private CheckBox? _watchAutoStart;
    private string _watchFormat = "txt";

    private void InitializeWatcher()
    {
        _watcher.Changed += () =>
        {
            if (_watchStatus is not null) _watchStatus.Text = _watcher.Error ?? _watcher.Status;
            if (_watchTab && !_watchEditing && !_picking && IsLoaded && _watchRenderedState != WatchState) Render();
        };
        _watchTimer.Tick += async (_, _) =>
        {
            if (_session is not null && !_queue.IsShutdown)
                await _watcher.PollAsync(!_queue.Running && _session.CanTranscribeFile, _session.TranscribeFileAsync);
        };
        _watchTimer.Start();
    }

    internal void ShowWatchFolder()
    { _watchTab = true; _tabs.SetSelected("watch"); _result = null; Render(); }

    private void RenderWatcher()
    {
        _watchRenderedState = WatchState;
        _crumbs.SetItems(new("Quick Launch", () => ExitRequested?.Invoke()), new("Files"), new("Watch folder"));
        _watchStatus = Text(_watcher.Error ?? _watcher.Status, 14);
        AutomationProperties.SetLiveSetting(_watchStatus, AutomationLiveSetting.Polite);
        _body.Children.Add(_watchStatus);
        _notice.Text = "Uses the model selected in Dictation. Cloud models upload each file automatically while watching. Originals are kept.";
        if (_watcher.Settings is null || _watchEditing)
        {
            _watchEditing = true;
            _watchDraft ??= _watcher.Settings ?? new("", "");
            _watchInput = FolderField("Watch folder", _watchDraft.Input);
            _watchOutput = FolderField("Save transcripts to (optional)", _watchDraft.Output, advanced: true);
            _watchInput.TextChanged += (_, _) => _watchDraft = _watchDraft with { Input = _watchInput.Text };
            _watchOutput.TextChanged += (_, _) => _watchDraft = _watchDraft with { Output = _watchOutput.Text };
            _formatPicker = new ChoicePicker(); _formatPicker.Configure("Output format", "file", "Watch folder output format");
            _watchFormat = _watchDraft.Format;
            _formatPicker.SetOptions([new("txt", "Plain text · TXT", "Works with every model"),
                new("srt", "Subtitles · SRT", "Requires actual provider timestamps"),
                new("vtt", "Subtitles · WebVTT", "Requires actual provider timestamps")], _watchFormat);
            _formatPicker.SelectionChanged += id => { _watchFormat = id; _watchDraft = _watchDraft with { Format = id }; };
            _body.Children.Add(_formatPicker);
            _watchAutoStart = new CheckBox { Content = "Resume watching when TypeWhisper starts", IsChecked = _watchDraft.StartWithApp };
            _watchAutoStart.Checked += (_, _) => _watchDraft = _watchDraft with { StartWithApp = true };
            _watchAutoStart.Unchecked += (_, _) => _watchDraft = _watchDraft with { StartWithApp = false };
            _body.Children.Add(_watchAutoStart);
            _body.Children.Add(Text("Processes existing and new audio/video files in this folder, without subfolders. Progress and transcripts are saved locally for recovery, independently of History. Automatic exports do not add History entries.", 12, true));
            if (_watcher.Settings is not null) _actions.Children.Add(Button("Cancel", () => { _watchEditing = false; _watchDraft = null; Render(); }));
            _primaryAction = () =>
            {
                if (_watcher.Configure(new(_watchInput.Text.Trim(), _watchOutput.Text.Trim(), _watchFormat, _watchAutoStart.IsChecked == true)))
                { _watchEditing = false; _watchDraft = null; _watcher.Start(); Render(); }
            };
            var start = Button("Start watching · Enter", _primaryAction, primary: true);
            start.IsEnabled = _watcher.Error is null && !_watcher.Busy;
            _actions.Children.Add(start);
            return;
        }

        var settings = _watcher.Settings;
        if (_watchResult?.Result is { } transcript)
        {
            _body.Children.Add(Text(Path.GetFileName(_watchResult.Path), 16));
            _body.Children.Add(Text(transcript.DisplayName ?? transcript.Model, 12, true));
            var resultText = Text(transcript.Text, 14); resultText.IsTextSelectionEnabled = true;
            _body.Children.Add(resultText);
            _actions.Children.Add(Button("Back to folder", () => { _watchResult = null; Render(); FocusPrimaryAction(); }));
            _primaryAction = () => CopyResult(transcript.Text);
            _actions.Children.Add(Button("Copy text · Enter", _primaryAction, primary: true));
            return;
        }
        _body.Children.Add(Text(settings.Input, 13));
        _body.Children.Add(Text("Exports to " + settings.Output + " · " + settings.Format.ToUpperInvariant(), 12, true));
        var completed = _watcher.Files.Count(f => f.Status == "Completed");
        var failed = _watcher.Files.Count(f => f.Status == "Failed");
        _body.Children.Add(Text($"{completed} exported · {failed} need attention", 13));
        if (_watcher.Files.Count == 0)
            _body.Children.Add(Text("Drop recordings into the watch folder. Finished files are transcribed and exported automatically while TypeWhisper is running.", 14, true));
        if (_watcher.Files.Count > 40) _body.Children.Add(Text("Showing 40 recent files, with failures first. All exported transcripts remain in the output folder.", 12, true));
        foreach (var file in _watcher.Files.Reverse().OrderByDescending(f => f.Status == "Failed").Take(40))
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Text(Path.GetFileName(file.Path), 13));
            row.Children.Add(Text(file.Status + (file.Message is null ? "" : " · " + file.Message), 12, true));
            if (file.Result is not null)
            {
                var openResult = new HandCursorButton { Content = row, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
                AutomationProperties.SetName(openResult, "Open transcript for " + Path.GetFileName(file.Path));
                openResult.Click += (_, _) => { _watchResult = file; Render(); FocusPrimaryAction(); };
                _body.Children.Add(openResult);
            }
            else _body.Children.Add(new Border { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = Brush("SurfaceBrush") });
        }
        if (_watcher.Error is not null)
            _actions.Children.Add(Button("Retry saving", () => { _watcher.RetrySavingProgress(); Render(); }));
        if (_watcher.Watching || _watcher.Busy)
        {
            _primaryAction = () => { _watcher.Stop(); Render(); };
            _actions.Children.Add(Button("Pause watching · Enter", _primaryAction, primary: true));
        }
        else
        {
            _actions.Children.Add(Button("Change folders", () => { _watchEditing = true; _watchDraft = _watcher.Settings; Render(); }));
            _primaryAction = () => { _watcher.Start(); Render(); };
            _actions.Children.Add(Button("Start watching · Enter", _primaryAction, primary: true));
        }
        if (failed > 0)
        {
            var retry = Button("Retry failed · R", () => _watcher.RetryFailures());
            retry.IsEnabled = !_watcher.Busy && _watcher.Error is null; _actions.Children.Add(retry);
        }
        _actions.Children.Add(Button("Open exports · F", async () =>
        {
            try { await Launcher.LaunchFolderPathAsync(settings.Output); }
            catch (Exception) { _notice.Text = "The export folder could not be opened."; }
        }));
    }

    private TextBox FolderField(string label, string value, bool advanced = false)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var input = new TextBox { Header = label, Text = value, PlaceholderText = "Choose a folder…", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, label); row.Children.Add(input);
        var choose = Button("Browse…", async () =>
        {
            if (_picking || XamlRoot is null) return;
            _picking = true;
            try
            {
                var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = label };
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null && !_queue.IsShutdown) input.Text = folder.Path;
            }
            catch (Exception) { _notice.Text = "The folder picker could not be opened. You can enter the path directly."; }
            finally { _picking = false; }
        });
        choose.VerticalAlignment = VerticalAlignment.Bottom;
        AutomationProperties.SetName(choose, "Browse " + label);
        Grid.SetColumn(choose, 1); row.Children.Add(choose);
        if (advanced) _body.Children.Add(new Expander { Header = "Output folder · Transcripts subfolder by default", Content = row, HorizontalAlignment = HorizontalAlignment.Stretch });
        else _body.Children.Add(row);
        return input;
    }

    private void CopyResult(string text)
    {
        try { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); _notice.Text = "Transcript copied."; }
        catch (Exception) { _notice.Text = "Clipboard is unavailable. Select the text to copy it."; }
    }

    internal void HandleActionKey(KeyRoutedEventArgs e)
    {
        if (e.Handled || _picking || !_exportOperation.IsCompleted || _queue.IsShutdown || _recoveryDialog is not null ||
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox or CheckBox or ComboBox or Slider) return;
        foreach (var modifier in new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows })
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (e.Key == VirtualKey.Enter && focused is Button) return;
        if (e.Key == VirtualKey.Enter && _primaryAction is not null) _primaryAction();
        else if (!_watchTab && _result is null && e.Key == VirtualKey.Delete) RemoveSelected();
        else if (!_watchTab && _result is null && e.Key == VirtualKey.R && _selectedJob is { } retry && _queue.Retry(retry)) _ = RunQueue(retry);
        else if (e.Key == VirtualKey.X && _result is { } result) _ = Export(result);
        else if (e.Key == VirtualKey.X && !_watchTab && _result is null) _ = ExportAllAsync();
        else if (e.Key == VirtualKey.R && _watchTab && !_watchEditing) _watcher.RetryFailures();
        else if (e.Key == VirtualKey.F && _watchTab && !_watchEditing && _watcher.Settings is { } settings) _ = OpenExportsAsync(settings.Output);
        else return;
        e.Handled = true;
    }

    private async Task OpenExportsAsync(string path)
    { try { await Launcher.LaunchFolderPathAsync(path); } catch (Exception) { _notice.Text = "The export folder could not be opened."; } }
}
