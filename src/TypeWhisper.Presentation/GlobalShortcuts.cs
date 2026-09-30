namespace TypeWhisper.Presentation;

/// <summary>How a global shortcut takes part in conflict checks.</summary>
public enum GlobalShortcutRole
{
    /// <summary>Main dictation; its chords may consist of modifiers alone.</summary>
    MainDictation,
    /// <summary>Cancels final processing or a running selected-text workflow.</summary>
    CancelProcessing,
    /// <summary>Push to talk, toggle or hold recording; chords may consist of modifiers alone.</summary>
    Recording,
    /// <summary>A single app action, such as Recorder or Copy last transcription.</summary>
    Action,
    /// <summary>The shortcuts of all enabled workflows together.</summary>
    Workflows
}

/// <summary>A global shortcut other shortcuts must not overlap.</summary>
/// <param name="Key">Its settings key.</param>
/// <param name="Role">How it takes part in conflict checks.</param>
/// <param name="Label">Its name in conflict messages.</param>
public sealed record GlobalShortcut(string Key, GlobalShortcutRole Role, string Label)
{
    /// <summary>Whether its chords may consist of modifiers alone, which then block every chord containing them.</summary>
    public bool AllowsModifierOnly => Role is GlobalShortcutRole.MainDictation or GlobalShortcutRole.Recording;
}

/// <summary>The single list of global shortcuts and the conflict check every assignment goes through.</summary>
public static class GlobalShortcuts
{
    /// <summary>Settings key of the main dictation shortcuts.</summary>
    public const string MainDictation = "MainDictationHotkeys";
    /// <summary>Settings key of the cancel processing shortcuts.</summary>
    public const string CancelProcessing = "CancelProcessingHotkeys";
    /// <summary>Settings key of the push-to-talk shortcuts.</summary>
    public const string PushToTalk = "PushToTalkHotkey";
    /// <summary>Settings key of the toggle recording shortcuts.</summary>
    public const string ToggleOnly = "ToggleOnlyHotkeys";
    /// <summary>Settings key of the hold-to-record shortcuts.</summary>
    public const string HoldOnly = "HoldOnlyHotkeys";
    /// <summary>Settings key of the Recorder shortcuts.</summary>
    public const string Recorder = "RecorderToggleHotkeys";
    /// <summary>Settings key of the workflow palette shortcuts.</summary>
    public const string WorkflowPalette = "WorkflowPaletteHotkeys";
    /// <summary>Settings key of the recent transcriptions shortcuts.</summary>
    public const string RecentTranscriptions = "RecentTranscriptionsHotkeys";
    /// <summary>Settings key of the read last transcription shortcuts.</summary>
    public const string ReadLastTranscription = "ReadLastTranscriptionHotkeys";
    /// <summary>Settings key of the copy last transcription shortcuts.</summary>
    public const string CopyLastTranscription = "CopyLastTranscriptionHotkeys";
    /// <summary>Settings key of the paste last transcription shortcuts.</summary>
    public const string PasteLastTranscription = "PasteLastTranscriptionHotkeys";
    /// <summary>Workflow shortcuts are stored with their workflows, not under a settings key.</summary>
    public const string Workflows = "Workflows";

    /// <summary>Every global shortcut in check order; the first overlap decides the message.</summary>
    public static IReadOnlyList<GlobalShortcut> All { get; } =
    [
        new(MainDictation, GlobalShortcutRole.MainDictation, "Main dictation"),
        new(CancelProcessing, GlobalShortcutRole.CancelProcessing, "Cancel processing"),
        new(PushToTalk, GlobalShortcutRole.Recording, "Push to talk"),
        new(ToggleOnly, GlobalShortcutRole.Recording, "Toggle recording"),
        new(HoldOnly, GlobalShortcutRole.Recording, "Hold to record"),
        new(Recorder, GlobalShortcutRole.Action, "Recorder"),
        new(WorkflowPalette, GlobalShortcutRole.Action, "Workflow palette"),
        new(RecentTranscriptions, GlobalShortcutRole.Action, "Recent transcriptions"),
        new(ReadLastTranscription, GlobalShortcutRole.Action, "Read last transcription"),
        new(CopyLastTranscription, GlobalShortcutRole.Action, "Copy last transcription"),
        new(PasteLastTranscription, GlobalShortcutRole.Action, "Paste last transcription"),
        new(Workflows, GlobalShortcutRole.Workflows, "Workflows")
    ];

    /// <summary>
    /// Returns why canonical <paramref name="value"/> cannot be assigned to <paramref name="key"/>, or null.
    /// <paramref name="registered"/> reads the canonical chords currently registered for a key ("" when none).
    /// </summary>
    public static string? FindConflict(string key, string value, Func<string, string> registered)
    {
        var candidate = All.Single(shortcut => shortcut.Key == key);
        foreach (var other in All)
            if (other.Key != key && Overlap(value, candidate.AllowsModifierOnly, registered(other.Key), other.AllowsModifierOnly))
                return Message(candidate, other);
        return null;
    }

    /// <summary>Detects equal chords and modifier-only prefixes in either direction.</summary>
    public static bool Overlap(string first, bool firstAllowsModifiers, string second, bool secondAllowsModifiers) =>
        Covers(first, second, secondAllowsModifiers) || Covers(second, first, firstAllowsModifiers);

    // Equal chords, or a modifier-only chord of other whose modifiers all appear in a chord of value.
    private static bool Covers(string value, string otherValue, bool otherAllowsModifierOnly)
    {
        foreach (var chord in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var other in otherValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (chord == other) return true;
            var parts = other.Split('+');
            if (otherAllowsModifierOnly && parts.Length > 0 && parts.All(IsModifier) && parts.All(chord.Split('+').Contains)) return true;
        }
        return false;
    }
    private static bool IsModifier(string value) => value is "CTRL" or "ALT" or "SHIFT" or "WIN";

    private static string Message(GlobalShortcut candidate, GlobalShortcut other) => other.Role switch
    {
        GlobalShortcutRole.MainDictation => candidate.Role == GlobalShortcutRole.Recording
            ? "Already used by Main dictation. Choose a different combination."
            : "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.",
        GlobalShortcutRole.CancelProcessing => candidate.Role == GlobalShortcutRole.MainDictation
            ? "This dictation shortcut overlaps Cancel processing. Change the cancel shortcut first."
            : "Already used by Cancel processing.",
        GlobalShortcutRole.Recording => "Already used by another recording shortcut. Choose a different combination.",
        GlobalShortcutRole.Workflows => "Already used by a workflow. Change its shortcut in Workflows first.",
        _ => $"Already used by {other.Label}. Change that shortcut first."
    };
}
