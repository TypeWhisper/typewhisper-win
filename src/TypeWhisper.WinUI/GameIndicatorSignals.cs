using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

/// <summary>
/// Tells a game add-on what one dictation is doing, so it can show the dictation in the game. Stages only move
/// forward and each is sent once. A finished dictation sends nothing: the add-on sees the chat open and completes
/// its bar itself. <paramref name="send"/> may fail, for example while the game is not in front or Alt is held; the
/// stage still moves on, and <see cref="Shown"/> says whether the game shows the dictation.
/// </summary>
internal sealed class GameIndicatorSignals(GameIndicatorChords chords, Func<IReadOnlyList<ushort>, bool> send, TimeProvider? time = null)
{
    /// <summary>How long the add-on keeps its bar without a new chord before it fades it out, so a lost chord cannot
    /// leave a bar behind. They match RECORDING_TIMEOUT and TRANSCRIBING_TIMEOUT in the add-on.</summary>
    internal static readonly TimeSpan RecordingLimit = TimeSpan.FromSeconds(300), TranscribingLimit = TimeSpan.FromSeconds(60);

    private enum Stage { None, Recording, Transcribing, Finished }
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Stage _stage;
    private DateTimeOffset _shownUntil = DateTimeOffset.MinValue;

    /// <summary>Whether the game shows the dictation: the last recording or transcribing chord reached it and its bar
    /// has not timed out. Otherwise TypeWhisper keeps its own indicator.</summary>
    internal bool Shown => _time.GetUtcNow() < _shownUntil;

    /// <summary>When the game's bar times out while the dictation is still recording or transcribing, or null.</summary>
    internal DateTimeOffset? ExpiresAt => _stage is Stage.Recording or Stage.Transcribing && Shown ? _shownUntil : null;

    /// <summary>Observes the visible phase; <paramref name="recording"/> tells whether the microphone is still open, and
    /// <paramref name="reachedChat"/> whether the dictation's text went into the game's chat. A dictation can complete
    /// without it: copied instead of pasted, or handled by a workflow action.</summary>
    internal void Observe(DictationPhase phase, bool recording, bool reachedChat = true)
    {
        if (_stage == Stage.Finished) return;
        switch (phase)
        {
            // A model that loads while the user still speaks belongs to the recording.
            case DictationPhase.Recording:
            case DictationPhase.LoadingModel when recording:
                if (_stage == Stage.None) Show(Move(Stage.Recording, chords.Recording), RecordingLimit);
                break;
            case DictationPhase.Processing or DictationPhase.LoadingModel:
                if (_stage is Stage.None or Stage.Recording) Show(Move(Stage.Transcribing, chords.Transcribing), TranscribingLimit);
                break;
            case DictationPhase.Completed when reachedChat:
                // The add-on sees the chat open, completes and fades its bar by itself, so a shown bar stays the
                // indicator for this result.
                _stage = Stage.Finished;
                if (Shown) _shownUntil = DateTimeOffset.MaxValue;
                break;
            default:
                // Canceled, failed, copied, handled by an action, or nothing was said: no text reaches the chat, so the
                // bar stops instead of waiting for it, and TypeWhisper's own indicator says what happened. Before the
                // first signal the add-on shows nothing to stop.
                _shownUntil = DateTimeOffset.MinValue;
                if (_stage == Stage.None) break;
                Move(Stage.Finished, chords.Stopped);
                break;
        }
    }

    private void Show(bool sent, TimeSpan limit) => _shownUntil = sent ? _time.GetUtcNow() + limit : DateTimeOffset.MinValue;

    private bool Move(Stage stage, IReadOnlyList<ushort> chord)
    {
        _stage = stage;
        return send(chord);
    }
}
