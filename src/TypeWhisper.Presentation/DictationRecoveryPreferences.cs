using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Explicit permission to persist recovery audio, independent of transcript history.</summary>
public sealed record DictationRecoveryPreferences
{
    /// <summary>Whether new dictations may write recovery audio. Defaults to off.</summary>
    public bool Enabled { get; init; }
    /// <summary>Retention after opting in, in days; zero means no automatic expiry.</summary>
    public int RetentionDays { get; init; } = 30;
    /// <summary>Whether this is a supported retention choice.</summary>
    public bool IsValid => RetentionDays is 0 or 1 or 7 or 30 or 60 or 90 or 180;
    /// <summary>Whether an in-flight recording still has permission to preserve audio.</summary>
    public bool CanPreserveWith(DictationRecoveryPreferences current) =>
        Enabled && IsValid && current.Enabled && current.IsValid;
}

/// <summary>Versioned, bounded preferences. Construction never creates files or enables audio storage.</summary>
public sealed class DictationRecoveryPreferencesStore
{
    private readonly string _path;
    /// <summary>Loaded or successfully saved preferences; invalid files default to disabled.</summary>
    public DictationRecoveryPreferences Current { get; private set; } = new();
    /// <summary>A visible load/write error, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Reads only the explicitly supplied profile file.</summary>
    public DictationRecoveryPreferencesStore(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > 4096) throw new JsonException();
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("Version" or "Enabled" or "RetentionDays")) throw new JsonException();
            if (root.GetProperty("Version").GetInt32() != 1) throw new JsonException();
            var next = new DictationRecoveryPreferences
            { Enabled = root.GetProperty("Enabled").GetBoolean(), RetentionDays = root.GetProperty("RetentionDays").GetInt32() };
            if (!next.IsValid) throw new JsonException();
            Current = next;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { Error = "Recovery preferences could not be loaded. New recovery audio is disabled; existing audio has not been deleted."; }
    }

    /// <summary>Persists one explicit choice atomically. This does not delete or change existing audio.</summary>
    public bool Save(DictationRecoveryPreferences next)
    {
        if (!next.IsValid) { Error = "Choose a supported recovery retention period."; return false; }
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { Version = 1, next.Enabled, next.RetentionDays });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = next; Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "Recovery preferences could not be saved. The previous choice still applies."; return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
