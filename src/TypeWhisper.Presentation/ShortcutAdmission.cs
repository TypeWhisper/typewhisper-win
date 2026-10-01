namespace TypeWhisper.Presentation;

/// <summary>Protects running operations when a shortcut asks to open a window or panel.</summary>
public static class ShortcutAdmission
{
    /// <summary>Returns a visible refusal while other work runs, or null when <paramref name="destination"/> can open.</summary>
    /// <param name="destination">What the shortcut opens, as used in the sentence, such as "the recorder".</param>
    /// <param name="operationBusy">Whether recording, processing or another operation is running.</param>
    public static string? Rejection(string destination, bool operationBusy) => operationBusy
        ? Loc.T("Finish the current operation before opening {0}. Your work is kept intact.", destination) : null;
}
