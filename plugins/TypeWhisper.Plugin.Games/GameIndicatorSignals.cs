using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Tells a game add-on what one dictation is doing, so it can show the dictation in the game. Stages only move
/// forward and each is sent once. A delivered dictation sends nothing: the add-on sees the chat open and completes
/// its bar itself. Sending may fail, for example while the game is not in front or Alt is held; the stage still
/// moves on, and <see cref="Shown"/> says whether the game shows the dictation.
/// </summary>
internal sealed class GameIndicatorSignals(GameIndicatorChords chords, TimeProvider? time = null)
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

    /// <summary>
    /// Sends the chord for the dictation's next step through <paramref name="send"/>. Returns until when the game shows
    /// the dictation, or null when it does not.
    /// </summary>
    internal DateTimeOffset? Observe(DictationTargetPhase phase, Func<IReadOnlyList<ushort>, bool> send)
    {
        if (_stage != Stage.Finished)
        {
            switch (phase)
            {
                case DictationTargetPhase.Recording:
                    if (_stage == Stage.None) Show(Move(Stage.Recording, chords.Recording, send), RecordingLimit);
                    break;
                case DictationTargetPhase.Transcribing:
                    if (_stage is Stage.None or Stage.Recording) Show(Move(Stage.Transcribing, chords.Transcribing, send), TranscribingLimit);
                    break;
                case DictationTargetPhase.Delivered:
                    // The add-on sees the chat open, completes and fades its bar by itself, so a shown bar stays the
                    // indicator for this result.
                    _stage = Stage.Finished;
                    if (Shown) _shownUntil = DateTimeOffset.MaxValue;
                    break;
                default:
                    // Canceled, failed, copied, handled by an action, or nothing was said: no text reaches the chat, so
                    // the bar stops instead of waiting for it, and TypeWhisper's own indicator says what happened.
                    // Before the first signal the add-on shows nothing to stop.
                    _shownUntil = DateTimeOffset.MinValue;
                    if (_stage == Stage.None) break;
                    Move(Stage.Finished, chords.Stopped, send);
                    break;
            }
        }
        return Shown ? _shownUntil : null;
    }

    private void Show(bool sent, TimeSpan limit) => _shownUntil = sent ? _time.GetUtcNow() + limit : DateTimeOffset.MinValue;

    private bool Move(Stage stage, IReadOnlyList<ushort> chord, Func<IReadOnlyList<ushort>, bool> send)
    {
        _stage = stage;
        return send(chord);
    }
}
