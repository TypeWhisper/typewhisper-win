using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

public sealed partial class SetupWizard
{
    private LexiconAppImportDialog? _appImportFlow;
    private Task? _appImportTask;
    private string? _appImportResult;
    private TextBlock? _appImportStatus;
    private bool _importsClosing;

    internal Task ShutdownImportAsync()
    {
        _importsClosing = true;
        _appImportFlow?.Cancel();
        return _appImportTask ?? Task.CompletedTask;
    }

    private void RenderAppImport()
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var link = new HyperlinkButton
        {
            Content = "Import from Wispr Flow or Handy…", FontSize = 13,
            Foreground = Resource("MutedBrush"), Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        link.Click += (_, _) => StartAppImport();
        panel.Children.Add(link);
        _appImportStatus = Copy(_appImportResult ?? "");
        _appImportStatus.TextAlignment = TextAlignment.Center;
        _appImportStatus.Visibility = string.IsNullOrEmpty(_appImportResult) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(_appImportStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_appImportStatus);
        _body.Children.Add(panel);
    }

    private void StartAppImport()
    {
        if (_closing || _importsClosing || _appImportTask is { IsCompleted: false }) return;
        // Presence only preselects a source; reading requires confirmation in the dialog.
        var app = new[] { LexiconImportApp.WisprFlow, LexiconImportApp.Handy }
            .FirstOrDefault(source => File.Exists(LexiconAppImport.DefaultPath(source)));
        _appImportTask = RunAppImportAsync(app);
    }

    private async Task RunAppImportAsync(LexiconImportApp app)
    {
        void Report(string message)
        {
            _appImportResult = message;
            if (_appImportStatus is not null && !_closing)
            {
                _appImportStatus.Text = message;
                _appImportStatus.Visibility = Visibility.Visible;
            }
        }
        _appImportFlow = new(this, Report);
        try
        {
            var result = await _appImportFlow.ShowAsync(false, app, chooseDestination: true);
            if (!_closing && !_importsClosing) Report(result ?? "Import canceled. Nothing was changed.");
        }
        finally { _appImportFlow = null; }
    }
}
