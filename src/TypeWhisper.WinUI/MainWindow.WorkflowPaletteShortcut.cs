using Microsoft.UI.Windowing;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HotkeyRegistration? _workflowPaletteHotkey;
    private ProcessingCancelShortcut? _workflowPaletteShortcutSettings;
    private WorkflowPaletteWindow? _workflowPalette;

    private void InitializeWorkflowPaletteShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _workflowPaletteHotkey = new(this, OpenWorkflowPaletteFromShortcut, 0x8000);
        _workflowPaletteShortcutSettings = new(WinUIProfile.DataPath("workflow-palette-hotkeys.txt"),
            new WorkflowPaletteShortcutBackend(_workflowPaletteHotkey), ValidateWorkflowPaletteShortcut, "Workflow palette shortcuts");
        var error = _workflowPaletteShortcutSettings.Initialize();
        _settingsValues["WorkflowPaletteHotkeys"] = _workflowPaletteHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? WorkflowPaletteShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_workflowPaletteHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Workflow palette. Change that shortcut first." : null;

    private string? ValidateWorkflowPaletteShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the workflow palette shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return RecordingShortcutConflict(value) ?? RecorderShortcutConflict(value) ?? HistoryShortcutConflict(value) ?? ReadLastShortcutConflict(value) ?? CopyLastShortcutConflict(value) ?? PasteLastShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangeWorkflowPaletteShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_workflowPaletteShortcutSettings is null) return "Workflow palette shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _workflowPaletteShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["WorkflowPaletteHotkeys"] = _workflowPaletteShortcutSettings.Value;
        return error;
    }

    private void OpenWorkflowPaletteFromShortcut()
    {
        if (_closing || _profileRestoreClosing || _workflowShortcutsStopping || ShortcutRecorder.AnyEditing) return;
        // The shortcut toggles the panel, as on macOS.
        if (_workflowPalette is { } open) { open.Dismiss(); return; }
        var busy = _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
            || WorkflowsView.IsBusy || _dictation.Models.Busy || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };
        if (WorkflowPaletteShortcutAdmission.Rejection(false, busy, otherWorkspaceOpen: false) is { } refusal)
        { ShowFromActivation(); ShowActivationNotice(refusal); return; }
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
            ShowFromActivation();
            ShowActivationNotice(recentOnly ? "No transcriptions yet. Dictate once, then try again." : "Please select or copy some text first.");
            return;
        }
        var palette = _workflowPalette = new WorkflowPaletteWindow(workflows, recent, hasText, recentOnly);
        palette.RunWorkflow = item => RunPaletteWorkflowAsync(palette, item.Workflow, text, target);
        palette.InsertRecent = record => DeliverPaletteTextAsync(palette, record.DisplayText, target);
        palette.Cancel = RequestWorkflowCancellation;
        palette.Closed += (_, _) => { if (ReferenceEquals(_workflowPalette, palette)) _workflowPalette = null; };
        GetCursorPos(out var cursor);
        palette.ShowOn(DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest));
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
        { palette.ShowMessage("Finish the current recording, transcription or model operation before running a workflow."); return; }
        var usesDefault = workflow.Behavior.ProviderOverride == WorkflowLlmDefaults.Inherit;
        try { workflow = _dictation.WorkflowDefaults.Resolve(workflow); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { palette.ShowMessage("Default LLM settings could not be loaded. Open Default LLM in Workflows and save the selection again."); return; }
        if (ManualWorkflowRunner.ConfigurationError(workflow.Behavior.ProviderOverride, workflow.Behavior.ModelOverride,
            (provider, model) => _dictation.LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model))) is { } error)
        {
            palette.ShowMessage(workflow.Name + "\n" + (usesDefault
                ? "The default LLM is missing or unavailable. Open Default LLM in Workflows, or choose a provider and model for this workflow." : error));
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
                DictationChanged?.Invoke("Workflow: " + workflow.Name, false);
                execution = await _dictation.RunWorkflowWithActionAsync(workflow, text, cancellation.Token);
            }
            // A workflow with an action plugin delivers its result there instead of replacing the selection.
            if (execution.ActionSucceeded is not null) palette.ShowMessage(execution.Message ?? "Completed.");
            else await DeliverPaletteTextAsync(palette, execution.Text, target);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { palette.ShowMessage("Canceled. Your selected text was not changed."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { palette.ShowMessage("Processing failed. Your selected text was not changed. " + ex.Message); }
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
        if (string.IsNullOrEmpty(text)) { palette.ShowMessage("The workflow returned no text. Your selected text was not changed."); return; }
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
            palette.ShowMessage(target is null ? "Copied to the clipboard." : "The text could not be inserted, so it was copied to the clipboard.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { palette.ShowMessage("The text could not be inserted or copied. Try again."); }
    }

    private sealed class WorkflowPaletteShortcutBackend(HotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
