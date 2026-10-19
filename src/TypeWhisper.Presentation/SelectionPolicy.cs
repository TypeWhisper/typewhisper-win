using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>What the transcription selection policy remembers across launches.</summary>
/// <param name="Retained">
/// Per registry provider whose own model selection an enforced pair replaced: that provider's model before
/// enforcement, selected again once no pair is enforced.
/// </param>
public sealed record SelectionPolicyPreferences(IReadOnlyDictionary<string, string>? Retained = null)
{
    /// <summary>The retained model of a provider, or null.</summary>
    public string? RetainedModel(string providerId) => Retained?.GetValueOrDefault(providerId);

    /// <summary>A copy that also retains the provider's model.</summary>
    public SelectionPolicyPreferences With(string providerId, string modelId) =>
        new(new Dictionary<string, string>(Retained ?? new Dictionary<string, string>()) { [providerId] = modelId });

    /// <summary>A copy without the provider's retained model.</summary>
    public SelectionPolicyPreferences Without(string providerId)
    {
        var next = new Dictionary<string, string>(Retained ?? new Dictionary<string, string>());
        next.Remove(providerId);
        return new(next.Count == 0 ? null : next);
    }
}

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

    /// <summary>Saves atomically; <see cref="Current"/> changes only once the write succeeded.</summary>
    public bool Save(SelectionPolicyPreferences next)
    {
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(next));
            Current = next;
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
