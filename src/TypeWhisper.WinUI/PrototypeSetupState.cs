using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class PrototypeSetupState(SetupPreferencesStore store)
{
    internal static readonly string[] Steps = ["Welcome", "Microphone", "Shortcut", "Model", "Finish"];
    internal int Step { get; private set; } = store.Current.Completed ? 0 : store.Current.Step;
    internal string? MoveTo(int step)
    {
        var error = store.Save(Math.Clamp(step, 0, 4));
        if (error is null) Step = store.Current.Step;
        return error;
    }
    internal string? Complete() => store.Save(4, completed: true);
}
