using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// App views shown as pages of the settings window, following the macOS settings sidebar.
public sealed partial class MainWindow
{
    private readonly RecorderView RecorderView = new();
    private readonly WorkflowsView WorkflowsView = new();
    private Grid? _workflowsPage;
    private TextBox? _workflowSearch;
    private HomeView? _home;
    private ActivityView? _statistics;
    private SyncBackupView? _backup;
    private Grid? _backupPage;
    // The settings page now showing one of these views, or null.
    private string? _settingsPage;
    // Runs once the requested page is shown; a new window first shows Home.
    private (string Category, Action Run)? _afterSettingsPageShown;
    private bool _settingsPageExited;

    private void InitializeSettingsPages()
    {
        var page = _workflowsPage = new Grid { RowSpacing = 12 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var search = _workflowSearch = new TextBox { PlaceholderText = "Search workflows…", Margin = new Thickness(12, 0, 12, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search workflows");
        search.TextChanged += (_, _) => WorkflowsView.Filter(search.Text.Trim());
        search.KeyDown += (_, e) =>
        {
            if (e.Key is not (global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)) return;
            WorkflowsView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
        };
        page.Children.Add(search);
        Grid.SetRow(WorkflowsView, 1);
        page.Children.Add(WorkflowsView);
        WorkflowsView.ClearSearchRequested += (_, _) => search.Text = "";
        // Search applies to the list only; detail pages have their own breadcrumb back to it.
        WorkflowsView.DetailModeChanged += detail => search.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        WorkflowsView.ExitRequested += (_, _) => _settingsPageExited = true;
    }

    private void OpenSettingsPage(string category, Action? afterShown = null)
    {
        if (_profileRestoreClosing) return;
        _afterSettingsPageShown = afterShown is null ? null : (category, afterShown);
        OpenSettings();
        _settingsWindow?.ShowCategory(category);
    }

    private FrameworkElement? SettingsPage(string category)
    {
        // Presenting resets a page; keep its state while it stays selected.
        var entering = _settingsPage != category;
        switch (category)
        {
            case "Home":
                if (_home is null)
                {
                    _home = new HomeView();
                    _home.Connect(_dictation.HistoryReader, () => _dictation.Shortcut);
                    _home.NavigateRequested += NavigateFromSettingsPage;
                }
                if (entering) _home.Present();
                return _home;
            case "Workflows":
                if (entering && !WorkflowsView.IsDetail) _workflowSearch!.Text = "";
                return _workflowsPage;
            case "Dictionary" or "Snippets":
                EnsureLexicon();
                if (entering) _lexicon!.Present(category == "Snippets");
                return _lexicon;
            case "Recorder":
                return RecorderView;
            case "File transcription":
                EnsureFileTranscription();
                if (entering) _fileTranscription!.Present();
                return _fileTranscription;
            case "Statistics":
                if (_statistics is null)
                {
                    _statistics = new ActivityView();
                    _statistics.Connect(_dictation.HistoryReader, () => _dictation.OutputPreferences.Current.SaveToHistory);
                    _statistics.NavigateRequested += NavigateFromSettingsPage;
                }
                if (entering) _statistics.Present();
                return _statistics;
            case "Sync & backup":
                return _backupPage ?? CreateBackupPage();
            default:
                return null;
        }
    }

    private Grid CreateBackupPage()
    {
        _backup = new SyncBackupView();
        _backup.ConnectRestore(RestoreProfile);
        _backup.ConnectDeleteAllData(DeleteAllData);
        var page = _backupPage = new Grid();
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock { Text = "Sync & backup", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(_backup);
        page.Children.Add(new ScrollViewer { Content = body, Padding = new Thickness(12, 4, 12, 16),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _backup.Actions.Margin = new Thickness(0, 12, 0, 12);
        var footer = new Border { Child = _backup.Actions, Margin = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] };
        Grid.SetRow(footer, 1);
        page.Children.Add(footer);
        return page;
    }

    private void SettingsPageChanged(string? category)
    {
        var previous = _settingsPage;
        _settingsPage = category;
        if (previous == "Recorder" && category != "Recorder") RecorderView.SetPresented(false);
        if (category == "Recorder" && previous != "Recorder") RecorderView.SetPresented(true);
        if (category is null) { _afterSettingsPageShown = null; return; }
        if (_afterSettingsPageShown is not { } after || after.Category != category) return;
        _afterSettingsPageShown = null;
        after.Run();
    }

    // Esc first leaves a page's detail view; on its top level it closes Settings as usual.
    private bool SettingsPageBack(string category)
    {
        _settingsPageExited = false;
        switch (category)
        {
            case "Workflows": WorkflowsView.GoBack(); break;
            case "Dictionary" or "Snippets": if (_lexicon is null) return false; _lexicon.GoBack(); break;
            case "File transcription": if (_fileTranscription is null) return false; _fileTranscription.GoBack(); break;
            case "Statistics": return _statistics?.CloseRangeIfOpen() == true;
            case "Sync & backup": return _backup?.ClosePreview() == true;
            default: return false;
        }
        return !_settingsPageExited;
    }

    private void SettingsPageKey(string category, KeyRoutedEventArgs e)
    {
        if (category == "File transcription") _fileTranscription?.HandleActionKey(e);
    }

    // The first section of History & Sync, as on macOS.
    private StackPanel HistoryWorkspaceSection()
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock { Text = "History workspace", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        section.Children.Add(new TextBlock { Text = "Search, review, edit, copy and export your transcriptions in a dedicated view.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
        var open = new HandCursorButton { Content = "Open History", HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        open.Click += (_, _) => ShowHistoryFromTray();
        section.Children.Add(open);
        return section;
    }

    private void NavigateFromSettingsPage(string destination)
    {
        if (destination == "History") ShowHistoryFromTray();
        else if (destination == "Setup") OpenSetup();
        else if (destination == "Statistics") OpenSettingsPage("Statistics");
        else OpenSettings();
    }

    private async Task RefreshSettingsPagesAsync()
    {
        if (_settingsPage == "Statistics" && _statistics is not null) await _statistics.RefreshAsync();
        else if (_settingsPage == "Home" && _home is not null) await _home.RefreshAsync();
    }

    private void EnsureLexicon()
    {
        if (_lexicon is not null) return;
        _lexicon = new LexiconView();
        _lexicon.ConnectTraining(_dictation);
        _lexicon.ExitRequested += () => _settingsPageExited = true;
    }

    private void EnsureFileTranscription()
    {
        if (_fileTranscription is not null) return;
        _fileTranscription = new FileTranscriptionView();
        _fileTranscription.Connect(_dictation);
        _fileTranscription.ExitRequested += () => _settingsPageExited = true;
    }

    internal void OpenLexicon(bool snippets = false, string? section = null) =>
        OpenSettingsPage(snippets ? "Snippets" : "Dictionary", section is null ? null : () => _lexicon?.Present(snippets, section));

    internal void OpenFileTranscription(Action? afterShown = null) => OpenSettingsPage("File transcription", afterShown);
    internal void OpenStatistics() => OpenSettingsPage("Statistics");
    internal void OpenSyncBackup() => OpenSettingsPage("Sync & backup");
    private void OpenWorkflows(Action? afterShown = null) => OpenSettingsPage("Workflows", afterShown);
    private void OpenRecorder(Action? afterShown = null) => OpenSettingsPage("Recorder", afterShown);
}
