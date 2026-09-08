using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>A provider/model pair inherited only by workflows explicitly using the default.</summary>
public sealed record WorkflowLlmSelection(string Provider, string Model);

/// <summary>Stores a local default without changing any workflow's own selection.</summary>
public sealed class WorkflowLlmDefaults(string path)
{
    /// <summary>Explicit inheritance marker; absent legacy selections retain their meaning.</summary>
    public const string Inherit = "__default__";
    /// <summary>Reads the persisted pair, reporting malformed data.</summary>
    public WorkflowLlmSelection? Read()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Default LLM settings are too large.");
        var value = JsonSerializer.Deserialize<WorkflowLlmSelection>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Default LLM settings are invalid.");
        Validate(value);
        return value;
    }
    /// <summary>Atomically replaces the default pair after validation.</summary>
    public void Save(WorkflowLlmSelection value)
    {
        Validate(value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Resolves inheritance once without modifying the saved workflow.</summary>
    public Workflow Resolve(Workflow workflow) => workflow.Behavior.ProviderOverride != Inherit ? workflow
        : Apply(workflow, Read());
    /// <summary>Applies a captured pair only to an explicitly inheriting workflow.</summary>
    public static Workflow Apply(Workflow workflow, WorkflowLlmSelection? defaults) => workflow.Behavior.ProviderOverride != Inherit ? workflow
        : workflow with { Behavior = workflow.Behavior with { ProviderOverride = defaults?.Provider, ModelOverride = defaults?.Model } };
    private static void Validate(WorkflowLlmSelection value)
    {
        if (string.IsNullOrWhiteSpace(value.Provider) || value.Provider is Inherit or "none"
            || string.IsNullOrWhiteSpace(value.Model) || value.Provider.Length > 1024 || value.Model.Length > 1024
            || value.Provider.Any(char.IsControl) || value.Model.Any(char.IsControl))
            throw new InvalidDataException("Choose a default LLM provider and model.");
    }
}
