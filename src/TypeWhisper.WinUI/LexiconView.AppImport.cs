using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace TypeWhisper.WinUI;

public sealed partial class LexiconView
{
    private ContentDialog? _appImportDialog;

    private ContentDialog ImportDialog(string title, object content, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = title, Content = content,
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogBackground"] = Brush("InkBrush");
        dialog.Resources["ContentDialogTopOverlay"] = Brush("InkBrush");
        return _appImportDialog = dialog;
    }

    private async Task ImportFromAppAsync()
    {
        if (_closing || _transferCompletion is { Task.IsCompleted: false } || _trainingTask is { IsCompleted: false }) return;
        var completion = _transferCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var snippets = _kind == LexiconKind.Snippet;
        try
        {
            var apps = snippets ? new[] { LexiconImportApp.WisprFlow } : [LexiconImportApp.WisprFlow, LexiconImportApp.Handy];
            var source = new ComboBox { ItemsSource = apps.Select(LexiconAppImport.Name).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(source, "Import source");
            var location = Text("", 12, true);
            var browse = new CheckBox { Content = "Choose a file instead" };
            void UpdateSource()
            {
                var app = apps[source.SelectedIndex];
                var found = File.Exists(LexiconAppImport.DefaultPath(app));
                location.Text = found ? $"{LexiconAppImport.Name(app)} data found on this PC." : "No data found in the default location. Choose the source file to continue.";
                browse.IsChecked = !found;
            }
            source.SelectionChanged += (_, _) => UpdateSource();
            UpdateSource();
            var body = new StackPanel { Spacing = 12, MaxWidth = 480 };
            body.Children.Add(Text(snippets ? "Bring your Wispr Flow snippets into TypeWhisper." : "Bring your words and alternate spellings into TypeWhisper.", 14));
            body.Children.Add(source); body.Children.Add(location); body.Children.Add(browse);
            body.Children.Add(Text("Review every entry before adding it. Existing entries and the source app's data stay unchanged.", 12, true));
            body.Children.Add(Text(snippets ? "Handy word import is available in Words. Snippets containing TypeWhisper placeholders are excluded to preserve their original meaning."
                : "Wispr Flow: flow.sqlite · Handy: settings_store.json. Handy imports words only.", 12, true));
            var choose = ImportDialog("Import from another app", body, "Review entries");
            if (await choose.ShowAsync() != ContentDialogResult.Primary || _closing) return;
            var selectedApp = apps[source.SelectedIndex];
            var path = LexiconAppImport.DefaultPath(selectedApp);
            if (browse.IsChecked == true)
            {
                var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { Title = selectedApp == LexiconImportApp.WisprFlow ? "Choose Wispr Flow's flow.sqlite" : "Choose Handy's settings_store.json" };
                foreach (var extension in selectedApp == LexiconImportApp.WisprFlow ? new[] { ".sqlite", ".db" } : [".json"])
                    picker.FileTypeFilter.Add(extension);
                var operation = picker.PickSingleFileAsync();
                _cancelPicker = () => operation.Cancel();
                var file = await operation;
                _cancelPicker = null;
                if (file is null || _closing) return;
                path = file.Path;
            }

            IsEnabled = false;
            _notice.Text = $"Reading {LexiconAppImport.Name(selectedApp)} entries…";
            var batch = await Task.Run(() => LexiconAppImport.Load(selectedApp, path, snippets));
            if (_closing) return;
            var review = await Task.Run(() => _store.ReviewAppImport(batch, snippets));
            if (_closing) return;
            IsEnabled = true;
            _notice.Text = "Review the imported entries before adding them.";
            var reviewBody = new StackPanel { Spacing = 12, MaxWidth = 520 };
            reviewBody.Children.Add(Text(review.Summary, 14));
            reviewBody.Children.Add(Text("Only new entries will be added. Conflicts keep your existing spelling or snippet. Deleted entries and entries for the other section are excluded.", 12, true));
            if (review.Lines.Count > 0)
            {
                var list = new ListView { Height = 300, SelectionMode = ListViewSelectionMode.Single,
                    ItemsSource = review.Lines.Select(line => $"{Outcome(line.Outcome)} · {line.Kind}: {line.Key}").ToArray() };
                AutomationProperties.SetName(list, "Import preview entries");
                var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
                AutomationProperties.SetName(details, "Selected entry content");
                list.SelectionChanged += (_, _) =>
                {
                    if (list.SelectedIndex >= 0)
                    {
                        var line = review.Lines[list.SelectedIndex];
                        details.Text = line.Value is null ? line.Key : line.Key + "\n→\n" + line.Value;
                    }
                };
                list.SelectedIndex = 0;
                reviewBody.Children.Add(list); reviewBody.Children.Add(details);
            }
            else reviewBody.Children.Add(Text("No entries for this section were found.", 13, true));
            var confirm = ImportDialog("Review " + LexiconAppImport.Name(selectedApp) + " import",
                new ScrollViewer { Content = reviewBody, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                $"Add {review.Additions} entries");
            confirm.IsPrimaryButtonEnabled = review.Additions > 0;
            if (await confirm.ShowAsync() != ContentDialogResult.Primary || _closing)
            { _notice.Text = "Import canceled. Nothing was changed."; return; }
            var error = _store.CommitAppImport(review);
            if (error is not null) { _notice.Text = error; return; }
            Render();
            _notice.Text = $"Added {review.Additions} entries from {LexiconAppImport.Name(selectedApp)}. Saved for the next dictation.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException
            or ArgumentException or OperationCanceledException or DllNotFoundException or EntryPointNotFoundException)
        { _notice.Text = "Import canceled: " + ex.Message; }
        finally
        {
            _appImportDialog = null;
            _cancelPicker = null;
            IsEnabled = !_closing;
            completion.TrySetResult();
        }
    }

    private static string Outcome(AppImportOutcome outcome) => outcome switch
    {
        AppImportOutcome.Add => "Add", AppImportOutcome.Duplicate => "Already present",
        AppImportOutcome.Conflict => "Conflict — keep existing", _ => "Unsupported — excluded"
    };
}
