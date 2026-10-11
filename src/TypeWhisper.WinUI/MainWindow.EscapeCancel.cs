using Microsoft.UI.Dispatching;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private DictationKeyHook? _dictationKeyHook;
    private readonly EscapeCancelConfirmation _escapeConfirmation = new();
    private DispatcherQueueTimer? _escapeWarningTimer;
    private DispatcherQueueTimer? _cancelledBannerTimer;

    private EscapeCancelTarget? EscapeCancelTarget => _closing || _profileRestoreClosing ? null
        : _dictationInput?.IsRecordingOrStarting == true ? TypeWhisper.Presentation.EscapeCancelTarget.Recording
        : _dictation.CanCancelProcessing ? TypeWhisper.Presentation.EscapeCancelTarget.Processing : null;

    private void InitializeEscapeCancel()
    {
        if (_closing || _profileRestoreClosing) return;
        try
        {
            // Install after the dictation hooks: Windows calls the newest low-level hook first,
            // so a consumed Escape or Enter never reaches their gesture state.
            _dictationKeyHook = new(() => EscapeCancelTarget is not null, () => DispatcherQueue.TryEnqueue(HandleEscapeCancel),
                EnterSubmitAvailable, SubmitDictationWithEnter, HeldByDictationShortcut,
                () => AppDiagnostics.Write("shortcut.enter-passed decision=modifiers"));
            _escapeWarningTimer = OneShotTimer(EscapeCancelConfirmation.WindowMilliseconds + 50, ObserveEscapeCancelTarget);
            _cancelledBannerTimer = OneShotTimer(EscapeCancelConfirmation.BannerMilliseconds, () => _dictation.Cancelled = false);
            _dictation.Changed += () => DispatcherQueue.TryEnqueue(ObserveEscapeCancelTarget);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppDiagnostics.Write("escape.hook.failed", ex);
            ShowActivationNotice(Loc.T("Esc cannot cancel dictation and Enter cannot submit it in this session. Use the dictation shortcut or restart TypeWhisper."));
        }
    }

    private bool EnterSubmitAvailable()
    {
        if (_closing || _profileRestoreClosing || _dictationInput?.IsRecordingOrStarting != true) return false;
        if (_dictation.CanRequestSubmit) return true;
        // Only Enter during a recording is logged, never which other keys are typed.
        AppDiagnostics.Write("shortcut.enter-passed decision=" + _dictation.SubmitUnavailableReason.ToLowerInvariant());
        return false;
    }

    // Windows calls the newest low-level hook first. The shortcut hooks installed after this one would otherwise see
    // a consumed Esc or Enter in their gesture state, so it is reinstalled once they exist.
    private void MoveDictationKeyHookToFront()
    {
        if (_closing || _profileRestoreClosing || _dictationKeyHook is null) return;
        if (_dictationKeyHook.Recover() is not null) AppDiagnostics.Write("escape.hook.reorder-failed");
    }

    // Runs inside the keyboard hook callback, like a stop shortcut: the coordinator takes the stop right away.
    private void SubmitDictationWithEnter()
    {
        if (!_dictation.RequestSubmit()) return;
        AppDiagnostics.Write("shortcut.enter-submit");
        _ = _dictationInput?.SubmitAsync(TypeWhisper.Presentation.DictationInputAction.Stop);
    }

    private bool HeldByDictationShortcut(int key) => _dictationHotkey?.HoldsKey(key) == true
        || _recordingShortcuts.Values.Any(entry => entry.Registration.HoldsKey(key));

    private DispatcherQueueTimer OneShotTimer(long milliseconds, Action tick)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private async void HandleEscapeCancel()
    {
        var behavior = _dictation.EscapeCancelPreferences.Current;
        if (EscapeCancelTarget is not { } target) return;
        if (_escapeConfirmation.Press(target, behavior, Environment.TickCount64) == EscapeCancelDecision.Warn)
        {
            _dictation.CancelWarning = target == TypeWhisper.Presentation.EscapeCancelTarget.Recording
                ? Loc.T("Press Esc again to cancel recording") : Loc.T("Press Esc again to cancel transcription");
            // Start() alone does not restart a running DispatcherQueueTimer.
            _escapeWarningTimer?.Stop();
            _escapeWarningTimer?.Start();
            return;
        }
        _escapeWarningTimer?.Stop();
        _dictation.CancelWarning = null;
        try
        {
            if (target == TypeWhisper.Presentation.EscapeCancelTarget.Recording)
            {
                _dictation.RequestCancel();
                if (_dictationInput is { } input) await input.SubmitAsync(TypeWhisper.Presentation.DictationInputAction.Cancel);
            }
            else if (_dictation.CanCancelProcessing) await _dictation.CancelAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.WriteFailure("escape.cancel.failed", ex);
            if (!_closing) ShowNotice(new AppNotice(Loc.T("Could not finish cancellation. Try again.")));
            return;
        }
        // Escape cancels dictation only; a selected-text workflow keeps its own cancel shortcut.
        if (_closing || behavior == EscapeCancelBehavior.Instant || _dictation.OverlayState.Phase != DictationPhase.Idle) return;
        _dictation.Cancelled = true;
        _cancelledBannerTimer?.Stop();
        _cancelledBannerTimer?.Start();
    }

    private void ObserveEscapeCancelTarget()
    {
        if (_escapeConfirmation.Observe(EscapeCancelTarget, Environment.TickCount64)) _dictation.CancelWarning = null;
        if (_escapeConfirmation.Armed is null) _escapeWarningTimer?.Stop();
        // A new dictation replaces the banner of the cancelled one.
        if (_dictation.Cancelled && _dictation.OverlayState.Phase != DictationPhase.Idle)
        {
            _cancelledBannerTimer?.Stop();
            _dictation.Cancelled = false;
        }
    }

    private void DisposeEscapeCancel()
    {
        _escapeWarningTimer?.Stop();
        _cancelledBannerTimer?.Stop();
        _dictationKeyHook?.Dispose();
    }
}
