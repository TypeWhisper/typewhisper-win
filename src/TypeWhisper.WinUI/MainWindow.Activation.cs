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
    // A rejected transcription task stops before recording, so the overlay cannot explain it.
    private void ShowTaskStartError(string? error, TypeWhisper.Core.Models.Workflow? workflow = null)
    {
        if (_closing || _profileRestoreClosing || error is null) return;
        ShowFromActivation();
        ShowActivationNotice(workflow is null ? error : workflow.Name + "\n" + error, workflow?.Id);
    }
    private void DismissActivationNotice_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _noticeWorkflowId = null;
        ActivationNotice.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    private void ActivationNoticeAction_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_closing || _profileRestoreClosing || _noticeWorkflowId is not { } id) return;
        OpenWorkflows(() =>
        {
            if (!WorkflowsView.EditWorkflow(id))
                ActivationNoticeText.Text = "Finish your current workflow action first. If this workflow was deleted, dismiss this notice.";
        });
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
        if (request.AccountCallback is { } callback)
        {
            WinUIPremiumAccount.ReceiveCallback(callback);
            OpenSettings();
            return;
        }
        if (request.Error is { } error) { ShowFromActivation(); ShowActivationNotice(error); return; }
        // Every route opens a settings page; the page keeps any unsaved work of its own.
        switch (request.Route)
        {
            case "--account": OpenAccount(); break;
            case "--sync-backup": OpenSyncBackup(); break;
            case "--dashboard": OpenSettingsPage("Home"); break;
            case "--statistics": OpenStatistics(); break;
            case "--dictionary": OpenLexicon(); break;
            case "--snippets": OpenLexicon(true); break;
            case "--files":
                // The file view reports added files, or why it cannot take them now, in its own notice.
                OpenFileTranscription(request.Files.Count == 0 ? null : () => _fileTranscription?.AddActivatedFiles(request.Files));
                break;
            case "--setup": OpenSetup(); break;
            case "--compare-selects": OpenSelectComparison(); break;
            case "--settings": OpenSettings(); break;
            default: ShowFromActivation(); break;
        }
    }
}
