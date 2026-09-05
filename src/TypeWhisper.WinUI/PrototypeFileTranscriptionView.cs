using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Storage;

namespace TypeWhisper.WinUI;

public sealed class PrototypeFileTranscriptionView : UserControl
{
    private readonly PrototypeFileQueue _queue = new();
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _notice = Text("", 12, true);
    private readonly PrototypeBreadcrumbs _crumbs = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<Guid, Action> _updateRows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly ScrollViewer _scroll;
    private PrototypeFileJob? _result;
    private PrototypeChoicePicker? _formatPicker;
    private string _format = "txt";
    private bool _picking;
    internal event Action? ExitRequested;

    public PrototypeFileTranscriptionView()
    {
        var root = new Grid { Background = Brush("InkBrush"), RowSpacing = 10, Padding = new Thickness(24, 8, 24, 0) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = Text("File transcription", 22); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1); root.Children.Add(heading);
        _scroll = new ScrollViewer { Content = _body, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite); Grid.SetRow(_notice, 2); root.Children.Add(_notice);
        var footer = new Grid { MinHeight = 52, ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_crumbs); Grid.SetColumn(_actions, 1); footer.Children.Add(_actions);
        var border = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush("HairlineBrush") };
        Grid.SetRow(border, 3); root.Children.Add(border);
        Content = root;
        _timer.Tick += (_, _) =>
        {
            var statuses = string.Join(',', _queue.Jobs.Select(job => job.Status));
            _queue.Tick();
            if (_result is null && statuses != string.Join(',', _queue.Jobs.Select(job => job.Status))) Render();
            else foreach (var update in _updateRows.Values) update();
            if (!_queue.Running) { _timer.Stop(); _notice.Text = "Demo finished. Results are sample text, not transcripts of your files."; }
        };
        Unloaded += (_, _) => Stop();
        Render();
    }
    internal void Present() { _notice.Text = "Preview only · no media is read or uploaded."; Render(); }
    internal void Stop() { _timer.Stop(); if (_queue.Running) _queue.Cancel(); }
    internal void GoBack()
    {
        if (_picking) return;
        if (_formatPicker?.IsPopupOpen == true) { _formatPicker.ClosePopup(); return; }
        if (_result is not null) { _result = null; Render(); }
        else { Stop(); ExitRequested?.Invoke(); }
    }
    private void Render()
    {
        _body.Children.Clear(); _actions.Children.Clear(); _updateRows.Clear(); _formatPicker = null;
        _crumbs.SetItems(new("Quick Launch", () => { if (!_picking) { Stop(); ExitRequested?.Invoke(); } }),
            new("Files", _result is null ? null : () => { _result = null; Render(); }), new(_result is null ? "Queue" : "Result"));
        if (_result is not null) { RenderResult(_result); return; }
        var dropContent = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        dropContent.Children.Add(new TypeWhisperGlyph { Kind = "file", Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
        dropContent.Children.Add(Text("Drop audio or video files here", 15));
        var importActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var browse = Button("Choose files…", async () => await ChooseFiles()); browse.IsEnabled = !_queue.Running && !_picking; importActions.Children.Add(browse);
        var sample = Button("Try sample", () => { AddPaths(["Team meeting.wav", "Interview.mp4"]); }); sample.IsEnabled = !_queue.Running && !_picking; importActions.Children.Add(sample);
        dropContent.Children.Add(importActions);
        dropContent.Children.Add(Text("Audio & video · up to 20 files · simulation only", 11, true));
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
        _body.Children.Add(drop);
        if (_queue.Jobs.Count == 0) _body.Children.Add(Text("Choose your own file to test the flow, or try the sample queue without selecting anything.", 13, true));
        else
        {
            _body.Children.Add(Text($"{_queue.Jobs.Count} files · simulated transcription", 12, true));
            foreach (var job in _queue.Jobs) AddRow(job);
        }
        var errorDemo = Button("Try an error example", () => { var error = _queue.Add("Unreadable demo.wav", true); _notice.Text = error ?? "This sample deliberately fails halfway through. Original files are never opened."; Render(); });
        errorDemo.Style = (Style)Application.Current.Resources["PrototypeIconButtonStyle"]; errorDemo.IsEnabled = !_queue.Running && !_picking; _body.Children.Add(errorDemo);
        if (_queue.Running) _actions.Children.Add(Button("Cancel run", () => { Stop(); _notice.Text = "Run canceled. Completed results are kept; originals are unchanged."; Render(); }, destructive: true));
        else
        {
            var start = Button("Start demo", () => { if (_queue.Start()) { _timer.Start(); _notice.Text = "Simulating transcription — your files are not being processed."; Render(); } }, primary: true);
            start.IsEnabled = !_picking && _queue.Jobs.Any(job => job.Status == PrototypeFileStatus.Queued); _actions.Children.Add(start);
        }
    }
    private void AddRow(PrototypeFileJob job)
    {
        var row = new Grid { ColumnSpacing = 12 }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 6 }; var name = Text(job.Name, 13); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap; ToolTipService.SetToolTip(name, job.Name); labels.Children.Add(name);
        var status = Text("", 11, true); labels.Children.Add(status);
        var fill = new Border { Height = 2, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("AccentBrush") };
        var track = new Grid { Height = 2, Background = Brush("HairlineBrush") }; track.Children.Add(fill); labels.Children.Add(track);
        void Update() { status.Text = job.Status == PrototypeFileStatus.Failed ? "Demo error · simulated unreadable file" : job.Status == PrototypeFileStatus.Processing ? $"Simulating · {job.Progress}%" : job.Status == PrototypeFileStatus.Ready ? "Ready · sample transcript" : job.Status.ToString(); fill.Width = track.ActualWidth * job.Progress / 100; }
        track.SizeChanged += (_, _) => Update(); _updateRows[job.Id] = Update; Update(); row.Children.Add(labels);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (job.Status == PrototypeFileStatus.Ready) actions.Children.Add(Button("View result", () => { _result = job; _scroll.ChangeView(null, 0, null, true); Render(); }));
        if (job.Status is PrototypeFileStatus.Failed or PrototypeFileStatus.Canceled)
        {
            var retry = Button("Retry", () => { _queue.Retry(job); Render(); }); retry.IsEnabled = !_queue.Running; actions.Children.Add(retry);
        }
        var remove = Button("×", () => { _queue.Remove(job); _notice.Text = "Removed from this preview queue. The original file is unchanged."; Render(); }, destructive: true);
        AutomationProperties.SetName(remove, $"Remove {job.Name} from queue"); remove.IsEnabled = !_queue.Running && !_picking; actions.Children.Add(remove);
        Grid.SetColumn(actions, 1); row.Children.Add(actions);
        _body.Children.Add(new Border { Child = row, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Background = Brush("SurfaceBrush") });
    }
    private void RenderResult(PrototypeFileJob job)
    {
        _body.Children.Add(Text(job.Name, 16)); _body.Children.Add(Text("SAMPLE RESULT · Not generated from the selected media. Subtitle timings are fictional.", 12, true));
        _body.Children.Add(new Border { Child = new TextBlock { Text = job.Transcript, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Brush("TextBrush") }, Padding = new Thickness(18), Background = Brush("SurfaceBrush"), CornerRadius = new CornerRadius(10) });
        _formatPicker = new PrototypeChoicePicker(); _formatPicker.Configure("Export format", "file", "File export format");
        _formatPicker.SetOptions([new("txt", "Plain text · TXT", "Sample transcript"), new("srt", "Subtitles · SRT", "Sample timestamps"), new("vtt", "Subtitles · WebVTT", "Sample timestamps")], _format);
        _formatPicker.SelectionChanged += selected => _format = selected; _body.Children.Add(_formatPicker);
        _actions.Children.Add(Button("Export sample…", async () => await Export(job), primary: true));
    }
    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0; var errors = new List<string>();
        foreach (var path in paths) { var error = _queue.Add(path); if (error is null) added++; else errors.Add(error); }
        _notice.Text = $"{added} file(s) added. " + string.Join(' ', errors.Distinct()); Render();
    }
    private async Task ChooseFiles()
    {
        if (_picking || _queue.Running || XamlRoot is null) return;
        _picking = true;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = "Choose audio or video files" };
            foreach (var extension in PrototypeFileQueue.Extensions) picker.FileTypeFilter.Add(extension);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) AddPaths(files.Select(file => file.Path)); else _notice.Text = "Selection canceled. Your queue is unchanged.";
        }
        catch (Exception) { _notice.Text = "The file dialog could not be opened. Try dropping a file or using the sample queue."; }
        finally { _picking = false; Render(); }
    }
    private async Task Export(PrototypeFileJob job)
    {
        if (_picking || XamlRoot is null) return;
        _picking = true; var format = _format;
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = Path.GetFileNameWithoutExtension(job.Name) + ".demo", Title = "Export sample transcript" };
            picker.FileTypeChoices.Add(format.ToUpperInvariant(), new List<string> { "." + format });
            var file = await picker.PickSaveFileAsync();
            if (file is null) { _notice.Text = "Export canceled. Nothing was written."; return; }
            // CreateNew deliberately refuses overwriting any existing file in this preview.
            await using var stream = new FileStream(file.Path, FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(PrototypeFileQueue.Export(job, format));
            _notice.Text = "Sample exported. Original media is unchanged.";
        }
        catch (IOException) { _notice.Text = "Could not save. Choose a new filename in a writable folder; this preview does not overwrite files."; }
        catch (Exception) { _notice.Text = "Export could not be completed. Your result is still available."; }
        finally { _picking = false; }
    }
    private static HandCursorButton Button(string text, Action action, bool primary = false, bool destructive = false)
    {
        var button = new HandCursorButton { Content = text, MinHeight = 34, Style = (Style)Application.Current.Resources[destructive ? "PrototypeDestructiveButtonStyle" : primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
        button.Click += (_, _) => action(); return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
