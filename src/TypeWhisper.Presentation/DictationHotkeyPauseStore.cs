using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Loads and atomically saves the dictation hotkey pause choice in an explicitly supplied profile.</summary>
public sealed class DictationHotkeyPauseStore
{
    private readonly string _path;
    /// <summary>The loaded or last successfully saved dictation hotkey pause choice.</summary>
    public bool Current { get; private set; }
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads preferences without writing; missing or invalid settings leave hotkeys active.</summary>
    public DictationHotkeyPauseStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count(property => property.Name == "Paused") != 1 ||
                !root.TryGetProperty("Paused", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Invalid pause preference.");
            var paused = value.GetBoolean();
            Current = paused;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "Dictation hotkey pause choice could not be loaded. Hotkeys are active. Save the pause choice again.";
        }
    }

    /// <summary>Persists the pause choice; failed writes preserve the previous selection.</summary>
    public string? Save(bool paused)
    {
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Paused = paused }));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = paused;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Dictation hotkey pause choice could not be saved. Your previous choice still applies.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
