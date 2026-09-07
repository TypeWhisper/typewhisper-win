using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Presentation;

/// <summary>Setup navigation progress; actual preferences remain in their existing stores.</summary>
public sealed record SetupPreferences
{
    /// <summary>The last successfully saved setup step.</summary>
    [JsonRequired]
    public int Step { get; init; }
    /// <summary>Whether the user finished setup with a ready configuration, not whether audio was tested.</summary>
    [JsonRequired]
    public bool Completed { get; init; }
}

/// <summary>Atomically persists setup progress in an explicit profile.</summary>
public sealed class SetupPreferencesStore
{
    private readonly string _path;
    /// <summary>The loaded or last successfully saved progress.</summary>
    public SetupPreferences Current { get; private set; } = new();
    /// <summary>A readable persistence failure.</summary>
    public string? Error { get; private set; }
    /// <summary>Loads progress without creating files.</summary>
    public SetupPreferencesStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path))
            {
                if (Directory.Exists(path)) throw new IOException("Setup path is a directory.");
                return;
            }
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new JsonException("Duplicate or invalid setup properties.");
            var loaded = JsonSerializer.Deserialize<SetupPreferences>(json,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (loaded is null || loaded.Step is < 0 or > 4 || (loaded.Completed && loaded.Step != 4)) throw new JsonException("Invalid setup progress.");
            Current = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = "Setup progress could not be loaded. Your actual settings are unchanged."; }
    }
    /// <summary>Saves progress without publishing in-memory success until the file is replaced.</summary>
    public string? Save(int step, bool completed = false)
    {
        if (step is < 0 or > 4 || (completed && step != 4)) throw new ArgumentOutOfRangeException(nameof(step));
        var next = new SetupPreferences { Step = step, Completed = completed };
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _path, true);
            Current = next; Error = null; return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Error = "Setup progress could not be saved. Your actual settings remain saved separately."; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
