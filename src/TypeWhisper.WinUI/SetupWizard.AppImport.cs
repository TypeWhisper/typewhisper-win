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
        // Detect files only. Reading another app's entries always requires an explicit import action.
        var detected = new[] { LexiconImportApp.WisprFlow, LexiconImportApp.Handy }
            .Where(app => File.Exists(LexiconAppImport.DefaultPath(app))).ToArray();
        var description = detected.Length > 0
            ? string.Join(" and ", detected.Select(LexiconAppImport.Name)) + " data found on this PC. Bring your words and snippets with you."
            : "Switching from Wispr Flow or Handy? Bring your words with you, or choose a file from another installation.";
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(CardContent("dictionary", "Import from another app", description, "Optional"));
        var actions = new StackPanel { Spacing = 8 };
        var words = Button("Import words and corrections…", () => StartAppImport(false, detected.FirstOrDefault()));
        words.HorizontalAlignment = HorizontalAlignment.Left;
        var snippets = Button("Import Wispr Flow snippets…", () => StartAppImport(true, LexiconImportApp.WisprFlow));
        snippets.HorizontalAlignment = HorizontalAlignment.Left;
        actions.Children.Add(words); actions.Children.Add(snippets); panel.Children.Add(actions);
        panel.Children.Add(Copy("Review before adding. Existing entries stay unchanged. You can continue setup without importing and do this later in Dictionary or Snippets."));
        _appImportStatus = Copy(_appImportResult ?? "");
        _appImportStatus.Visibility = string.IsNullOrEmpty(_appImportResult) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(_appImportStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_appImportStatus);
        _body.Children.Add(Card(panel));
    }

    private void StartAppImport(bool snippets, LexiconImportApp app)
    {
        if (_closing || _importsClosing || _appImportTask is { IsCompleted: false }) return;
        _appImportTask = RunAppImportAsync(snippets, app);
    }

    private async Task RunAppImportAsync(bool snippets, LexiconImportApp app)
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
            var result = await _appImportFlow.ShowAsync(snippets, app);
            if (!_closing && !_importsClosing) Report(result ?? "Import canceled. Nothing was changed.");
        }
        finally { _appImportFlow = null; }
    }
}
