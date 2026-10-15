using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>A provider/model pair inherited only by workflows explicitly using the default.</summary>
public sealed record WorkflowLlmSelection(string Provider, string Model);

/// <summary>
/// Stores a local default without changing any workflow's own selection. Machine policy can enforce one pair for
/// every workflow (<c>LlmProviderId</c>/<c>LlmModelId</c>) or recommend the default for a profile that has not saved
/// its own; neither is ever written to the file.
/// </summary>
/// <param name="path">The JSON file holding the user's saved default.</param>
/// <param name="enforced">The pair policy enforces, or null; evaluated on every read so policy changes apply at once.</param>
/// <param name="recommended">The pair policy recommends as the initial default, or null.</param>
public sealed class WorkflowLlmDefaults(string path, Func<WorkflowLlmSelection?>? enforced = null, Func<WorkflowLlmSelection?>? recommended = null)
{
    /// <summary>Explicit inheritance marker; absent legacy selections retain their meaning.</summary>
    public const string Inherit = "__default__";
    /// <summary>The pair every workflow must use, or null when policy does not fix the text model.</summary>
    public WorkflowLlmSelection? Enforced => enforced?.Invoke();
    /// <summary>The effective default: the enforced pair, then the saved pair, then an administrator recommendation.</summary>
    public WorkflowLlmSelection? Read() => Enforced ?? ReadSaved() ?? recommended?.Invoke();
    /// <summary>Reads the persisted pair, reporting malformed data.</summary>
    public WorkflowLlmSelection? ReadSaved()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException(Loc.T("Default LLM settings are too large."));
        var value = JsonSerializer.Deserialize<WorkflowLlmSelection>(File.ReadAllText(path))
            ?? throw new InvalidDataException(Loc.T("Default LLM settings are invalid."));
        Validate(value);
        return value;
    }
    /// <summary>Atomically replaces the default pair after validation. Refused while policy enforces the pair.</summary>
    public void Save(WorkflowLlmSelection value)
    {
        if (Enforced is not null) throw new InvalidOperationException(Loc.T("The text model is managed by your organization."));
        Validate(value);
        AtomicFileWriter.WriteAllText(path, JsonSerializer.Serialize(value));
    }
    /// <summary>The pair a workflow with this selection actually uses: the enforced pair, then an inherited default, then its own.</summary>
    public WorkflowLlmSelection? Effective(string? provider, string? model) =>
        Enforced ?? (provider == Inherit ? Read() : provider is null ? null : new(provider, model ?? ""));
    /// <summary>Resolves inheritance once without modifying the saved workflow. An enforced pair replaces every selection.</summary>
    public Workflow Resolve(Workflow workflow)
    {
        if (workflow.Template == WorkflowTemplate.Dictation) return workflow;
        if (Enforced is { } required)
            return workflow.Behavior.ProviderOverride == required.Provider && workflow.Behavior.ModelOverride == required.Model ? workflow
                : workflow with { Behavior = workflow.Behavior with { ProviderOverride = required.Provider, ModelOverride = required.Model } };
        return workflow.Behavior.ProviderOverride != Inherit ? workflow : Apply(workflow, Read());
    }
    /// <summary>Applies a captured pair only to an explicitly inheriting workflow.</summary>
    public static Workflow Apply(Workflow workflow, WorkflowLlmSelection? defaults) => workflow.Template == WorkflowTemplate.Dictation || workflow.Behavior.ProviderOverride != Inherit ? workflow
        : workflow with { Behavior = workflow.Behavior with { ProviderOverride = defaults?.Provider, ModelOverride = defaults?.Model } };
    private static void Validate(WorkflowLlmSelection value)
    {
        if (string.IsNullOrWhiteSpace(value.Provider) || value.Provider is Inherit or "none"
            || string.IsNullOrWhiteSpace(value.Model) || value.Provider.Length > 1024 || value.Model.Length > 1024
            || value.Provider.Any(char.IsControl) || value.Model.Any(char.IsControl))
            throw new InvalidDataException(Loc.T("Choose a default LLM provider and model."));
    }
}
