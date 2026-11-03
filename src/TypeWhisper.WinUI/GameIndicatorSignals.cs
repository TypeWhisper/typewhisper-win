using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

/// <summary>
/// Tells a game add-on what one dictation is doing, so it can show the dictation in the game. Stages only move
/// forward and each is sent once. A finished dictation sends nothing: the add-on sees the chat open and completes
/// its bar itself. <paramref name="send"/> may fail, for example while the game is not in front; the stage still moves on.
/// </summary>
internal sealed class GameIndicatorSignals(GameIndicatorChords chords, Func<IReadOnlyList<ushort>, bool> send)
{
    private enum Stage { None, Recording, Transcribing, Finished }
    private Stage _stage;

    /// <summary>Observes the visible phase; <paramref name="recording"/> tells whether the microphone is still open.</summary>
    internal void Observe(DictationPhase phase, bool recording)
    {
        if (_stage == Stage.Finished) return;
        switch (phase)
        {
            // A model that loads while the user still speaks belongs to the recording.
            case DictationPhase.Recording:
            case DictationPhase.LoadingModel when recording:
                if (_stage == Stage.None) Move(Stage.Recording, chords.Recording);
                break;
            case DictationPhase.Processing or DictationPhase.LoadingModel:
                if (_stage is Stage.None or Stage.Recording) Move(Stage.Transcribing, chords.Transcribing);
                break;
            case DictationPhase.Completed:
                _stage = Stage.Finished;
                break;
            default:
                // Canceled, failed, copied, or nothing was said. Before the first signal the add-on shows nothing to stop.
                if (_stage == Stage.None) break;
                Move(Stage.Finished, chords.Stopped);
                break;
        }
    }

    private void Move(Stage stage, IReadOnlyList<ushort> chord)
    {
        _stage = stage;
        send(chord);
    }
}
