using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// UI-thread owner: native registration and the persisted workflow catalog change together.
internal sealed class WorkflowShortcutCatalog(ManualWorkflowStore store, IProcessingCancelShortcutBackend backend,
    Func<string, string?> reservedConflict)
{
    private Dictionary<string, Workflow> _active = new(StringComparer.Ordinal);
    internal string? Error { get; private set; }
    internal string ActiveValue => backend.Value;
    internal static string Canonical(string value) => string.Join(",", ShortcutRules.Split(value)
        .Select(ShortcutRules.Normalize).Distinct(StringComparer.Ordinal));

    internal string? Initialize()
    {
        try { Apply(store.Read(), () => { }); return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Error = "Workflow shortcuts are unavailable. " + ex.Message; }
    }

    internal Workflow? Resolve(string chord) => _active.TryGetValue(ShortcutRules.Normalize(chord), out var workflow)
        ? Snapshot(workflow) : null;

    internal string? Conflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(ActiveValue, Canonical(value), modifierOnly)
            ? "Already used by a workflow. Change its shortcut in Workflows first." : null;

    internal string? ValidateDraft(string id, string value, bool enabled)
    {
        try
        {
            ValidateValue(value);
            if (!enabled) return null;
            var other = store.Read().Where(w => w.Id != id).ToArray();
            var bindings = Build(other);
            foreach (var chord in ShortcutRules.Split(Canonical(value)))
            {
                if (reservedConflict(chord) is { } conflict) return conflict;
                if (bindings.TryGetValue(chord, out var owner)) return "Already used by " + owner.Name + ".";
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ex.Message; }
    }

    internal void Save(Workflow workflow)
    {
        var items = store.Read().Where(w => w.Id != workflow.Id).Append(workflow).ToArray();
        Apply(items, () => store.Save(workflow, allowAutomatic: true));
    }

    internal Workflow SetEnabled(string id, bool enabled)
    {
        var items = store.Read();
        Workflow? updated = null;
        Apply(items.Select(w => w.Id == id ? w with { IsEnabled = enabled } : w).ToArray(),
            () => updated = store.SetEnabled(id, enabled));
        return updated!;
    }

    internal void Delete(string id) => Apply(store.Read().Where(w => w.Id != id).ToArray(),
        () => store.Delete(id, allowAutomatic: true));

    private void Apply(IReadOnlyList<Workflow> items, Action persist)
    {
        var next = Build(items);
        var previous = backend.Value;
        var requested = string.Join(",", next.Keys);
        try
        {
            if (backend.TryChange(requested) is { } error) throw new InvalidOperationException(error);
            if (!SameBindings(backend.Value, requested)) throw new InvalidOperationException("Native shortcut registration was incomplete.");
            persist();
            _active = next;
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string? rollback;
            try { rollback = backend.TryChange(previous); }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
            { rollback = "Native shortcut registration could not be restored."; }
            if (rollback is not null || !SameBindings(backend.Value, previous))
            {
                // A partly restored native chord must never invoke a different or unsaved workflow.
                _active.Clear();
                Error = "Workflow changes could not be saved and shortcut registration could not be restored. Workflow shortcuts are suspended; reassign them or restart.";
            }
            else Error = "Workflow changes could not be saved. Previous shortcuts still apply. " + ex.Message;
            throw new InvalidOperationException(Error, ex);
        }
    }

    private Dictionary<string, Workflow> Build(IEnumerable<Workflow> items)
    {
        var result = new Dictionary<string, Workflow>(StringComparer.Ordinal);
        foreach (var workflow in items.Where(w => w.IsEnabled && (ManualWorkflowStore.IsSelectedTextShortcut(w) || ManualWorkflowStore.IsDictationShortcut(w))))
        {
            var value = string.Join(",", workflow.Trigger.Hotkeys);
            ValidateValue(value);
            // Copy every mutable collection used by this supported subset before publishing callbacks.
            var snapshot = Snapshot(workflow);
            foreach (var chord in ShortcutRules.Split(Canonical(value)))
            {
                if (reservedConflict(chord) is { } conflict) throw new InvalidOperationException(workflow.Name + ": " + conflict);
                if (!result.TryAdd(chord, snapshot)) throw new InvalidOperationException(chord + " is assigned to more than one enabled workflow.");
                if (result.Count > 128) throw new InvalidOperationException("At most 128 workflow shortcuts can be active.");
            }
        }
        return result;
    }

    private static Workflow Snapshot(Workflow workflow) => workflow with
    {
        Trigger = workflow.Trigger with { Hotkeys = workflow.Trigger.Hotkeys.ToArray(), ProcessNames = workflow.Trigger.ProcessNames.ToArray(), WebsitePatterns = workflow.Trigger.WebsitePatterns.ToArray() },
        Behavior = workflow.Behavior with { Settings = new Dictionary<string, string>(workflow.Behavior.Settings), InputLanguageHints = workflow.Behavior.InputLanguageHints.ToArray() },
        Output = workflow.Output with { }
    };

    private static void ValidateValue(string value)
    {
        if (value.Length > 256 || ShortcutRules.Split(value).Length == 0 || value.Any(char.IsControl))
            throw new InvalidOperationException("Assign at least one shortcut (maximum 256 characters).");
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) throw new InvalidOperationException(error);
    }
    private static bool SameBindings(string left, string right) => ShortcutRules.Split(left).ToHashSet(StringComparer.Ordinal)
        .SetEquals(ShortcutRules.Split(right));
}
