using Microsoft.UI.Dispatching;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private EscapeCancelHook? _escapeCancelHook;
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
            // so a consumed Escape never reaches their gesture state.
            _escapeCancelHook = new(() => EscapeCancelTarget is not null, () => DispatcherQueue.TryEnqueue(HandleEscapeCancel));
            _escapeWarningTimer = OneShotTimer(EscapeCancelConfirmation.WindowMilliseconds + 50, ObserveEscapeCancelTarget);
            _cancelledBannerTimer = OneShotTimer(EscapeCancelConfirmation.BannerMilliseconds, () => _dictation.Cancelled = false);
            _dictation.Changed += () => DispatcherQueue.TryEnqueue(ObserveEscapeCancelTarget);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Escape cancel hook failed: {0}", ex);
            ShowActivationNotice("Esc cannot cancel dictation in this session. Use the dictation shortcut or restart TypeWhisper.");
        }
    }

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
                ? "Press Esc again to cancel recording" : "Press Esc again to cancel transcription";
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
            System.Diagnostics.Trace.TraceError("Escape cancellation failed: {0}", ex);
            if (!_closing) MetricsText.Text = "Could not finish cancellation. Try again.";
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
        _escapeCancelHook?.Dispose();
    }
}
