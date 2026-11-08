using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// A plugin can take over dictation into the apps it lists, such as a game whose chat needs keys around the text. It
// hears about the dictation's steps, may show it in the app in place of the overlay, and delivers the text itself.
internal sealed partial class LocalDictationSession
{
    // How long a dictation waits for the plugin to accept it; after that the host handles it as usual.
    private static readonly TimeSpan TargetPrepareTimeout = TimeSpan.FromSeconds(1);
    private bool _targetChecked;
    // The plugin that lists the target app, from the moment it is asked until it declines.
    private PortableDictationTarget? _targetPlugin;
    private Task<DictationTargetPlan?>? _targetPlanTask;
    // Set once the plugin accepted the dictation.
    private DictationTargetPlan? _targetPlan;
    private Guid _targetDictationId;
    // Keeps TypeWhisper's indicator hidden while the plugin decides, so it does not flash over the app.
    private bool _targetPending;
    private DictationTargetSteps _targetSteps = new();
    // Whether the plugin's delivery put text into the app; a copy or a workflow action completes without it.
    private bool _targetReached;
    // Fires when the app stops showing a long recording or transcription, so TypeWhisper's indicator returns.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _targetIndicatorTimer;

    /// <summary>Whether a plugin shows the current dictation in its app, so TypeWhisper's own indicator stays hidden.
    /// An app that is no longer in front cannot show that the microphone is still open, so it never counts then.</summary>
    internal bool ShowsTargetIndicator => (_targetPending || _targetSteps.Shown) && NativeMethods.GetForegroundWindow() == _target;

    private void ResetDictationTarget()
    {
        _targetPlugin = null;
        _targetPlanTask = null;
        _targetPlan = null;
        _targetDictationId = Guid.Empty;
        _targetPending = false;
        _targetSteps = new();
        _targetReached = false;
        _targetIndicatorTimer?.Stop();
    }

    // Once per dictation, after the target app is known: a plugin that lists the app is asked whether it takes the
    // dictation over. Other apps never reach a plugin.
    private void BeginDictationTarget()
    {
        if (_targetChecked) return;
        _targetChecked = true;
        ResetDictationTarget();
        if (_setupOutputAtStart is not null) return;
        if (PluginRuntime.DictationTargets.FirstOrDefault(item => item.Handles(_targetApp)) is not { } target) return;
        var id = _targetDictationId = Guid.NewGuid();
        _targetPlugin = target;
        _targetPending = true;
        var info = new DictationTargetInfo(id, _targetApp, ProcessImagePath(_targetProcessId));
        _targetPlanTask = PrepareDictationTargetAsync(target, info, _operationCancellation.Token);
        _ = AcceptDictationTargetAsync(id, _targetPlanTask);
    }

    // The plugin's code runs off the UI thread; a plugin that does not answer in time leaves the dictation to the host.
    private async Task<DictationTargetPlan?> PrepareDictationTargetAsync(PortableDictationTarget target, DictationTargetInfo info, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TargetPrepareTimeout);
            return await Task.Run(() => PluginRuntime.UseDictationTargetAsync(target,
                (plugin, token) => plugin.PrepareDictationAsync(info, token), timeout.Token)).WaitAsync(TargetPrepareTimeout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write(AppDiagnostics.PluginStage("dictation.target.prepare-failed", target.PluginId), ex);
            return null;
        }
    }

    private async Task AcceptDictationTargetAsync(Guid id, Task<DictationTargetPlan?> planTask)
    {
        var plan = await planTask;
        if (_disposed || _targetDictationId != id) return;
        _targetPending = false;
        AppDiagnostics.Write($"dictation.target plugin={_targetPlugin?.PluginId} accepted={plan is not null} translate={plan?.TranslateTo is not null}");
        if (plan is null) _targetPlugin = null;
        else
        {
            _targetPlan = plan;
            ObserveDictationTarget();
        }
        Changed?.Invoke();
    }

    // The plan of the plugin that took the dictation over, or null when the host handles it.
    private async Task<DictationTargetPlan?> DictationTargetPlanAsync() => _targetPlanTask is { } task ? await task : null;

    // Reports the dictation's visible phase to the plugin that took it over.
    private void ObserveDictationTarget()
    {
        if (_targetPlugin is not { } target || _targetPlan is null) return;
        if (_targetSteps.Next(OverlayState.Phase, _audio.IsRecording, _targetReached) is not { } step) return;
        DateTimeOffset? shownUntil = null;
        var id = _targetDictationId;
        var input = TargetInput();
        try { PluginRuntime.TryUseDictationTarget(target, plugin => plugin.OnDictationPhaseChanged(id, step, input), out shownUntil); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            shownUntil = null;
            AppDiagnostics.Write(AppDiagnostics.PluginStage("dictation.target.step-failed", target.PluginId), ex);
        }
        _targetSteps.Reported(step, shownUntil);
        AppDiagnostics.Write($"dictation.target.step step={step} shown={shownUntil is not null}");
        if (_targetSteps.ExpiresAt is not { } expires || _targetIndicatorTimer is not { } timer) return;
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, (expires - DateTimeOffset.UtcNow).TotalMilliseconds) + 250);
        timer.Start();
    }

    private void CreateTargetIndicatorTimer(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _targetIndicatorTimer = dispatcher.CreateTimer();
        _targetIndicatorTimer.IsRepeating = false;
        _targetIndicatorTimer.Tick += (_, _) => { if (!_disposed) Changed?.Invoke(); };
    }

    // Partial delivery reports failure, so the review and the clipboard keep the full text for the parts that did not arrive.
    private async Task<bool> DeliverToTargetAsync(string text)
    {
        if (_targetPlugin is not { } target) return false;
        var id = _targetDictationId;
        var input = TargetInput();
        DictationDeliveryResult result;
        try
        {
            result = await PluginRuntime.UseDictationTargetAsync(target,
                (plugin, ct) => plugin.DeliverDictationAsync(id, text, input, ct), _operationCancellation.Token);
        }
        catch (OperationCanceledException) when (_operationCancellation.Token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write(AppDiagnostics.PluginStage("dictation.target.delivery-failed", target.PluginId), ex);
            return false;
        }
        if (_targetDictationId == id) _targetReached = result.Delivered > 0;
        AppDiagnostics.Write(result.Completed
            ? $"dictation.target.delivered parts={result.Parts}"
            : $"dictation.target.stopped stage={result.StoppedAt ?? "unknown"} parts={result.Parts} delivered={result.Delivered}");
        return result.Completed;
    }

    // Keys and pastes for the plugin reach only the dictation's own window; pasting follows the dictation's settings.
    private DictationTargetInput TargetInput()
    {
        var target = _target;
        return new(target, async (part, ct) => (await _inserter.InsertAsync(part, target, () =>
            !_disposed && !_operationCancellation.Token.IsCancellationRequested && !ct.IsCancellationRequested
            && _outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste,
            preserveClipboard: () => _outputAtStart.RestrictedBy(OutputPreferences.Current).PreserveClipboard)).Inserted);
    }

    // A translation the plugin asks for joins as the lowest-ranked App rule for the target app.
    private Workflow? TargetTranslationWorkflow(DictationTargetPlan plan) =>
        DictationTargetTranslation.Workflow(_targetApp, plan.AppName ?? _targetPlugin?.Name ?? _targetApp, plan.TranslateTo);

    /// <summary>The shared workflow LLM by display name and whether it can run now, or null when none is chosen.
    /// Plugins read it, for example to name the LLM that translates their app's messages.</summary>
    internal PluginLlmStatus? DefaultLlmStatus()
    {
        try
        {
            var probe = new Workflow
            {
                Id = "default-llm-probe", Name = "", Template = WorkflowTemplate.Translation, Trigger = WorkflowTrigger.Global(),
                Behavior = new() { TranslationTarget = "English", ProviderOverride = WorkflowLlmDefaults.Inherit }
            };
            var behavior = WorkflowDefaults.Resolve(probe).Behavior;
            if (behavior.ProviderOverride is not { Length: > 0 } provider || provider == WorkflowLlmDefaults.Inherit
                || behavior.ModelOverride is not { Length: > 0 } model) return null;
            var names = LlmDisplayNames(provider, model);
            return new($"{names.Provider} · {names.Model}", TranslationLlmReady(probe));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private bool TranslationLlmReady(Workflow translation)
    {
        try
        {
            var behavior = WorkflowDefaults.Resolve(translation).Behavior;
            return behavior.ProviderOverride is { Length: > 0 } provider && behavior.ModelOverride is { Length: > 0 } model
                && LlmProviders.Any(item => item.SelectionId == provider && item.Ready && item.Models.Any(candidate => candidate.Id == model));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    // The executable's path with the least access a process grants, so the app is never opened for reading memory.
    private static string? ProcessImagePath(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }
}
