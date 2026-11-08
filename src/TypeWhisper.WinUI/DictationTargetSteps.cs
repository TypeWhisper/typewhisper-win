using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

/// <summary>
/// Follows one dictation for the plugin that handles its target app: which step to report next, and until when the
/// app shows the dictation. Steps only move forward and each is reported once.
/// </summary>
internal sealed class DictationTargetSteps(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DictationTargetPhase? _last;
    private DateTimeOffset _shownUntil = DateTimeOffset.MinValue;

    /// <summary>Whether the app shows the dictation, so TypeWhisper's own indicator can stay hidden.</summary>
    internal bool Shown => _time.GetUtcNow() < _shownUntil;

    /// <summary>When the app stops showing a dictation that still records or transcribes, or null.</summary>
    internal DateTimeOffset? ExpiresAt => _last is DictationTargetPhase.Recording or DictationTargetPhase.Transcribing
        && Shown && _shownUntil != DateTimeOffset.MaxValue ? _shownUntil : null;

    /// <summary>The step for a visible phase. <paramref name="recording"/> tells whether the microphone is still open,
    /// <paramref name="delivered"/> whether the plugin's delivery put text into the app.</summary>
    internal static DictationTargetPhase StepFor(DictationPhase phase, bool recording, bool delivered) => phase switch
    {
        // A model that loads while the user still speaks belongs to the recording.
        DictationPhase.Recording => DictationTargetPhase.Recording,
        DictationPhase.LoadingModel when recording => DictationTargetPhase.Recording,
        DictationPhase.Processing or DictationPhase.LoadingModel => DictationTargetPhase.Transcribing,
        DictationPhase.Completed when delivered => DictationTargetPhase.Delivered,
        // Canceled, failed, copied, handled by an action, or nothing was said.
        _ => DictationTargetPhase.Stopped
    };

    /// <summary>
    /// The step to report for the visible phase, or null when there is nothing new: the step was reported already, it
    /// would go back, the dictation ended, or it stopped before anything was reported and so has nothing to end.
    /// </summary>
    internal DictationTargetPhase? Next(DictationPhase phase, bool recording, bool delivered)
    {
        if (_last is DictationTargetPhase.Delivered or DictationTargetPhase.Stopped) return null;
        var step = StepFor(phase, recording, delivered);
        if (_last is not { } last) return step == DictationTargetPhase.Stopped ? null : step;
        return step > last ? step : null;
    }

    /// <summary>Records a reported step and until when the app shows the dictation; null when it does not.</summary>
    internal void Reported(DictationTargetPhase step, DateTimeOffset? shownUntil)
    {
        _last = step;
        _shownUntil = shownUntil ?? DateTimeOffset.MinValue;
    }
}
