namespace TypeWhisper.WinUI;

// Session-only navigation. Never reads permissions or modifies production settings.
internal sealed class PrototypeSetupState(Dictionary<string, string> values)
{
    internal static readonly string[] Steps = ["Welcome", "Microphone", "Shortcut", "Model", "Try it"];
    internal int Step { get; private set; } = Math.Clamp(int.TryParse(values.GetValueOrDefault("PrototypeSetup.Step"), out var saved) ? saved : 0, 0, 4);
    internal string? Validation => Step switch
    {
        2 when string.IsNullOrWhiteSpace(values.GetValueOrDefault("MainDictationHotkeys", "Ctrl+Shift+F9")) => "Add a dictation shortcut before continuing, or skip setup.",
        3 when !PrototypeModelSession.Models.Any(model => model.Id == new PrototypeModelSession(values).Active
            && new PrototypeModelSession(values).IsDownloaded(model.Id)) => "Choose a downloaded sample model before continuing, or skip setup.",
        _ => null
    };
    internal bool Next()
    {
        if (Validation is not null || Step == 4) return false;
        MoveTo(Step + 1); return true;
    }
    internal void Back() => MoveTo(Math.Max(0, Step - 1));
    internal void Revisit(int step) { if (step >= 0 && step < Step) MoveTo(step); }
    internal void Restart() => MoveTo(0);
    private void MoveTo(int step) { Step = step; values["PrototypeSetup.Step"] = step.ToString(); }
}
