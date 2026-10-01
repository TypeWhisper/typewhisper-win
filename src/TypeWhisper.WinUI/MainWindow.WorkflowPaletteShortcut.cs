using Microsoft.UI.Windowing;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private WorkflowPaletteWindow? _workflowPalette;

    private void InitializeWorkflowPaletteShortcut() => _workflowPaletteShortcut = InitializeActionShortcut(GlobalShortcuts.WorkflowPalette,
        OpenWorkflowPaletteFromShortcut, 0x8000, "workflow-palette-hotkeys.txt", Loc.T("Workflow palette shortcuts"), Loc.T("workflow palette"));

    private string? ChangeWorkflowPaletteShortcut(string value) => ChangeActionShortcut(_workflowPaletteShortcut, Loc.T("Workflow palette"), value);

    private void OpenWorkflowPaletteFromShortcut()
    {
        if (_closing || _profileRestoreClosing || _workflowShortcutsStopping || ShortcutRecorder.AnyEditing) return;
        // The shortcut toggles the panel, as on macOS.
        if (_workflowPalette is { } open) { open.Dismiss(); return; }
        if (ShortcutAdmission.Rejection(Loc.T("the workflow palette"), ShortcutActionBusy || WorkflowsView.IsBusy || _dictation.Models.Busy) is { } refusal)
        { ShowActivationNotice(refusal); return; }
        var target = ForegroundWindowHistory.CurrentTarget;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowTask = completion.Task;
        _ = OpenWorkflowPaletteAsync(target, completion);
    }

    // Copies the selection from the app in front before the panel takes focus; without one, the clipboard text is used.
    private async Task OpenWorkflowPaletteAsync(PasteTarget? target, TaskCompletionSource completion)
    {
        string text;
        try { text = await PaletteSourceTextAsync(target); }
        finally { completion.TrySetResult(); }
        if (_closing || _profileRestoreClosing || _workflowShortcutsStopping) return;
        IReadOnlyList<WorkflowPaletteItem> workflows;
        try { workflows = WorkflowPalette.Candidates(new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { workflows = []; }
        ShowPalette(workflows, text, target, recentOnly: false);
    }

    // The recent-transcriptions palette of the macOS menu bar and its shortcut: pick an entry to insert it.
    private void OpenRecentTranscriptions(PasteTarget? target)
    {
        if (_closing || _profileRestoreClosing || _workflowShortcutsStopping) return;
        if (_workflowPalette is { } open) { open.Dismiss(); return; }
        ShowPalette([], "", target, recentOnly: true);
    }

    private void ShowPalette(IReadOnlyList<WorkflowPaletteItem> workflows, string text, PasteTarget? target, bool recentOnly)
    {
        var recent = (_historyService?.Records ?? []).Where(record => !string.IsNullOrWhiteSpace(record.DisplayText))
            .OrderByDescending(record => record.Timestamp).Take(20).ToArray();
        var hasText = !string.IsNullOrWhiteSpace(text);
        if (!hasText && recent.Length == 0)
        {
            ShowActivationNotice(recentOnly ? Loc.T("No transcriptions yet. Dictate once, then try again.") : Loc.T("Please select or copy some text first."));
            return;
        }
        var palette = _workflowPalette = new WorkflowPaletteWindow(workflows, recent, hasText, recentOnly);
        palette.RunWorkflow = item => RunPaletteWorkflowAsync(palette, item.Workflow, text, target);
        palette.InsertRecent = record => DeliverPaletteTextAsync(palette, record.DisplayText, target);
        palette.Cancel = RequestWorkflowCancellation;
        palette.Closed += (_, _) => { if (ReferenceEquals(_workflowPalette, palette)) _workflowPalette = null; };
        NativeMethods.GetCursorPos(out var cursor);
        palette.ShowOn(DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Nearest));
    }

    private async Task<string> PaletteSourceTextAsync(PasteTarget? target)
    {
        if (target is { } source)
        {
            try { return await new WindowsSelectedTextCapture(WinRT.Interop.WindowNative.GetWindowHandle(this)).CaptureAsync(source.Window, source.ProcessId); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException) { }
        }
        try
        {
            var clipboard = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return clipboard.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                ? await clipboard.GetTextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)) : "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    private async Task RunPaletteWorkflowAsync(WorkflowPaletteWindow palette, Workflow workflow, string text, PasteTarget? target)
    {
        if (_closing || _workflowShortcutsStopping || _workflowTask is { IsCompleted: false } || !_dictation.CanStartWorkflowShortcut)
        { palette.ShowMessage(Loc.T("Finish the current recording, transcription or model operation before running a workflow.")); return; }
        var usesDefault = workflow.Behavior.ProviderOverride == WorkflowLlmDefaults.Inherit;
        try { workflow = _dictation.WorkflowDefaults.Resolve(workflow); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { palette.ShowMessage(Loc.T("Default LLM settings could not be loaded. Open Default LLM in Workflows and save the selection again.")); return; }
        if (ManualWorkflowRunner.ConfigurationError(workflow.Behavior.ProviderOverride, workflow.Behavior.ModelOverride,
            (provider, model) => _dictation.LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model))) is { } error)
        {
            palette.ShowMessage(workflow.Name + "\n" + (usesDefault
                ? Loc.T("The default LLM is missing or unavailable. Open Default LLM in Workflows, or choose a provider and model for this workflow.") : error));
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowTask = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _workflowCancellation = cancellation;
        palette.ShowRunning(workflow.Name);
        try
        {
            (string Text, string? Message, bool? ActionSucceeded) execution;
            using (_dictation.ReserveWorkflowShortcut())
            {
                DictationChanged?.Invoke(Loc.T("Workflow: {0}", workflow.Name), false);
                execution = await _dictation.RunWorkflowWithActionAsync(workflow, text, cancellation.Token);
            }
            // A workflow with an action plugin delivers its result there instead of replacing the selection.
            if (execution.ActionSucceeded is not null) palette.ShowMessage(execution.Message ?? Loc.T("Completed."));
            else await DeliverPaletteTextAsync(palette, execution.Text, target);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { palette.ShowMessage(Loc.T("Canceled. Your selected text was not changed.")); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { palette.ShowMessage(Loc.T("Processing failed. Your selected text was not changed. {0}", ex.Message)); }
        finally
        {
            _workflowCancellation = null;
            try
            {
                await ObserveWorkflowCancellationAsync(_workflowCancelTask);
                _workflowCancelTask = Task.CompletedTask;
                if (!_closing) DictationChanged?.Invoke(_dictation.Status, _dictation.IsRecording);
            }
            finally { completion.TrySetResult(); }
        }
    }

    // Replaces the selection in the source app; when that app is gone or refuses the paste, the text is copied instead.
    private async Task DeliverPaletteTextAsync(WorkflowPaletteWindow palette, string text, PasteTarget? target)
    {
        if (string.IsNullOrEmpty(text)) { palette.ShowMessage(Loc.T("The workflow returned no text. Your selected text was not changed.")); return; }
        if (target is { } destination)
        {
            palette.HideForInsertion();
            if (await _dictation.InsertIntoAsync(text, destination)) { palette.Dismiss(); return; }
        }
        try
        {
            var content = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
            palette.ShowMessage(target is null ? Loc.T("Copied to the clipboard.") : Loc.T("The text could not be inserted, so it was copied to the clipboard."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { palette.ShowMessage(Loc.T("The text could not be inserted or copied. Try again.")); }
    }
}
