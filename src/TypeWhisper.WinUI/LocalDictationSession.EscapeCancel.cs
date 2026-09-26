using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal EscapeCancelPreferencesStore EscapeCancelPreferences { get; } = new(WinUIProfile.DataPath("escape-cancel.json"));

    // Escape is only read when pressed, so a change also applies to the current dictation.
    internal string? SelectEscapeCancelBehavior(EscapeCancelBehavior behavior)
    {
        try { return EscapeCancelPreferences.Save(behavior); }
        finally { Changed?.Invoke(); }
    }

    internal string? CancelWarning
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (!_disposed) Changed?.Invoke();
        }
    }

    internal bool Cancelled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (!_disposed) Changed?.Invoke();
        }
    }
}
