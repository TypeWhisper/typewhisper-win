using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>What the transcription selection policy remembers across launches.</summary>
/// <param name="RetainedProviderId">The registry provider whose own model selection an enforced pair replaced, or null.</param>
/// <param name="RetainedModelId">That provider's model before enforcement; selected again once the policy is removed.</param>
public sealed record SelectionPolicyPreferences(string? RetainedProviderId = null, string? RetainedModelId = null);

/// <summary>
/// Atomically persists <see cref="SelectionPolicyPreferences"/>; an unreadable file behaves like an empty one. The
/// dictation provider and the local engine's model are never written here because the app keeps the user's own
/// values in their stores and only overlays the enforced pair in memory.
/// </summary>
public sealed class SelectionPolicyStore
{
    private readonly string _path;

    /// <summary>The loaded or last saved preferences.</summary>
    public SelectionPolicyPreferences Current { get; private set; } = new();

    /// <summary>A readable persistence failure.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads the preferences without creating files.</summary>
    public SelectionPolicyStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                Current = JsonSerializer.Deserialize<SelectionPolicyPreferences>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = Loc.T("The selection policy state could not be loaded."); }
    }

    /// <summary>Saves atomically; a failed write keeps the in-memory state for this session.</summary>
    public bool Save(SelectionPolicyPreferences next)
    {
        Current = next;
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(next));
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.T("The selection policy state could not be saved.");
            return false;
        }
    }
}
