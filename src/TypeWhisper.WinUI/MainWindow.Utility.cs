using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private bool UtilityOpen => UtilityHost.Visibility == Visibility.Visible;
    private ActivityView? _utilityActivity;
    private SyncBackupView? _utilityBackup;

    private void OpenUtility(string title)
    {
        if (_historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen)
        { ShowFromActivation(); ShowActivationNotice("Return to Quick Launch before opening " + title + ". Your current work is kept intact."); return; }
        if (UtilityOpen)
        {
            CloseUtility();
            if (UtilityOpen) return;
        }
        _settingsWindow?.AppWindow.Hide();
        ShowFromActivation();
        _launcherQuery = SearchBox.Text;
        var root = new Grid(); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        if (title == "Sync & backup")
        {
            _utilityBackup = new SyncBackupView();
            _utilityBackup.ConnectRestore(RestoreProfile);
            var body = new StackPanel { Spacing = 16 };
            body.Children.Add(new TextBlock { Text = title, FontSize = 24 });
            body.Children.Add(_utilityBackup);
            root.Children.Add(new ScrollViewer { Content = body, Padding = new Thickness(24, 16, 24, 16),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        else
        {
            _utilityActivity = new ActivityView();
            _utilityActivity.Connect(_dictation.HistoryReader, () => _dictation.OutputPreferences.Current.SaveToHistory);
            _utilityActivity.NavigateRequested += destination =>
            {
                CloseUtility();
                if (destination == "History") OpenHistory();
                else if (destination == "Setup") OpenSetup();
                else if (destination == "Statistics") OpenUtility(destination);
                else OpenSettings();
            };
            root.Children.Add(_utilityActivity);
            _utilityActivity.Present();
        }
        var footer = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 12, 0, 12) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var back = new HandCursorButton { Content = "Back · Esc", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        back.Click += (_, _) => CloseUtility(); Grid.SetColumn(back, 1); footer.Children.Add(back);
        var footerBody = new StackPanel { Spacing = 8 };
        footerBody.Children.Add(footer);
        if (_utilityBackup is not null) { _utilityBackup.Actions.Margin = new Thickness(0, 0, 0, 12); footerBody.Children.Add(_utilityBackup.Actions); }
        var border = new Border { Child = footerBody, Margin = new Thickness(24, 0, 24, 0), BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] };
        Grid.SetRow(border, 1); root.Children.Add(border);
        var navigation = new Breadcrumbs();
        navigation.SetItems(new("Quick Launch", CloseUtility), new(title));
        navigation.Margin = new Thickness(24, 4, 24, 8);
        var shell = new Grid(); shell.RowDefinitions.Add(new() { Height = GridLength.Auto }); shell.RowDefinitions.Add(new());
        shell.Children.Add(navigation); Grid.SetRow(root, 1); shell.Children.Add(root);
        UtilityHost.Child = shell;
        SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        UtilityHost.Visibility = Visibility.Visible;
        back.Focus(FocusState.Programmatic);
    }

    private void CloseUtility()
    {
        if (_utilityActivity?.CloseRangeIfOpen() == true || _utilityBackup?.ClosePreview() == true) return;
        UtilityHost.Visibility = Visibility.Collapsed;
        UtilityHost.Child = null; _utilityActivity = null; _utilityBackup = null;
        SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Text = _launcherQuery;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }
}
