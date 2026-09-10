namespace TypeWhisper.Presentation;

/// <summary>Checks both directions when recording gestures can consist of modifiers alone.</summary>
public static class RecordingShortcutConflicts
{
    /// <summary>Detects equal chords and modifier-prefix collisions in either direction.</summary>
    public static bool Overlap(string first, bool firstAllowsModifiers, string second, bool secondAllowsModifiers) =>
        ProcessingCancelShortcut.Conflicts(first, second, secondAllowsModifiers)
        || ProcessingCancelShortcut.Conflicts(second, first, firstAllowsModifiers);
}
