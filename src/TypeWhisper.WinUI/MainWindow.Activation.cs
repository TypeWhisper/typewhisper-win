using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    internal void OpenFilesFromTray() => HandleActivation(ApplicationActivationRequest.Parse(["--files"]));
    private NoticeWindow? _notice;
    private string? _noticeWorkflowId;
    private bool _noticeUsesDefault;

    // Notices appear in a card at the overlay position and never take focus from the app in front.
    internal void ShowNotice(AppNotice notice)
    {
        if (_profileRestoreClosing) return;
        // A new notice replaces the card, so a later workflow fix must not close it.
        _noticeWorkflowId = null;
        _noticeUsesDefault = false;
        try
        {
            if (_notice is null)
            {
                var window = _notice = new NoticeWindow();
                window.Closed += (_, _) => { if (ReferenceEquals(_notice, window)) _notice = null; };
            }
            // Keep a visible recording overlay uncovered.
            var overlay = _liveOverlay?.IsPreviewVisible == true ? _overlayMode switch
            {
                OverlayMode.Minimal => 22 + 8, OverlayMode.Compact => 36 + 8, _ => OverlayWindow.WindowHeight + 8
            } : 0;
            _notice.Show(notice, ResolveOverlayDisplayArea(), OverlayPreferences, overlay);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Notice could not be shown: {0}; {1}", ex, notice.Text);
        }
    }

    private void ShowActivationNotice(string message, string? workflowId = null)
    {
        ShowNotice(workflowId is null
            ? new AppNotice(message)
            : new AppNotice(message, Loc.T("Workflow could not start"), ActionLabel: Loc.T("Edit workflow"), Action: () => EditNoticeWorkflow(workflowId)));
        _noticeWorkflowId = workflowId;
    }

    // Closes the notice once the workflow it reported has been fixed.
    private void DismissWorkflowNotice()
    {
        _noticeWorkflowId = null;
        _noticeUsesDefault = false;
        _notice?.Dismiss();
    }

    // A rejected transcription task stops before recording, so the overlay cannot explain it.
    private void ShowTaskStartError(string? error, TypeWhisper.Core.Models.Workflow? workflow = null)
    {
        if (_closing || _profileRestoreClosing || error is null) return;
        ShowActivationNotice(workflow is null ? error : workflow.Name + "\n" + error, workflow?.Id);
    }

    private void EditNoticeWorkflow(string id)
    {
        if (_closing || _profileRestoreClosing) return;
        OpenWorkflows(() =>
        {
            if (!WorkflowsView.EditWorkflow(id))
                ShowNotice(new AppNotice(Loc.T("Finish your current workflow action first. If this workflow was deleted, you can ignore this notice.")));
        });
    }

    internal void ShowActivationFailure(Exception error)
    {
        System.Diagnostics.Trace.TraceError("Activation request failed: {0}", error);
        if (_closing || _profileRestoreClosing) return;
        ShowActivationNotice(Loc.T("An activation request could not be opened. Retry that request; other queued requests will continue."));
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
        if (request.Error is { } error) { ShowActivationNotice(error); return; }
        if (request.SettingsCategory is { } category) { OpenSettingsPage(category); return; }
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
            // Starting TypeWhisper again while it runs opens Settings, as on macOS.
            default: OpenSettings(); break;
        }
    }
}
