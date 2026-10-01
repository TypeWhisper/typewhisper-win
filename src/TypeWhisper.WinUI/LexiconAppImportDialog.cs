using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace TypeWhisper.WinUI;

// One review and commit flow, shared by the dictionary workspace and setup.
internal sealed class LexiconAppImportDialog(Control owner, Action<string> report)
{
    private ContentDialog? _dialog;
    private Action? _cancelPicker;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;
    private bool IsCanceled => _cancellation.IsCancellationRequested || !owner.IsLoaded;

    internal void Cancel()
    {
        if (_finished) return;
        _cancellation.Cancel();
        _dialog?.Hide();
        try { _cancelPicker?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Import picker cancellation failed: " + ex); }
    }

    private ContentDialog ImportDialog(string title, object content, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot, RequestedTheme = owner.ActualTheme, Title = title, Content = content,
            PrimaryButtonText = primary, CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogBackground"] = Brush("InkBrush");
        dialog.Resources["ContentDialogTopOverlay"] = Brush("InkBrush");
        return _dialog = dialog;
    }

    internal async Task<string?> ShowAsync(bool snippets, LexiconImportApp? preferredApp = null, bool chooseDestination = false)
    {
        if (IsCanceled) return null;
        var wasEnabled = owner.IsEnabled;
        var store = new Lexicon(DictationDictionarySnapshot.StoragePath, DictationSnippetSnapshot.StoragePath);
        try
        {
            var apps = snippets && !chooseDestination ? new[] { LexiconImportApp.WisprFlow } : [LexiconImportApp.WisprFlow, LexiconImportApp.Handy];
            var source = new ComboBox { ItemsSource = apps.Select(LexiconAppImport.Name).ToArray(),
                SelectedIndex = Math.Max(0, Array.IndexOf(apps, preferredApp ?? LexiconImportApp.WisprFlow)), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(source, Loc.T("Import source"));
            var destination = new ComboBox { ItemsSource = new[] { Loc.T("Words and corrections"), Loc.T("Snippets") },
                SelectedIndex = snippets ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(destination, Loc.T("Import content"));
            var location = Text("", 12, true);
            var browse = new CheckBox { Content = Loc.T("Choose a file instead") };
            void UpdateSource()
            {
                var app = apps[source.SelectedIndex];
                var found = File.Exists(LexiconAppImport.DefaultPath(app));
                location.Text = found ? Loc.T("{0} data found on this PC.", LexiconAppImport.Name(app)) : Loc.T("No data found in the default location. Choose the source file to continue.");
                browse.IsChecked = !found;
                destination.IsEnabled = app == LexiconImportApp.WisprFlow;
                if (!destination.IsEnabled) destination.SelectedIndex = 0;
            }
            source.SelectionChanged += (_, _) => UpdateSource();
            UpdateSource();
            var body = new StackPanel { Spacing = 12, MaxWidth = 480 };
            body.Children.Add(Text(chooseDestination ? Loc.T("Choose what to bring into TypeWhisper.")
                : snippets ? Loc.T("Bring your Wispr Flow snippets into TypeWhisper.") : Loc.T("Bring your words and alternate spellings into TypeWhisper."), 14));
            body.Children.Add(source);
            if (chooseDestination) { body.Children.Add(Text(Loc.T("Import"), 12, true)); body.Children.Add(destination); }
            body.Children.Add(location); body.Children.Add(browse);
            body.Children.Add(Text(Loc.T("Review every entry before adding it. Existing entries and the source app's data stay unchanged."), 12, true));
            body.Children.Add(Text(snippets && !chooseDestination ? Loc.T("Handy word import is available in Words. Snippets containing TypeWhisper placeholders are excluded to preserve their original meaning.")
                : Loc.T("Wispr Flow: flow.sqlite · Handy: settings_store.json. Handy imports words only."), 12, true));
            var choose = ImportDialog(Loc.T("Import from another app"), body, Loc.T("Review entries"));
            if (await choose.ShowAsync() != ContentDialogResult.Primary || IsCanceled) return null;
            if (chooseDestination) snippets = destination.SelectedIndex == 1;
            var selectedApp = apps[source.SelectedIndex];
            var path = LexiconAppImport.DefaultPath(selectedApp);
            if (browse.IsChecked == true)
            {
                var picker = new FileOpenPicker(owner.XamlRoot.ContentIslandEnvironment.AppWindowId)
                { Title = selectedApp == LexiconImportApp.WisprFlow ? Loc.T("Choose Wispr Flow's flow.sqlite") : Loc.T("Choose Handy's settings_store.json") };
                foreach (var extension in selectedApp == LexiconImportApp.WisprFlow ? new[] { ".sqlite", ".db" } : [".json"])
                    picker.FileTypeFilter.Add(extension);
                var operation = picker.PickSingleFileAsync();
                _cancelPicker = () => operation.Cancel();
                var file = await operation;
                _cancelPicker = null;
                if (file is null || IsCanceled) return null;
                path = file.Path;
            }

            owner.IsEnabled = false;
            report(Loc.T("Reading {0} entries…", LexiconAppImport.Name(selectedApp)));
            var batch = await Task.Run(() => LexiconAppImport.Load(selectedApp, path, snippets, _cancellation.Token));
            if (IsCanceled) return null;
            var review = await Task.Run(() => store.ReviewAppImport(batch, snippets, _cancellation.Token));
            if (IsCanceled) return null;
            owner.IsEnabled = true;
            report(Loc.T("Review the imported entries before adding them."));
            var reviewBody = new StackPanel { Spacing = 12, MaxWidth = 520 };
            reviewBody.Children.Add(Text(review.Summary, 14));
            reviewBody.Children.Add(Text(Loc.T("Only new entries will be added. Conflicts keep your existing spelling or snippet. Deleted entries and entries for the other section are excluded."), 12, true));
            if (review.Lines.Count > 0)
            {
                var list = new ListView { Height = 300, SelectionMode = ListViewSelectionMode.Single,
                    ItemsSource = review.Lines.Select(line => $"{Outcome(line.Outcome)} · {line.Kind}: {line.Key}").ToArray() };
                AutomationProperties.SetName(list, Loc.T("Import preview entries"));
                var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
                AutomationProperties.SetName(details, Loc.T("Selected entry content"));
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
            else reviewBody.Children.Add(Text(Loc.T("No entries for this section were found."), 13, true));
            var confirm = ImportDialog(Loc.T("Review {0} import", LexiconAppImport.Name(selectedApp)),
                new ScrollViewer { Content = reviewBody, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                Loc.T("Add {0} entries", review.Additions));
            confirm.IsPrimaryButtonEnabled = review.Additions > 0;
            if (await confirm.ShowAsync() != ContentDialogResult.Primary || IsCanceled) return null;
            var error = store.CommitAppImport(review);
            return error ?? Loc.T("Added {0} entries from {1}. Saved for the next dictation.", review.Additions, LexiconAppImport.Name(selectedApp));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException
            or ArgumentException or OperationCanceledException or DllNotFoundException or EntryPointNotFoundException)
        { return Loc.T("Import canceled: {0}", ex.Message); }
        finally
        {
            _dialog = null;
            _cancelPicker = null;
            if (owner.IsLoaded) owner.IsEnabled = wasEnabled;
            _finished = true;
            _cancellation.Dispose();
        }
    }

    private static string Outcome(AppImportOutcome outcome) => outcome switch
    {
        AppImportOutcome.Add => Loc.T("Add"), AppImportOutcome.Duplicate => Loc.T("Already present"),
        AppImportOutcome.Conflict => Loc.T("Conflict — keep existing"), _ => Loc.T("Unsupported — excluded")
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Text(string text, double size, bool muted = false) => new()
    { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
