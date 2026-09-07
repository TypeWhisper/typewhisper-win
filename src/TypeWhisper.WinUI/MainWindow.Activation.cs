using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    internal void OpenFilesFromTray() => HandleActivation(ApplicationActivationRequest.Parse(["--files"]));
    private void ShowActivationNotice(string message)
    {
        ActivationNoticeText.Text = message;
        ActivationNotice.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }
    private void DismissActivationNotice_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ActivationNotice.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    internal void ShowActivationFailure(Exception error)
    {
        System.Diagnostics.Trace.TraceError("Activation request failed: {0}", error);
        if (_closing || _profileRestoreClosing) return;
        ShowFromActivation();
        ShowActivationNotice("An activation request could not be opened. Retry that request; other queued requests will continue.");
    }
    internal void HandleActivation(ApplicationActivationRequest request)
    {
        if (_closing || _profileRestoreClosing) return;
        if (!request.ShowWindow) return;
        ShowFromActivation();
        if (request.Error is { } error) { ShowActivationNotice(error); return; }
        var filesOpen = FileTranscriptionOpen && !LexiconOpen && !_historyOpen && !_recorderOpen && !_workflowsOpen && !_pluginsOpen && !_marketplaceOpen;
        var workspaceOpen = FileTranscriptionOpen || LexiconOpen || _historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen;
        if (ActivationAdmission.Reject(false, workspaceOpen, filesOpen, _fileTranscription?.CanAcceptActivation == true, request.Route) is { } rejection)
        { ShowActivationNotice(rejection); return; }
        switch (request.Route)
        {
            case "--account": OpenAccount(); break;
            case "--sync-backup": OpenSyncBackup(); break;
            case "--dashboard": OpenDashboard(); break;
            case "--statistics": OpenDashboard(true); break;
            case "--dictionary": OpenLexicon(); break;
            case "--snippets": OpenLexicon(true); break;
            case "--files":
                if (!filesOpen) OpenFileTranscription();
                if (request.Files.Count > 0 && _fileTranscription is not null)
                    ShowActivationNotice(_fileTranscription.AddActivatedFiles(request.Files));
                break;
            case "--setup": OpenSetup(); break;
            case "--compare-selects": OpenSelectComparison(); break;
            case "--settings": OpenSettings(); break;
        }
    }
}
