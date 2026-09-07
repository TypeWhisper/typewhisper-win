#if DEBUG
using System.Text.Json;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private static bool WorkflowProbeEnabled => WinUIProfile.IsTestProfile &&
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
            _target = GetForegroundWindow();
            GetWindowThreadProcessId(_target, out var processId);
            _targetProcessId = processId;
            if (_target == IntPtr.Zero || processId == 0 || processId == Environment.ProcessId)
                error = "external_target_required";
            else
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                _targetApp = process.ProcessName;
                await CaptureWorkflowAtStartAsync();
                _operationCancellation.Token.ThrowIfCancellationRequested();
                GetWindowThreadProcessId(_target, out var currentProcessId);
                if (GetForegroundWindow() != _target || currentProcessId != processId)
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
        var temporary = path + ".tmp";
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                ProcessName = Bounded(_targetApp, 260),
                Host = TypeWhisper.Presentation.BrowserWorkflowContext.NormalizeHost(_targetHostAtStart),
                SelectedWorkflowId = Bounded(_workflowAtStart?.Id, 256),
                Error = error
            });
            Directory.CreateDirectory(WinUIProfile.Root);
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, path, overwrite: true);
            SetStatus("Workflow probe saved. No audio was recorded.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("Workflow probe could not be saved. No audio was recorded."); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? Bounded(string? value, int maximum) =>
        value is { Length: var length } && length > maximum ? value[..maximum] : value;
}
#endif
