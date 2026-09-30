#if DEBUG
using System.Text.Json;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal static bool WorkflowProbeEnabled => WinUIProfile.IsTestProfile &&
        Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_WORKFLOW_PROBE") == "1";

    // Runs under the ordinary recording gate, but before readiness, audio, or effects.
    // Only the genuine capture/matcher result is written; never the raw UIA value.
    private async Task RunWorkflowProbeAsync()
    {
        if (_disposed || !WorkflowProbeEnabled) return;
        _operationCancellation.Begin();
        _workflowAtStart = null;
        _targetHostAtStart = null;
        _targetApp = "";
        string? error = null;
        try
        {
            _target = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(_target, out var processId);
            _targetProcessId = processId;
            if (_target == IntPtr.Zero || processId == 0 || processId == Environment.ProcessId)
                error = "external_target_required";
            else
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                _targetApp = process.ProcessName;
                await CaptureWorkflowAtStartAsync();
                _operationCancellation.Token.ThrowIfCancellationRequested();
                NativeMethods.GetWindowThreadProcessId(_target, out var currentProcessId);
                if (NativeMethods.GetForegroundWindow() != _target || currentProcessId != processId)
                {
                    _targetHostAtStart = null;
                    _workflowAtStart = null;
                    error = "target_changed";
                }
                else if (_workflowAtStart?.Error is not null) error = "workflow_unavailable";
            }
        }
        catch (OperationCanceledException) { error = "capture_cancelled"; _targetHostAtStart = null; _workflowAtStart = null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { error = "capture_failed"; _targetHostAtStart = null; _workflowAtStart = null; }
        if (_disposed) return;
        var path = WinUIProfile.DataPath("workflow-probe.json");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                ProcessName = Bounded(_targetApp, 260),
                Host = TypeWhisper.Presentation.BrowserWorkflowContext.NormalizeHost(_targetHostAtStart),
                SelectedWorkflowId = Bounded(_workflowAtStart?.Id, 256),
                CaptureStage = WindowsBrowserTargetReader.LastProbeStage,
                AddressState = WindowsBrowserTargetReader.LastAddressState,
                Error = error
            });
            AtomicFileWriter.WriteAllBytes(path, payload);
            SetStatus("Workflow probe saved. No audio was recorded.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("Workflow probe could not be saved. No audio was recorded."); }
    }

    private static string? Bounded(string? value, int maximum) =>
        value is { Length: var length } && length > maximum ? value[..maximum] : value;
}
#endif
