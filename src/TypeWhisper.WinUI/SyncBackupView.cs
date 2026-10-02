using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Core.Models.Backup;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.UserData;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class SyncBackupView : UserControl
{
    private readonly PersistedProfileBackup _store = new(WinUIProfile.Root);
    private readonly StackPanel _body = new() { Spacing = 20 };
    private readonly StackPanel _selection = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _review = new() { Spacing = 10 };
    private readonly TextBlock _notice = Copy(Loc.T("Choose the categories to include. History contains saved text only."), 13, true);
    private readonly HandCursorButton _export;
    private readonly HandCursorButton _import;
    private readonly HandCursorButton _exportAll;
    private readonly HandCursorButton _deleteAll;
    private readonly TextBlock _dataNotice = Copy(Loc.T("Everything TypeWhisper keeps on this PC, in one .zip file. API keys, licenses, downloaded models and installed plugins are left out."), 13, true);
    private readonly CancellationTokenSource _lifetime = new();
    private BackupCategory _categories = BackupCategory.Dictionary | BackupCategory.Snippets | BackupCategory.Workflows;
    private PersistedProfileBackupPreview? _preview;
    private Func<PersistedProfileBackup, PersistedProfileBackupPreview, Task>? _restore;
    private Func<Task>? _deleteAllData;
    private bool _busy;
    private bool _unloaded;
    private ContentDialog? _dialog;

    internal StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    internal SyncBackupView()
    {
        Content = _body;
        foreach (var (category, label) in new[]
        {
            (BackupCategory.Dictionary, Loc.T("Dictionary")), (BackupCategory.Snippets, Loc.T("Snippets")),
            (BackupCategory.Workflows, Loc.T("Workflows")), (BackupCategory.History, Loc.T("History (text only)"))
        })
        {
            var toggle = new CheckBox { Content = label, IsChecked = _categories.HasFlag(category), MinWidth = 0, Margin = new Thickness(0, 0, 12, 0) };
            AutomationProperties.SetName(toggle, Loc.T("Include {0} in backup", label));
            toggle.Checked += (_, _) => { _categories |= category; InvalidatePreview(); };
            toggle.Unchecked += (_, _) => { _categories &= ~category; InvalidatePreview(); };
            _selection.Children.Add(toggle);
        }
        _export = Button(Loc.T("Export backup…"), () => RunAsync(ExportAsync));
        _import = Button(Loc.T("Choose backup to restore…"), () => RunAsync(PreviewAsync));
        Actions.Children.Add(_export); Actions.Children.Add(_import);
        AutomationProperties.SetLiveSetting(_notice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var backup = new SettingsCard(Loc.T("Local backup"), Loc.T("Save a portable JSON file or merge data from an existing TypeWhisper backup. Audio, model files, API keys, licenses, plugin installation and device preferences are excluded."));
        backup.Add(Block(_selection, _notice, _review, Actions));
        _body.Children.Add(backup);
        _body.Children.Add(new CloudSyncView());
        _exportAll = Button(Loc.T("Export all data…"), () => RunAsync(ExportAllAsync, _dataNotice, Loc.T("Export failed: {0}")));
        _deleteAll = Button(Loc.T("Delete all data…"), () => RunAsync(ConfirmDeleteAllAsync, _dataNotice, Loc.T("Delete all data failed: {0}")), "DestructiveButtonStyle");
        AutomationProperties.SetLiveSetting(_dataNotice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var data = new SettingsCard(Loc.T("All data"), Loc.T("Take a complete copy of your TypeWhisper data with you, or remove everything TypeWhisper stores on this PC and start fresh."));
        data.Add(Block(_dataNotice, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _exportAll, _deleteAll } }));
        _body.Children.Add(data);
        Unloaded += (_, _) => { _unloaded = true; _lifetime.Cancel(); _dialog?.Hide(); };
        UpdateButtons();
    }

    internal void ConnectRestore(Func<PersistedProfileBackup, PersistedProfileBackupPreview, Task>? restore)
    { _restore = restore; UpdateButtons(); }

    internal void ConnectDeleteAllData(Func<Task>? deleteAllData)
    { _deleteAllData = deleteAllData; UpdateButtons(); }

    internal bool ClosePreview()
    {
        if (_busy) return true;
        if (_preview is null) return false;
        InvalidatePreview(); _import.Focus(FocusState.Keyboard); return true;
    }

    private void InvalidatePreview()
    { _preview = null; _review.Children.Clear(); Actions.Children.Clear(); Actions.Children.Add(_export); Actions.Children.Add(_import); UpdateButtons(); }

    private void UpdateButtons()
    {
        if (_export is null) return;
        foreach (var button in Actions.Children.OfType<Button>()) button.IsEnabled = !_busy;
        foreach (var control in _selection.Children.OfType<Control>()) control.IsEnabled = !_busy;
        _export.IsEnabled = !_busy && _categories != BackupCategory.None;
        _import.IsEnabled = !_busy && _categories != BackupCategory.None && _restore is not null;
        if (_exportAll is null) return;
        _exportAll.IsEnabled = !_busy;
        _deleteAll.IsEnabled = !_busy && _deleteAllData is not null;
    }

    private async Task RunAsync(Func<Task> operation, TextBlock? notice = null, string? failure = null)
    {
        if (_busy || _unloaded) return;
        notice ??= _notice;
        _busy = true; UpdateButtons();
        try { await operation(); }
        catch (OperationCanceledException) { if (!_unloaded) notice.Text = Loc.T("Operation canceled."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_unloaded) notice.Text = string.Format(failure ?? Loc.T("Backup operation failed: {0}"), ex.Message); }
        finally { _busy = false; if (!_unloaded) UpdateButtons(); }
    }

    private async Task ExportAsync()
    {
        InvalidatePreview();
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { Title = Loc.T("Export TypeWhisper backup"), SuggestedFileName = "typewhisper-backup-" + DateTime.Now.ToString("yyyy-MM-dd") };
        picker.FileTypeChoices.Add(Loc.T("TypeWhisper backup"), new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (file is null) { _notice.Text = Loc.T("Export canceled."); return; }
        var json = await _store.ExportAsync(_categories, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        LexiconTransfer.WriteFile(file.Path, json);
        _notice.Text = Loc.T("Backup saved to {0}", file.Path);
    }

    private async Task PreviewAsync()
    {
        InvalidatePreview();
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { Title = Loc.T("Choose a TypeWhisper backup") };
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (file is null) { _notice.Text = Loc.T("Restore canceled."); return; }
        await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException(Loc.T("Backups are limited to 64 MB."));
        using var reader = new StreamReader(input);
        var json = await reader.ReadToEndAsync(_lifetime.Token);
        var preview = await _store.PreviewAsync(json, _categories, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        _preview = preview;
        _notice.Text = Loc.T("Review the merge from {0}. No profile data has changed.", Path.GetFileName(file.Path));
        _review.Children.Add(Copy(Loc.T("Restore preview"), 18));
        foreach (var pair in preview.Merge.Categories)
            _review.Children.Add(Copy(Loc.T("{0}: {1} to add · {2} skipped · {3} conflicts", pair.Key, pair.Value.Imported, pair.Value.Skipped, pair.Value.Conflicts), 13));
        foreach (var warning in preview.Merge.Warnings) _review.Children.Add(Copy(warning, 12, true));
        _review.Children.Add(Copy(preview.ChangedFileCount == 1 ? Loc.T("{0} profile file will change. Existing items are kept according to the merge rules. The app closes after restoring; reopen it to use the restored data.", preview.ChangedFileCount) : Loc.T("{0} profile files will change. Existing items are kept according to the merge rules. The app closes after restoring; reopen it to use the restored data.", preview.ChangedFileCount), 13, true));
        Actions.Children.Clear();
        Actions.Children.Add(Button(Loc.T("Cancel restore"), () => { InvalidatePreview(); _notice.Text = Loc.T("Restore canceled. No data changed."); return Task.CompletedTask; }));
        Actions.Children.Add(Button(Loc.T("Restore and close app…"), () => RunAsync(ConfirmRestoreAsync), primary: true));

    }

    private async Task ConfirmRestoreAsync()
    {
        var preview = _preview;
        if (preview is null || _restore is null) return;
        _dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = Loc.T("Restore and close TypeWhisper?"),
            Content = preview.ChangedFileCount == 1 ? Loc.T("Apply the reviewed merge to {0} profile file. Active work will stop. Reopen TypeWhisper after it closes.", preview.ChangedFileCount) : Loc.T("Apply the reviewed merge to {0} profile files. Active work will stop. Reopen TypeWhisper after it closes.", preview.ChangedFileCount),
            PrimaryButtonText = Loc.T("Restore and close app"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult choice;
        try { choice = await _dialog.ShowAsync(); }
        finally { _dialog = null; }
        if (_unloaded || choice != ContentDialogResult.Primary) return;
        await _restore(_store, preview);
    }

    private async Task ExportAllAsync()
    {
        InvalidatePreview();
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { Title = Loc.T("Export all TypeWhisper data"), SuggestedFileName = "typewhisper-data-" + DateTime.Now.ToString("yyyy-MM-dd") };
        picker.FileTypeChoices.Add(Loc.T("ZIP archive"), new List<string> { ".zip" });
        var file = await picker.PickSaveFileAsync();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (file is null) { _dataNotice.Text = Loc.T("Export canceled."); return; }
        _dataNotice.Text = Loc.T("Exporting your data…");
        var progress = new Progress<UserDataExportProgress>(value =>
        { if (!_unloaded) _dataNotice.Text = Loc.T("Exporting your data… {0:N0} files, {1}", value.Files, Size(value.Bytes)); });
        var result = await Task.Run(() => UserDataExport.ExportAsync(WinUIProfile.Root, file.Path, UserDataDeletion.PreviousVersionData, progress, _lifetime.Token));
        _lifetime.Token.ThrowIfCancellationRequested();
        var summary = Loc.T("Exported {0:N0} files ({1}) to {2}.", result.Files, Size(result.Bytes), file.Path);
        if (result.Skipped.Count > 0)
            summary += " " + (result.Skipped.Count == 1 ? Loc.T("{0:N0} item was in use or unreadable and are missing from the export; README.txt in the export lists them.", result.Skipped.Count) : Loc.T("{0:N0} items were in use or unreadable and are missing from the export; README.txt in the export lists them.", result.Skipped.Count));
        if (!result.IncludesBackup)
            summary += " " + (result.Skipped.Count == 0
                ? Loc.T("The restorable backup file could not be created, but the copy of your data folder is complete.")
                : Loc.T("The restorable backup file could not be created either."));
        _dataNotice.Text = summary;
    }

    private async Task ConfirmDeleteAllAsync()
    {
        if (_deleteAllData is null) return;
        InvalidatePreview();
        var understood = new CheckBox { Content = Loc.T("I understand that this cannot be undone") };
        var content = new StackPanel { Spacing = 12, MaxWidth = 460 };
        content.Children.Add(Copy(Loc.T("This permanently deletes everything TypeWhisper stores on this PC: history and its audio, recordings, dictionary, snippets, workflows, settings, plugin settings and API keys, downloaded models, and the license and account sign-in saved on this PC."), 14));
        content.Children.Add(Copy(Loc.T("Use Export all data first if you want to keep a copy. Files in your cloud sync folder, your TypeWhisper account and your license activations are not changed; to move a license to another PC, use Deactivate this device under Premium first."), 13, true));
        content.Children.Add(Copy(Loc.T("Active work stops, Start with Windows is turned off and the command line tool installed from this profile is removed, then TypeWhisper restarts and opens setup like a new installation."), 13, true));
        content.Children.Add(understood);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = Loc.T("Delete all TypeWhisper data?"), Content = content,
            PrimaryButtonText = Loc.T("Delete everything and restart"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false, PrimaryButtonStyle = (Style)Application.Current.Resources["DestructiveConfirmButtonStyle"]
        };
        understood.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        understood.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        _dialog = dialog;
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        finally { _dialog = null; }
        if (_unloaded) return;
        if (choice != ContentDialogResult.Primary || understood.IsChecked != true) { _dataNotice.Text = Loc.T("Nothing was deleted."); return; }
        await _deleteAllData();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024.0):0} KB"
    };

    // The content of a card under its heading.
    private static StackPanel Block(params UIElement[] children)
    {
        var block = new StackPanel { Spacing = 12, Padding = new Thickness(0, 14, 0, 14) };
        foreach (var child in children) block.Children.Add(child);
        return block;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    { Text = text, FontSize = size, Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap };
    private static HandCursorButton Button(string label, Func<Task> action, bool primary = false) =>
        Button(label, action, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");

    private static HandCursorButton Button(string label, Func<Task> action, string style)
    {
        var button = new HandCursorButton { Content = label, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources[style] };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await action();
        return button;
    }
}
