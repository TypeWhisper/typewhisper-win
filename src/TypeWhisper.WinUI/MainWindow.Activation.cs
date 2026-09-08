using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    internal void OpenFilesFromTray() => HandleActivation(ApplicationActivationRequest.Parse(["--files"]));
    private string? _noticeWorkflowId;
    private bool _noticeUsesDefault;
    private void ShowActivationNotice(string message, string? workflowId = null)
    {
        _noticeWorkflowId = workflowId;
        _noticeUsesDefault = false;
        ActivationNoticeTitle.Text = workflowId is null ? "Action needed" : "Workflow could not start";
        ActivationNoticeAction.Visibility = workflowId is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        ActivationNoticeText.Text = message;
        ActivationNotice.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }
    private void DismissActivationNotice_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _noticeWorkflowId = null;
        ActivationNotice.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    private void ActivationNoticeAction_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_closing || _profileRestoreClosing || _noticeWorkflowId is not { } id) return;
        if (_recorderOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen || UtilityOpen || _historyOpen)
        {
            ActivationNoticeText.Text = "Return to Quick Launch, then choose Edit workflow here. Your current work has been kept.";
            return;
        }
        if (!_workflowsOpen) OpenWorkflows();
        if (!WorkflowsView.EditWorkflow(id))
            ActivationNoticeText.Text = "Finish your current workflow action first. If this workflow was deleted, dismiss this notice.";
    }
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
        var workspaceOpen = FileTranscriptionOpen || LexiconOpen || UtilityOpen || _historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen;
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
