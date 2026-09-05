using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace TypeWhisper.WinUI;

// Interaction study only: selections live in the settings session; no sync/backup I/O.
internal sealed class PrototypeSyncBackupView : UserControl
{
    private readonly Dictionary<string, string> _values;
    private readonly StackPanel _body = new() { Spacing = 24 };
    private readonly TextBlock _status = Copy("Sync is off", 18);
    private readonly TextBlock _detail = Copy("Your data stays on this device.", 13, true);
    private readonly TextBlock _folder = Copy("No folder selected", 13, true);
    private readonly StackPanel _sharing = new() { Spacing = 16 };
    private readonly StackPanel _devices = new() { Spacing = 16 };
    private readonly StackPanel _backup = new() { Spacing = 16 };
    private bool _offline;
    private bool _backupOpen;
    internal bool ClosePreview()
    {
        if (!_backupOpen) return false;
        ShowBackupHome();
        return true;
    }
    private bool On(string key) => _values.GetValueOrDefault("PreviewSync." + key) == "On";
    private void Set(string key, bool value) => _values["PreviewSync." + key] = value ? "On" : "Off";

    internal PrototypeSyncBackupView(Dictionary<string, string> values)
    {
        _values = values;
        Content = _body;
        _body.Children.Add(Copy("Across your devices. Safely in your hands.", 14, true));
        var sync = new StackPanel { Spacing = 18 };
        var toggle = PrototypeToggleSwitch.Create(On("Enabled"));
        AutomationProperties.SetName(toggle, "Simulate cloud folder sync");
        toggle.Toggled += (_, _) => { Set("Enabled", toggle.IsOn); UpdateSync(); };
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(_status); heading.Children.Add(_detail);
        sync.Children.Add(Row("devices", heading, toggle));
        sync.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush") });
        var folderButton = Button("Choose folder…", async () =>
        {
            try
            {
                var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { Title = "Choose a sync folder for the preview", CommitButtonText = "Choose folder" };
                var result = await picker.PickSingleFolderAsync();
                if (result is not null) _values["CloudFolderSyncFolderPath"] = result.Path;
                UpdateSync();
            }
            catch { _detail.Text = "Folder picker could not open. No changes were made; try again."; }
        });
        var folderText = new StackPanel { Spacing = 5, Tag = "CloudFolderSyncFolderPath" };
        folderText.Children.Add(Copy("Private cloud folder", 14)); folderText.Children.Add(_folder);
        sync.Children.Add(Row("folder", folderText, folderButton));
        sync.Children.Add(Copy("Choose a folder managed by your cloud app. This preview never reads, writes or uploads its contents.", 12, true));
        _body.Children.Add(Card(sync));

        _sharing.Children.Add(Copy("What to share", 16));
        _sharing.Children.Add(SwitchRow("History", "History & Inbox", "Saved text and Inbox state. Optional on each device.", () => UpdateSync()));
        _sharing.Children.Add(SwitchRow("Audio", "Audio for new entries", "Include new recordings only when History & Inbox is enabled.", () => UpdateSync()));
        _body.Children.Add(_sharing);
        _devices.Children.Add(Copy("Devices · examples", 16));
        _devices.Children.Add(Row("desktop", Description("This PC", "Windows · current device")));
        _devices.Children.Add(Row("laptop", Description("MacBook", "macOS · sample device")));
        _devices.Children.Add(Row("phone", Description("iPhone", "iOS · sample device")));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Simulate sync", () => { _offline = false; UpdateSync(true); }));
        actions.Children.Add(Button("Preview offline", () => { _offline = true; UpdateSync(); }));
        _devices.Children.Add(actions);
        _body.Children.Add(Card(_devices));
        _body.Children.Add(_backup);
        ShowBackupHome(); UpdateSync();
        _body.Children.Add(Copy("Prototype only · no account connection, file export or restoration. Device and backup contents are fictional.", 12, true));
    }

    private void UpdateSync(bool synced = false)
    {
        var enabled = On("Enabled");
        var path = _values.GetValueOrDefault("CloudFolderSyncFolderPath", "");
        _folder.Text = string.IsNullOrWhiteSpace(path) ? "No folder selected" : path;
        _status.Text = !enabled ? "Sync is off" : path.Length == 0 ? "Choose your sync folder" : _offline ? "Waiting for connection · demo" : synced ? "Up to date · simulated" : "Ready to preview sync";
        _detail.Text = !enabled ? "Your data stays on this device." : _offline ? "Nothing was lost. Use Simulate sync to preview a retry." : "Simulation only. No data leaves this device.";
        SetControlsEnabled(_sharing, enabled && path.Length > 0);
        SetControlsEnabled(_devices, enabled && path.Length > 0);
        // Audio is subordinate to History; never retain a hidden enabled audio flag.
        if (!On("History")) Set("Audio", false);
        if (_sharing.Children.Count > 2 && _sharing.Children[2] is Grid audioRow)
        {
            SetControlsEnabled(audioRow, enabled && path.Length > 0 && On("History"));
            if (audioRow.Children.Last() is ToggleSwitch audio && audio.IsOn != On("Audio")) audio.IsOn = On("Audio");
        }
    }

    private static void SetControlsEnabled(DependencyObject element, bool enabled)
    {
        if (element is Control control) control.IsEnabled = enabled;
        if (element is Panel panel)
            foreach (var child in panel.Children) SetControlsEnabled(child, enabled);
    }

    private void ShowBackupHome()
    {
        var returning = _backupOpen;
        _backupOpen = false;
        _backup.Children.Clear();
        _backup.Children.Add(Copy("Backup & restore", 18));
        _backup.Children.Add(Copy("Choose which parts of TypeWhisper to keep or bring back, like on Mac.", 13, true));
        _backup.Children.Add(Row("folder", Description("Create a backup", "Select the categories to include."), Button("Preview backup", () => ShowSelection(false))));
        _backup.Children.Add(Row("restore", Description("Restore a backup", "Review a sample before confirming."), Button("Preview restore", () => ShowSelection(true))));
        if (_values.TryGetValue("PreviewSync.BackupResult", out var result)) _backup.Children.Add(Copy(result, 13, true));
        if (returning) DispatcherQueue.TryEnqueue(() =>
        {
            var row = _backup.Children.OfType<Grid>().FirstOrDefault();
            row?.Children.OfType<HandCursorButton>().FirstOrDefault()?.Focus(FocusState.Keyboard);
        });
    }

    private void ShowSelection(bool restore, string[]? previous = null)
    {
        _backupOpen = true;
        _backup.Children.Clear();
        _backup.Children.Add(Copy(restore ? "Restore · sample backup" : "Create · sample backup", 18));
        _backup.Children.Add(Copy("Example snapshot · Sep 5, 2026 · not a file from your computer", 12, true));
        string[] categories = ["General preferences", "Shortcuts", "Dictionary", "Snippets", "Workflows", "History (text only)"];
        var selected = (previous ?? categories).ToHashSet();
        var next = Button("Review selection", () => ShowReview(restore, selected.Order().ToArray()), true);
        next.IsEnabled = selected.Count > 0;
        foreach (var category in categories)
        {
            var toggle = PrototypeToggleSwitch.Create(selected.Contains(category));
            AutomationProperties.SetName(toggle, "Include " + category);
            toggle.Toggled += (_, _) => { if (toggle.IsOn) selected.Add(category); else selected.Remove(category); next.IsEnabled = selected.Count > 0; };
            _backup.Children.Add(Row(null, Copy(category, 14), toggle));
        }
        _backup.Children.Add(Copy("Not included: audio, model files, API keys, licenses or device-specific preferences. This is a UI subset, not a Mac-compatible backup format.", 12, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Cancel", ShowBackupHome)); actions.Children.Add(next); _backup.Children.Add(actions);
        DispatcherQueue.TryEnqueue(() => _backup.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<ToggleSwitch>().FirstOrDefault()?.Focus(FocusState.Keyboard));
    }

    private void ShowReview(bool restore, string[] selection)
    {
        _backup.Children.Clear();
        _backup.Children.Add(Copy(restore ? "Review restore" : "Review backup", 18));
        _backup.Children.Add(Copy(string.Join(" · ", selection), 14));
        _backup.Children.Add(Copy(restore ? "Confirming only completes this demo. Your settings, History and files will remain unchanged." : "Confirming only completes this demo. No backup file will be created.", 13, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var back = Button("Back", () => ShowSelection(restore, selection));
        actions.Children.Add(back);
        actions.Children.Add(Button("Cancel", ShowBackupHome));
        var confirm = Button(restore ? "Simulate restore" : "Simulate backup", () =>
        {
            _values["PreviewSync.BackupResult"] = $"{(restore ? "Restore" : "Backup")} simulated · {selection.Length} categories · no data changed";
            ShowBackupHome();
        }, true);
        actions.Children.Add(confirm); _backup.Children.Add(actions);
        DispatcherQueue.TryEnqueue(() => back.Focus(FocusState.Keyboard));
    }

    private Grid SwitchRow(string key, string title, string hint, Action changed)
    {
        var toggle = PrototypeToggleSwitch.Create(On(key));
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => { Set(key, toggle.IsOn); changed(); };
        return Row(null, Description(title, hint), toggle);
    }

    private static StackPanel Description(string title, string hint)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(Copy(title, 14)); panel.Children.Add(Copy(hint, 12, true)); return panel;
    }
    private static Grid Row(string? icon, FrameworkElement text, FrameworkElement? action = null)
    {
        var row = new Grid { ColumnSpacing = 14, RowSpacing = 10 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(icon is null ? 0 : 28) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        if (icon is not null) row.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
        if (action is not null)
        {
            Grid.SetColumn(action, 2); row.Children.Add(action);
            row.SizeChanged += (_, _) =>
            {
                var narrow = row.ActualWidth < 480 && action is not ToggleSwitch;
                Grid.SetColumn(action, narrow ? 1 : 2); Grid.SetRow(action, narrow ? 1 : 0);
                action.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            };
        }
        return row;
    }
    private static Border Card(UIElement child) => new() { Child = child, Padding = new Thickness(18), CornerRadius = new CornerRadius(10), Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) };
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new() { Text = text, FontSize = size, Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap, FontWeight = size >= 16 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
    private static HandCursorButton Button(string label, Action action, bool primary = false)
    {
        var button = new HandCursorButton { Content = label, Style = (Style)Application.Current.Resources[primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
        button.Click += (_, _) => action(); return button;
    }
}
