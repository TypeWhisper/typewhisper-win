using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Core.Models.Backup;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class PrototypeSyncBackupView : UserControl
{
    private readonly PersistedProfileBackup _store = new(WinUIProfile.Root);
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly StackPanel _selection = new() { Spacing = 8 };
    private readonly StackPanel _review = new() { Spacing = 10 };
    private readonly TextBlock _notice = Copy("Choose the categories to include. History contains saved text only.", 13, true);
    private readonly HandCursorButton _export;
    private readonly HandCursorButton _import;
    private readonly CancellationTokenSource _lifetime = new();
    private BackupCategory _categories = BackupCategory.Dictionary | BackupCategory.Snippets | BackupCategory.Workflows;
    private PersistedProfileBackupPreview? _preview;
    private Func<PersistedProfileBackup, PersistedProfileBackupPreview, Task>? _restore;
    private bool _busy;
    private bool _unloaded;
    private ContentDialog? _dialog;

    internal PrototypeSyncBackupView(Dictionary<string, string> values)
    {
        Content = _body;
        _body.Children.Add(Copy("Local backup", 22));
        _body.Children.Add(Copy("Save a portable JSON file or merge data from an existing TypeWhisper backup.", 13, true));
        foreach (var (category, label) in new[]
        {
            (BackupCategory.Dictionary, "Dictionary"), (BackupCategory.Snippets, "Snippets"),
            (BackupCategory.Workflows, "Workflows"), (BackupCategory.History, "History (text only)")
        })
        {
            var toggle = new CheckBox { Content = label, IsChecked = _categories.HasFlag(category) };
            AutomationProperties.SetName(toggle, "Include " + label + " in backup");
            toggle.Checked += (_, _) => { _categories |= category; InvalidatePreview(); };
            toggle.Unchecked += (_, _) => { _categories &= ~category; InvalidatePreview(); };
            _selection.Children.Add(toggle);
        }
        _body.Children.Add(_selection);
        _body.Children.Add(Copy("Audio, model files, API keys, licenses, plugin installation and device preferences are excluded. Keep exported files somewhere you trust.", 12, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _export = Button("Export backup…", () => RunAsync(ExportAsync));
        _import = Button("Choose backup to restore…", () => RunAsync(PreviewAsync));
        actions.Children.Add(_export); actions.Children.Add(_import); _body.Children.Add(actions);
        AutomationProperties.SetLiveSetting(_notice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _body.Children.Add(_notice); _body.Children.Add(_review);
        _body.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new(0, 8, 0, 8) });
        _body.Children.Add(Copy("Sync", 18));
        _body.Children.Add(Copy("Sync between devices is not connected yet. Use a local backup to transfer the categories above.", 13, true));
        Unloaded += (_, _) => { _unloaded = true; _lifetime.Cancel(); _dialog?.Hide(); };
        UpdateButtons();
    }

    internal void ConnectRestore(Func<PersistedProfileBackup, PersistedProfileBackupPreview, Task>? restore)
    { _restore = restore; UpdateButtons(); }

    internal bool ClosePreview()
    {
        if (_busy) return true;
        if (_preview is null) return false;
        InvalidatePreview(); _import.Focus(FocusState.Keyboard); return true;
    }

    private void InvalidatePreview()
    { _preview = null; _review.Children.Clear(); UpdateButtons(); }

    private void UpdateButtons()
    {
        if (_export is null) return;
        foreach (var control in _selection.Children.OfType<Control>()) control.IsEnabled = !_busy;
        _export.IsEnabled = !_busy && _categories != BackupCategory.None;
        _import.IsEnabled = !_busy && _categories != BackupCategory.None && _restore is not null;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || _unloaded) return;
        _busy = true; UpdateButtons();
        try { await operation(); }
        catch (OperationCanceledException) { if (!_unloaded) _notice.Text = "Operation canceled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_unloaded) _notice.Text = "Backup operation failed: " + ex.Message; }
        finally { _busy = false; if (!_unloaded) UpdateButtons(); }
    }

    private async Task ExportAsync()
    {
        InvalidatePreview();
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { Title = "Export TypeWhisper backup", SuggestedFileName = "typewhisper-backup-" + DateTime.Now.ToString("yyyy-MM-dd") };
        picker.FileTypeChoices.Add("TypeWhisper backup", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (file is null) { _notice.Text = "Export canceled."; return; }
        var json = await _store.ExportAsync(_categories, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        LexiconTransfer.WriteFile(file.Path, json);
        _notice.Text = "Backup saved to " + file.Path;
    }

    private async Task PreviewAsync()
    {
        InvalidatePreview();
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { Title = "Choose a TypeWhisper backup" };
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (file is null) { _notice.Text = "Restore canceled."; return; }
        await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("Backups are limited to 64 MB.");
        using var reader = new StreamReader(input);
        var json = await reader.ReadToEndAsync(_lifetime.Token);
        var preview = await _store.PreviewAsync(json, _categories, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        _preview = preview;
        _notice.Text = "Review the merge from " + Path.GetFileName(file.Path) + ". No profile data has changed.";
        _review.Children.Add(Copy("Restore preview", 18));
        foreach (var pair in preview.Merge.Categories)
            _review.Children.Add(Copy($"{pair.Key}: {pair.Value.Imported} to add · {pair.Value.Skipped} skipped · {pair.Value.Conflicts} conflicts", 13));
        foreach (var warning in preview.Merge.Warnings) _review.Children.Add(Copy(warning, 12, true));
        _review.Children.Add(Copy($"{preview.ChangedFileCount} profile {(preview.ChangedFileCount == 1 ? "file" : "files")} will change. Existing items are kept according to the merge rules. The app closes after restoring; reopen it to use the restored data.", 13, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Cancel restore", () => { InvalidatePreview(); _notice.Text = "Restore canceled. No data changed."; return Task.CompletedTask; }));
        actions.Children.Add(Button("Restore and close app…", () => RunAsync(ConfirmRestoreAsync), primary: true));
        _review.Children.Add(actions);
    }

    private async Task ConfirmRestoreAsync()
    {
        var preview = _preview;
        if (preview is null || _restore is null) return;
        _dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Restore and close TypeWhisper?",
            Content = $"Apply the reviewed merge to {preview.ChangedFileCount} profile {(preview.ChangedFileCount == 1 ? "file" : "files")}. Active work will stop. Reopen TypeWhisper after it closes.",
            PrimaryButtonText = "Restore and close app", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult choice;
        try { choice = await _dialog.ShowAsync(); }
        finally { _dialog = null; }
        if (_unloaded || choice != ContentDialogResult.Primary) return;
        await _restore(_store, preview);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    { Text = text, FontSize = size, Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap };
    private static HandCursorButton Button(string label, Func<Task> action, bool primary = false)
    {
        var button = new HandCursorButton { Content = label, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources[primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await action();
        return button;
    }
}
