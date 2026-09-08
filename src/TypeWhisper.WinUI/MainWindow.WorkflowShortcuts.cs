using System.Runtime.InteropServices;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private PrototypeHotkeyRegistration? _workflowHotkeys;
    private WorkflowShortcutCatalog? _workflowShortcuts;
    private CancellationTokenSource? _workflowCancellation;
    private Task? _workflowTask;
    private Task _workflowCancelTask = Task.CompletedTask;
    private bool _workflowShortcutsStopping;
    private readonly HashSet<WorkflowShortcutWindow> _workflowWindows = [];

    private void InitializeWorkflowShortcuts()
    {
        if (_closing) return;
        _workflowHotkeys = new PrototypeHotkeyRegistration(this, RunWorkflowShortcut, 0x7800);
        _workflowShortcuts = new(new(WinUIProfile.DataPath("workflows.json")), new WorkflowHotkeyBackend(_workflowHotkeys), value =>
        {
            if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
                return "Already used by Quick Launch.";
            if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
                return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
            if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
                return "Already used by Cancel processing.";
            return HistoryShortcutConflict(value) ?? CopyLastShortcutConflict(value) ?? ReadLastShortcutConflict(value);
        });
        WorkflowsView.Shortcuts = _workflowShortcuts;
        if (_workflowShortcuts.Initialize() is { } error) MetricsText.Text = error;
    }

    private void RunWorkflowShortcut(string chord)
    {
        var workflow = _workflowShortcuts?.Resolve(chord);
        if (workflow is null) return;
        if (!_closing && !_profileRestoreClosing && !_workflowShortcutsStopping && !PrototypeShortcutRecorder.AnyEditing
            && ManualWorkflowStore.IsDictationShortcut(workflow) && _dictationInput?.IsRecordingOrStarting == true)
        {
            _ = _dictationInput.SubmitAsync(DictationInputAction.Stop);
            return;
        }
        if (_closing || _profileRestoreClosing || _workflowShortcutsStopping || _workflowTask is { IsCompleted: false }
            || PrototypeShortcutRecorder.AnyEditing || WorkflowsView.IsBusy || !_dictation.CanChangeProvider
            || _dictationInitialization is not { IsCompleted: true } || _dictationInput?.IsRecordingOrStarting == true) return;
        if (ManualWorkflowStore.IsDictationShortcut(workflow))
        {
            var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(workflow);
            _ = _dictationInput?.SubmitAsync(DictationInputAction.Start, () =>
            {
                _dictation.LivePreviewEnabled = _transcriptPreviewEnabled;
                return _dictation.StartAsync(snapshot);
            });
            return;
        }
        var target = GetForegroundWindow();
        GetWindowThreadProcessId(target, out var processId);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowTask = completion.Task;
        _ = CaptureAndRunWorkflowAsync(workflow, target, processId, completion);
    }

    private async Task CaptureAndRunWorkflowAsync(TypeWhisper.Core.Models.Workflow workflow, IntPtr target, uint processId, TaskCompletionSource completion)
    {
        using var cancellation = new CancellationTokenSource();
        _workflowCancellation = cancellation;
        var provider = _dictation.LlmProviders.FirstOrDefault(p => p.SelectionId == workflow.Behavior.ProviderOverride);
        var label = (provider?.Name ?? workflow.Behavior.ProviderOverride ?? "Not configured") + " · " + workflow.Behavior.ModelOverride;
        try
        {
            using var reservation = _dictation.ReserveWorkflowShortcut();
            DictationChanged?.Invoke("Workflow: " + workflow.Name, false);
            if (provider is not { Ready: true } || !provider.Models.Any(m => m.Id == workflow.Behavior.ModelOverride))
                throw new InvalidOperationException("The workflow provider or model is unavailable. Configure it in Workflows before running this shortcut.");
            var capture = new WindowsSelectedTextCapture(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var source = await capture.CaptureAsync(target, processId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing || _workflowShortcutsStopping) return;
            var window = TrackWorkflowWindow(new(workflow.Name, source, label));
            window.Activate();
            await window.RunAsync(workflow, _dictation, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closing && !_workflowShortcutsStopping)
            {
                var window = TrackWorkflowWindow(new(workflow.Name, "", label));
                window.ShowCaptureError(ex.Message);
                window.Activate();
            }
        }
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

    private WorkflowShortcutWindow TrackWorkflowWindow(WorkflowShortcutWindow window)
    {
        _workflowWindows.Add(window);
        window.Closed += (_, _) => _workflowWindows.Remove(window);
        return window;
    }
    private void RequestWorkflowCancellation()
    {
        if (_workflowCancellation is { IsCancellationRequested: false } request)
            _workflowCancelTask = request.CancelAsync();
    }
    private static async Task ObserveWorkflowCancellationAsync(Task callbacks)
    {
        try { await callbacks; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Workflow cancellation callback failed: {0}", ex.GetType().Name); }
    }
    private void RequestProcessingCancellation() { RequestWorkflowCancellation(); _dictation.RequestCancel(); }
    private async Task StopWorkflowShortcutsAsync()
    {
        _workflowShortcutsStopping = true;
        _workflowHotkeys?.Dispose();
        RequestWorkflowCancellation();
        if (_workflowTask is not null) await _workflowTask;
        await Task.WhenAll(_workflowWindows.ToArray().Select(w => w.ShutdownAsync()));
    }
    private sealed class WorkflowHotkeyBackend(PrototypeHotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
