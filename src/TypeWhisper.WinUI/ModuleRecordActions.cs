using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// How a module record action appears for a number of chosen records, as on macOS: without a selection title it is
// offered for one record only; beyond its maximum it is shown disabled with help.
internal readonly record struct ModuleRecordActionState(bool Visible, bool Enabled, string Title, string? Help)
{
    internal static ModuleRecordActionState For(ModuleRecordAction action, int count)
    {
        if (count < 1) return new(false, false, action.Title, null);
        if (count == 1) return new(true, true, action.Title, null);
        if (action.SelectionTitle is not { } title) return new(false, false, action.Title, null);
        if (count <= action.MaximumRecordCount) return new(true, true, title, null);
        var help = action.SelectionLimitHelp ?? (action.MaximumRecordCount == 1
            ? Loc.T("Select only one entry.")
            : Loc.T("Select at most {0} entries.", action.MaximumRecordCount));
        return new(true, false, title, help);
    }

    // Runs the action with the records' identifiers; a failure is reported, never thrown.
    internal static void Perform(ModuleRecordAction action, IReadOnlyList<string> ids, Action<string, Exception> report)
    {
        var state = For(action, ids.Count);
        if (!state.Enabled) return;
        var chosen = ids.ToArray();
        ModuleCalls.Run("record-action", action.Id, () => action.Perform(chosen), report);
    }
}
