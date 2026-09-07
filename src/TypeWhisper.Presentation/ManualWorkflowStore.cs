using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Persists workflows using the shared Core schema without hiding load or write failures.</summary>
/// <param name="path">Workflow JSON file to read and update.</param>
/// <param name="write">Optional atomic snapshot writer; false reports a failed write. The default uses the Core workflow service.</param>
public sealed class ManualWorkflowStore(string path, Func<IReadOnlyList<Workflow>, bool>? write = null)
{
    private readonly Func<IReadOnlyList<Workflow>, bool> _write = write ?? (items => new WorkflowService(path).TryReplaceAll(items));
    private static readonly object MutationLock = new();
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Identifies workflows whose semantics this manual editor supports.</summary>
    public static bool IsSupported(Workflow workflow) => Enum.IsDefined(workflow.Template)
        && workflow.Trigger.Kind == WorkflowTriggerKind.Manual && workflow.Behavior.Settings.Count == 0
        && string.IsNullOrWhiteSpace(workflow.Output.TargetActionPluginId);

    /// <summary>Identifies manual or supported App/Global workflows that the editor can preserve and execute.</summary>
    public static bool IsEditable(Workflow workflow) => IsSupported(workflow) ||
        (workflow.Trigger.Kind is WorkflowTriggerKind.App or WorkflowTriggerKind.Global
            && (workflow.Trigger.Kind != WorkflowTriggerKind.App || workflow.Trigger.ProcessNames.Count > 0)
            && AutomaticWorkflowSnapshot.UnsupportedReason(workflow) is null);

    /// <summary>Reads the current snapshot, preserving workflows outside the manual editor.</summary>
    public IReadOnlyList<Workflow> Read()
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new IOException("The workflow file is a directory.");
            return [];
        }
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement);
        var items = JsonSerializer.Deserialize<List<Workflow>>(json, Options)
            ?? throw new JsonException("The workflow list is empty or invalid.");
        if (items.Any(w => w is null || string.IsNullOrWhiteSpace(w.Id) || string.IsNullOrWhiteSpace(w.Name)
            || w.Behavior is null || w.Behavior.Settings is null || w.Behavior.FineTuning is null || w.Trigger is null || w.Output is null)
            || items.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new JsonException("The workflow list contains invalid or duplicate entries.");
        return items.OrderBy(w => w.SortOrder).ToArray();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate workflow property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    /// <summary>Writes one manual workflow atomically; callers keep drafts when this throws.</summary>
    public void Save(Workflow workflow, bool allowAutomatic = false)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (!(allowAutomatic ? IsEditable(workflow) : IsSupported(workflow))) throw new InvalidOperationException("This workflow cannot be edited here.");
        if (string.IsNullOrWhiteSpace(workflow.Id) || string.IsNullOrWhiteSpace(workflow.Name))
            throw new ArgumentException("A workflow name is required.", nameof(workflow));
        if (workflow.Template == WorkflowTemplate.Custom && string.IsNullOrWhiteSpace(workflow.Behavior.FineTuning))
            throw new ArgumentException("Custom workflows require instructions.", nameof(workflow));
        lock (MutationLock)
        {
            var items = Read().ToList();
            int index = items.FindIndex(w => w.Id == workflow.Id);
            if (index >= 0 && !(allowAutomatic ? IsEditable(items[index]) : IsSupported(items[index])))
                throw new InvalidOperationException("This workflow has changed and can no longer be edited here.");
            var updated = workflow with { UpdatedAt = DateTime.UtcNow };
            if (index < 0) items.Add(updated); else items[index] = updated;
            if (!_write(items.AsReadOnly()))
                throw new IOException("Workflow changes could not be saved.");
        }
    }

    /// <summary>Deletes a supported manual workflow after the caller obtains confirmation.</summary>
    public void Delete(string id, bool allowAutomatic = false)
    {
        lock (MutationLock)
        {
            var items = Read().ToList();
            var current = items.FirstOrDefault(w => w.Id == id)
                ?? throw new InvalidOperationException("This workflow no longer exists.");
            if (!(allowAutomatic ? IsEditable(current) : IsSupported(current))) throw new InvalidOperationException("This workflow cannot be deleted here.");
            items.Remove(current);
            if (!_write(items.AsReadOnly()))
                throw new IOException("The workflow could not be deleted.");
        }
    }
}

/// <summary>Runs the exact configured provider and model; no fallback or sample transformation is permitted.</summary>
public static class ManualWorkflowRunner
{
    /// <summary>Processes source text, rejecting unavailable choices and late results after cancellation.</summary>
    public static async Task<string> RunAsync(Workflow workflow, string input,
        Func<string, string, bool> available,
        Func<string, string, string, string, CancellationToken, Task<string>> process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!workflow.IsEnabled) throw new InvalidOperationException("This workflow is disabled.");
        var provider = workflow.Behavior.ProviderOverride;
        var model = workflow.Behavior.ModelOverride;
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("Enter source text first.");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || !available(provider, model))
            throw new InvalidOperationException("The saved provider or model is unavailable. Configure the workflow before running it.");
        var prompt = workflow.SystemPrompt();
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("This workflow has no instructions.");
        var result = await process(provider, prompt, input, model, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("The provider returned an empty result. Your source text is unchanged.");
        return result;
    }
}
